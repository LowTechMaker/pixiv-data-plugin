using SceneGallery.PluginSdk;

namespace SceneGallery.Plugin.PixivAuthors;

internal sealed class PixivFetchCoordinator
{
    private readonly PixivApiClient _client;
    private readonly AuthorDiskCache _cache;
    private readonly ArtworkDiskCache _artworkCache;
    private readonly Action<string> _log;
    private readonly string _avatarDirectory;
    private readonly PluginOperationDrain _operations;
    private readonly CancellationToken _lifetimeToken;
    private readonly Func<Task>? _beforePreviewPromotion;
    private readonly Func<string> _pixivLanguage;

    // Dedupes concurrent fetches: 50 cards of one author trigger one request.
    private readonly object _authorGate = new();
    private readonly Dictionary<string, AuthorOperation> _inFlight = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _authorGenerations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Lazy<Task<ArtworkRefreshResult>>> _artworkInFlight = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ArtworkDiskCache.CachedArtwork> _unsavedArtworkDetails = new(StringComparer.Ordinal);

    public PixivFetchCoordinator(
        string storageDirectory,
        Action<string> log,
        PixivApiClient client,
        PluginOperationDrain operations,
        CancellationToken lifetimeToken,
        Func<Task>? beforePreviewPromotion = null,
        Func<string>? pixivLanguage = null)
    {
        _log = log;
        _avatarDirectory = Path.Combine(storageDirectory, "avatars");
        _cache = new AuthorDiskCache(storageDirectory, log);
        _artworkCache = new ArtworkDiskCache(storageDirectory, log);
        _client = client;
        _operations = operations;
        _lifetimeToken = lifetimeToken;
        _beforePreviewPromotion = beforePreviewPromotion;
        // Read per request rather than captured: it is the host's answer, and
        // asking each time keeps the plugin free of any idea of when it changes.
        _pixivLanguage = pixivLanguage ?? (() => PixivTagLanguage.Default);
    }

    internal static string GetProfileUrl(AuthorKey key)
        => $"https://www.pixiv.net/users/{key.Id}";

    public Task<AuthorInfo?> GetAuthorInfoAsync(
        AuthorKey key,
        bool forceRefresh,
        CancellationToken ct)
    {
        if (key.ProviderId != PixivFolderNameParser.ProviderId)
            return Task.FromResult<AuthorInfo?>(null);

        AuthorOperation operation;
        lock (_authorGate)
        {
            if (_operations.IsDisposing)
                return Task.FromException<AuthorInfo?>(new ObjectDisposedException(nameof(PixivFetchCoordinator)));
            if (!forceRefresh && _cache.TryGet(key.Id, out var cached))
                return Task.FromResult(ToAuthorInfo(key, cached));

            if (forceRefresh || !_inFlight.TryGetValue(key.Id, out operation!))
            {
                var generation = _authorGenerations.GetValueOrDefault(key.Id) + 1;
                _authorGenerations[key.Id] = generation;
                operation = new AuthorOperation(generation);
                var selected = operation;
                operation.Fetch = new Lazy<Task<AuthorInfo?>>(
                    () => _operations.RunProducerAsync(() => FetchAndCacheAsync(key, selected, _lifetimeToken)));
                _inFlight[key.Id] = operation;
            }
        }
        return operation.Fetch.Value.WaitAsync(ct);
    }

    private async Task<AuthorInfo?> FetchAndCacheAsync(AuthorKey key, AuthorOperation operation, CancellationToken ct)
    {
        try
        {
            var user = await _client.FetchUserAsync(key.Id, ct).ConfigureAwait(false);
            if (user.Status == PixivApiClient.UserFetchStatus.NotFound)
            {
                // Deleted/private account: negative-cache so we don't re-query
                // a dead id every launch (the cache applies a TTL to these).
                lock (_authorGate)
                {
                    if (IsCurrent(key.Id, operation))
                        _cache.Set(key.Id, new AuthorDiskCache.CachedAuthor(null, null, DateTimeOffset.UtcNow, Failed: true));
                }
                return null;
            }
            if (user.Status == PixivApiClient.UserFetchStatus.SchemaError)
                return null;

            PixivAvatarDownload? avatar = null;
            if (user.AvatarUrl is { } avatarUrl && !IsDefaultAvatar(avatarUrl))
            {
                avatar = await _client.StageAvatarAsync(
                    avatarUrl, Path.Combine(_avatarDirectory, key.Id), ct).ConfigureAwait(false);
            }
            using (avatar)
            lock (_authorGate)
            {
                // Publishing the image and cache entry is one generation-guarded commit.
                // An older request can finish for its caller but cannot alter newer state.
                var current = IsCurrent(key.Id, operation);
                var avatarFile = current ? avatar?.Publish() : null;
                var entry = new AuthorDiskCache.CachedAuthor(
                    user.Name!, avatarFile, DateTimeOffset.UtcNow, Failed: false);
                if (current)
                    _cache.Set(key.Id, entry);
                return ToAuthorInfo(key, entry);
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            // Transport failure (offline, blocked): do NOT negative-cache —
            // next launch should try again.
            _log($"fetch failed for pixiv user {key.Id}: {ex.Message}");
            return null;
        }
        finally
        {
            lock (_authorGate)
            {
                if (_inFlight.TryGetValue(key.Id, out var registered) && ReferenceEquals(registered, operation))
                    _inFlight.Remove(key.Id);
            }
        }
    }

    private bool IsCurrent(string key, AuthorOperation operation)
        => _authorGenerations.GetValueOrDefault(key) == operation.Generation;

    private sealed class AuthorOperation(long generation)
    {
        internal long Generation { get; } = generation;
        internal Lazy<Task<AuthorInfo?>> Fetch { get; set; } = null!;
    }

    private static AuthorInfo? ToAuthorInfo(AuthorKey key, AuthorDiskCache.CachedAuthor entry)
    {
        if (entry.Failed || entry.Name is null) return null;
        var avatar = entry.AvatarFile is { } file && File.Exists(file) ? file : null;
        return new AuthorInfo(key, entry.Name, avatar, GetProfileUrl(key), entry.FetchedAt);
    }

    // pixiv serves a placeholder image for users without an avatar; skip
    // downloading it so the UI falls back to its own person glyph.
    private static bool IsDefaultAvatar(string url) => url.Contains("no_profile", StringComparison.Ordinal);

    public Task<ArtworkInfo?> FetchArtworkInfoAsync(
        ArtworkId id,
        CancellationToken ct,
        bool saveToLocalCache = true)
    {
        if (id.ProviderId != PixivFolderNameParser.ProviderId)
            return Task.FromResult<ArtworkInfo?>(null);

        Lazy<Task<ArtworkRefreshResult>>? lazy = null;
        bool promote;
        lock (_authorGate)
        {
            if (_operations.IsDisposing)
                return Task.FromException<ArtworkInfo?>(new ObjectDisposedException(nameof(PixivFetchCoordinator)));
            // Old entries without titles need refetching for artwork folder names.
            if (_artworkCache.TryGet(id.Id, out var cached) && cached.Title != null)
                return Task.FromResult(ToArtworkInfo(id, cached, isSavedLocally: true));
            promote = saveToLocalCache && _unsavedArtworkDetails.ContainsKey(id.Id);
            if (!promote)
            {
                var inFlightKey = $"{id.Id}:{saveToLocalCache}";
                if (!_artworkInFlight.TryGetValue(inFlightKey, out lazy))
                {
                    Lazy<Task<ArtworkRefreshResult>> selected = null!;
                    selected = new Lazy<Task<ArtworkRefreshResult>>(() => _operations.RunProducerAsync(
                        () => FetchArtworkAsync(id, saveToLocalCache, inFlightKey, selected, _lifetimeToken)));
                    lazy = selected;
                    _artworkInFlight[inFlightKey] = lazy;
                }
            }
        }
        // Admission and producer completion run outside the state lock. Completion may
        // trigger deferred persistence, so it must never re-enter while this lock is held.
        return (promote ? PromotePreviewAsync(id) : InfoOf(lazy!.Value)).WaitAsync(ct);
    }

    private static async Task<ArtworkInfo?> InfoOf(Task<ArtworkRefreshResult> result)
        => (await result.ConfigureAwait(false)).Info;

    /// <summary>
    /// Fetches <paramref name="id"/> from pixiv whatever the cache holds, and
    /// replaces the cached copy with the answer. A failed or missing answer
    /// leaves the cache as it was: a re-fetch that cannot reach pixiv must not
    /// throw away what an earlier import did get.
    /// </summary>
    /// <remarks>
    /// Its own in-flight key, so a re-fetch never settles for a cached answer
    /// an ordinary lookup of the same artwork happens to be returning.
    /// </remarks>
    public Task<ArtworkRefreshResult> RefreshArtworkAsync(ArtworkId id, CancellationToken ct)
    {
        if (id.ProviderId != PixivFolderNameParser.ProviderId)
            return Task.FromResult(ArtworkRefreshResult.Failed);

        Lazy<Task<ArtworkRefreshResult>>? lazy;
        lock (_authorGate)
        {
            if (_operations.IsDisposing)
                return Task.FromException<ArtworkRefreshResult>(new ObjectDisposedException(nameof(PixivFetchCoordinator)));
            var inFlightKey = $"{id.Id}:refresh";
            if (!_artworkInFlight.TryGetValue(inFlightKey, out lazy))
            {
                Lazy<Task<ArtworkRefreshResult>> selected = null!;
                selected = new Lazy<Task<ArtworkRefreshResult>>(() => _operations.RunProducerAsync(
                    () => FetchArtworkAsync(id, saveToLocalCache: true, inFlightKey, selected, _lifetimeToken)));
                lazy = selected;
                _artworkInFlight[inFlightKey] = lazy;
            }
        }
        return lazy.Value.WaitAsync(ct);
    }

    private Task<ArtworkInfo?> PromotePreviewAsync(ArtworkId id)
        => _operations.RunProducerAsync(async () =>
        {
            // Optional scheduling seam for deterministic shutdown-vs-commit tests.
            if (_beforePreviewPromotion is not null)
                await _beforePreviewPromotion().ConfigureAwait(false);
            lock (_authorGate)
            {
                if (_artworkCache.TryGet(id.Id, out var cached) && cached.Title is not null)
                    return ToArtworkInfo(id, cached, isSavedLocally: true);
                if (!_unsavedArtworkDetails.Remove(id.Id, out var unsaved))
                    return null;
                _artworkCache.Set(id.Id, unsaved);
                return ToArtworkInfo(id, unsaved, isSavedLocally: true);
            }
        });

    /// <remarks>
    /// Null from the client means pixiv said the artwork is not there; every
    /// other problem arrives as an exception. The two stay apart all the way to
    /// the host, which stops a run on a streak of failures but must not count
    /// deleted artworks as those.
    /// </remarks>
    private async Task<ArtworkRefreshResult> FetchArtworkAsync(
        ArtworkId id,
        bool saveToLocalCache,
        string inFlightKey,
        Lazy<Task<ArtworkRefreshResult>> operation,
        CancellationToken ct)
    {
        try
        {
            var data = await _client.FetchArtworkAsync(id.Id, _pixivLanguage(), ct).ConfigureAwait(false);
            if (data is null)
                return ArtworkRefreshResult.Gone;

            var tags = data.Value.Tags
                .Select(t => new ArtworkDiskCache.CachedTag(t.Tag, t.Translation))
                .ToList();

            var entry = new ArtworkDiskCache.CachedArtwork(
                data.Value.UserName, data.Value.UserId, data.Value.Title, data.Value.Description,
                data.Value.XRestrict, tags, DateTimeOffset.UtcNow, Failed: false);
            lock (_authorGate)
            {
                if (saveToLocalCache)
                {
                    _artworkCache.Set(id.Id, entry);
                    _unsavedArtworkDetails.Remove(id.Id);
                }
                else
                {
                    _unsavedArtworkDetails[id.Id] = entry;
                }
            }
            return ToArtworkInfo(id, entry, saveToLocalCache) is { } info
                ? ArtworkRefreshResult.Found(info)
                : ArtworkRefreshResult.Failed;
        }
        catch (OperationCanceledException)
        {
            return ArtworkRefreshResult.Failed;
        }
        catch (Exception ex)
        {
            _log($"fetch failed for pixiv artwork {id.Id}: {ex.Message}");
            return ArtworkRefreshResult.Failed;
        }
        finally
        {
            lock (_authorGate)
            {
                if (_artworkInFlight.TryGetValue(inFlightKey, out var registered) && ReferenceEquals(registered, operation))
                    _artworkInFlight.Remove(inFlightKey);
            }
        }
    }

    private static ArtworkInfo? ToArtworkInfo(
        ArtworkId id,
        ArtworkDiskCache.CachedArtwork entry,
        bool isSavedLocally)
    {
        if (entry.Failed || entry.AuthorName is null || entry.AuthorId is null)
            return null;

        var tags = entry.Tags?
            .Select(t => new ArtworkTag(t.Name, t.TranslatedName))
            .ToList() as IReadOnlyList<ArtworkTag>
            ?? [];

        var rating = entry.XRestrict switch
        {
            1 => ContentRating.R18,
            2 => ContentRating.R18G,
            _ => ContentRating.AllAges,
        };

        return new ArtworkInfo(
            id,
            entry.AuthorName,
            entry.AuthorId,
            entry.Title,
            entry.Description,
            rating,
            tags,
            entry.FetchedAt,
            isSavedLocally);
    }

    internal void DisposeExecution() => _client.Dispose();

    internal void DisposePersistence()
    {
        lock (_authorGate)
        {
            _cache.Dispose();
            _artworkCache.Dispose();
        }
    }
}

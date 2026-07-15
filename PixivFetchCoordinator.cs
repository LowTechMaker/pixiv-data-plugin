using System.Collections.Concurrent;
using SceneGallery.PluginSdk;

namespace SceneGallery.Plugin.PixivAuthors;

internal sealed class PixivFetchCoordinator : IDisposable
{
    private readonly PixivApiClient _client;
    private readonly AuthorDiskCache _cache;
    private readonly ArtworkDiskCache _artworkCache;
    private readonly Action<string> _log;
    private readonly string _avatarDirectory;

    // Dedupes concurrent fetches: 50 cards of one author trigger one request.
    private readonly ConcurrentDictionary<string, Lazy<Task<AuthorInfo?>>> _inFlight = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<ArtworkInfo?>>> _artworkInFlight = new();
    private readonly ConcurrentDictionary<string, ArtworkDiskCache.CachedArtwork> _unsavedArtworkDetails = new();

    public PixivFetchCoordinator(
        string storageDirectory,
        Action<string> log,
        PixivApiClient client)
    {
        _log = log;
        _avatarDirectory = Path.Combine(storageDirectory, "avatars");
        _cache = new AuthorDiskCache(storageDirectory, log);
        _artworkCache = new ArtworkDiskCache(storageDirectory, log);
        _client = client;
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

        if (!forceRefresh && _cache.TryGet(key.Id, out var cached))
            return Task.FromResult(ToAuthorInfo(key, cached));

        // Force-refresh must bypass any in-flight non-force fetch.
        if (forceRefresh)
            _inFlight.TryRemove(key.Id, out _);

        var lazy = _inFlight.GetOrAdd(key.Id, _ => new Lazy<Task<AuthorInfo?>>(
            () => FetchAndCacheAsync(key, CancellationToken.None)));
        return lazy.Value.WaitAsync(ct);
    }

    private async Task<AuthorInfo?> FetchAndCacheAsync(AuthorKey key, CancellationToken ct)
    {
        try
        {
            var user = await _client.FetchUserAsync(key.Id, ct).ConfigureAwait(false);
            if (user.Status == PixivApiClient.UserFetchStatus.NotFound)
            {
                // Deleted/private account: negative-cache so we don't re-query
                // a dead id every launch (the cache applies a TTL to these).
                _cache.Set(key.Id, new AuthorDiskCache.CachedAuthor(null, null, DateTimeOffset.UtcNow, Failed: true));
                return null;
            }
            if (user.Status == PixivApiClient.UserFetchStatus.SchemaError)
                return null;

            string? avatarFile = null;
            if (user.AvatarUrl is { } avatarUrl && !IsDefaultAvatar(avatarUrl))
            {
                avatarFile = await _client.DownloadAvatarAsync(
                    avatarUrl, Path.Combine(_avatarDirectory, key.Id), ct).ConfigureAwait(false);
            }

            var entry = new AuthorDiskCache.CachedAuthor(
                user.Name!, avatarFile, DateTimeOffset.UtcNow, Failed: false);
            _cache.Set(key.Id, entry);
            return ToAuthorInfo(key, entry);
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
            _inFlight.TryRemove(key.Id, out _);
        }
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

        // Title was added after initial release; old cache entries have Title == null.
        // Re-fetch those so the artwork subfolder feature works correctly.
        if (_artworkCache.TryGet(id.Id, out var cached) && cached.Title != null)
            return Task.FromResult(ToArtworkInfo(id, cached, isSavedLocally: true));

        if (saveToLocalCache && _unsavedArtworkDetails.TryRemove(id.Id, out var unsaved))
        {
            _artworkCache.Set(id.Id, unsaved);
            return Task.FromResult(ToArtworkInfo(id, unsaved, isSavedLocally: true));
        }

        var inFlightKey = $"{id.Id}:{saveToLocalCache}";
        var lazy = _artworkInFlight.GetOrAdd(inFlightKey, _ => new Lazy<Task<ArtworkInfo?>>(
            () => FetchArtworkAsync(id, saveToLocalCache, CancellationToken.None)));
        return lazy.Value.WaitAsync(ct);
    }

    private async Task<ArtworkInfo?> FetchArtworkAsync(
        ArtworkId id,
        bool saveToLocalCache,
        CancellationToken ct)
    {
        try
        {
            var data = await _client.FetchArtworkAsync(id.Id, ct).ConfigureAwait(false);
            if (data is null)
                return null;

            var tags = data.Value.Tags
                .Select(t => new ArtworkDiskCache.CachedTag(t.Tag, t.Translation))
                .ToList();

            var entry = new ArtworkDiskCache.CachedArtwork(
                data.Value.UserName, data.Value.UserId, data.Value.Title, data.Value.Description,
                data.Value.XRestrict, tags, DateTimeOffset.UtcNow, Failed: false);
            if (saveToLocalCache)
            {
                _artworkCache.Set(id.Id, entry);
                _unsavedArtworkDetails.TryRemove(id.Id, out _);
            }
            else
            {
                _unsavedArtworkDetails[id.Id] = entry;
            }
            return ToArtworkInfo(id, entry, saveToLocalCache);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _log($"fetch failed for pixiv artwork {id.Id}: {ex.Message}");
            return null;
        }
        finally
        {
            _artworkInFlight.TryRemove($"{id.Id}:{saveToLocalCache}", out _);
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

    public void Dispose()
    {
        _cache.Dispose();
        _artworkCache.Dispose();
        _client.Dispose();
    }
}

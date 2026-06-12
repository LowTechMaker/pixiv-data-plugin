using System.Collections.Concurrent;
using SceneGallery.PluginSdk;

namespace SceneGallery.Plugin.PixivAuthors;

/// <summary>
/// Resolves pixiv author info for folders named like "ArtistName (12345678)".
/// Anonymous web requests only (no account); rate-limited and disk-cached so
/// each author is fetched at most once until a forced refresh.
/// </summary>
public sealed class PixivAuthorPlugin : IFolderAuthorProvider, ICardImportProvider, IReverseImageSearchProvider, IDisposable
{
    private static readonly TimeSpan MinRequestInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxJitter = TimeSpan.FromSeconds(3);

    private IPluginHost? _host;
    private PixivApiClient? _client;
    private SauceNaoClient? _sauceNaoClient;
    private AuthorDiskCache? _cache;
    private ArtworkDiskCache? _artworkCache;
    private string _avatarDirectory = "";

    // Dedupes concurrent fetches: 50 cards of one author trigger one request.
    private readonly ConcurrentDictionary<string, Lazy<Task<AuthorInfo?>>> _inFlight = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<ArtworkInfo?>>> _artworkInFlight = new();
    private readonly ConcurrentDictionary<string, ArtworkDiskCache.CachedArtwork> _unsavedArtworkDetails = new();

    public string Name => "Pixiv Authors";

    public string Version => typeof(PixivAuthorPlugin).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public void Initialize(IPluginHost host)
    {
        _host = host;
        _avatarDirectory = Path.Combine(host.StorageDirectory, "avatars");
        _cache = new AuthorDiskCache(host.StorageDirectory, host.Log);
        _artworkCache = new ArtworkDiskCache(host.StorageDirectory, host.Log);
        _client = new PixivApiClient(new RateLimiter(MinRequestInterval, MaxJitter), host.Log);
        _sauceNaoClient = new SauceNaoClient(new RateLimiter(MinRequestInterval, MaxJitter), host.Log);
    }

    public ParsedAuthor? TryParseFolderName(string folderName)
        => PixivFolderNameParser.TryParse(folderName);

    public string GetProfileUrl(AuthorKey key) => $"https://www.pixiv.net/users/{key.Id}";

    public Task<AuthorInfo?> GetAuthorInfoAsync(AuthorKey key, bool forceRefresh, CancellationToken ct)
    {
        if (_client is null || _cache is null || key.ProviderId != PixivFolderNameParser.ProviderId)
            return Task.FromResult<AuthorInfo?>(null);

        if (!forceRefresh && _cache.TryGet(key.Id, out var cached))
            return Task.FromResult(ToAuthorInfo(key, cached));

        // Force-refresh must bypass any in-flight non-force fetch.
        if (forceRefresh)
            _inFlight.TryRemove(key.Id, out _);

        var lazy = _inFlight.GetOrAdd(key.Id, _ => new Lazy<Task<AuthorInfo?>>(
            () => FetchAndCacheAsync(key, ct)));
        return lazy.Value;
    }

    private async Task<AuthorInfo?> FetchAndCacheAsync(AuthorKey key, CancellationToken ct)
    {
        try
        {
            var user = await _client!.FetchUserAsync(key.Id, ct).ConfigureAwait(false);
            if (user is null)
            {
                // Deleted/private account: negative-cache so we don't re-query
                // a dead id every launch (the cache applies a TTL to these).
                _cache!.Set(key.Id, new AuthorDiskCache.CachedAuthor(null, null, DateTimeOffset.UtcNow, Failed: true));
                return null;
            }

            string? avatarFile = null;
            if (user.Value.AvatarUrl is { } avatarUrl && !IsDefaultAvatar(avatarUrl))
            {
                avatarFile = await _client.DownloadAvatarAsync(
                    avatarUrl, Path.Combine(_avatarDirectory, key.Id), ct).ConfigureAwait(false);
            }

            var entry = new AuthorDiskCache.CachedAuthor(
                user.Value.Name, avatarFile, DateTimeOffset.UtcNow, Failed: false);
            _cache!.Set(key.Id, entry);
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
            _host?.Log($"fetch failed for pixiv user {key.Id}: {ex.Message}");
            return null;
        }
        finally
        {
            _inFlight.TryRemove(key.Id, out _);
        }
    }

    private AuthorInfo? ToAuthorInfo(AuthorKey key, AuthorDiskCache.CachedAuthor entry)
    {
        if (entry.Failed || entry.Name is null) return null;
        var avatar = entry.AvatarFile is { } file && File.Exists(file) ? file : null;
        return new AuthorInfo(key, entry.Name, avatar, GetProfileUrl(key), entry.FetchedAt);
    }

    // pixiv serves a placeholder image for users without an avatar; skip
    // downloading it so the UI falls back to its own person glyph.
    private static bool IsDefaultAvatar(string url) => url.Contains("no_profile", StringComparison.Ordinal);

    // ── ICardImportProvider ──────────────────────────────────────────

    public string ProviderId => PixivFolderNameParser.ProviderId;

    public ArtworkId? TryParseFilename(string fileName)
        => PixivFilenameParser.TryParse(fileName);

    public ArtworkId? TryParseArtworkFolderName(string folderName)
    {
        var parsed = PixivFolderNameParser.TryParse(folderName);
        if (parsed is null) return null;
        return new ArtworkId(ProviderId, parsed.Key.Id);
    }

    public string GetArtworkUrl(ArtworkId id) => $"https://www.pixiv.net/artworks/{id.Id}";

    public async Task<ReverseImageSearchResult?> SearchImageAsync(
        string imagePath,
        string apiKey,
        CancellationToken ct)
    {
        if (_sauceNaoClient is null)
            return null;

        try
        {
            var result = await _sauceNaoClient.SearchPixivAsync(imagePath, apiKey, ct).ConfigureAwait(false);
            if (result is null)
                return null;

            return new ReverseImageSearchResult(
                "SauceNao",
                new ArtworkId(ProviderId, result.PixivId),
                result.Title,
                result.AuthorName,
                result.AuthorId,
                result.Similarity,
                result.ThumbnailUrl,
                result.SourceUrl);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _host?.Log($"SauceNao search failed for {Path.GetFileName(imagePath)}: {ex.Message}");
            return null;
        }
    }

    public Task<ArtworkInfo?> FetchArtworkInfoAsync(
        ArtworkId id,
        CancellationToken ct,
        bool saveToLocalCache = true)
    {
        if (_client is null || _artworkCache is null || id.ProviderId != ProviderId)
            return Task.FromResult<ArtworkInfo?>(null);

        // Title was added after initial release; old cache entries have Title == null.
        // Re-fetch those so the artwork subfolder feature works correctly.
        if (_artworkCache.TryGet(id.Id, out var cached) && (cached.Failed || cached.Title != null))
            return Task.FromResult(ToArtworkInfo(id, cached, isSavedLocally: true));

        if (saveToLocalCache && _unsavedArtworkDetails.TryRemove(id.Id, out var unsaved))
        {
            _artworkCache.Set(id.Id, unsaved);
            return Task.FromResult(ToArtworkInfo(id, unsaved, isSavedLocally: true));
        }

        var inFlightKey = $"{id.Id}:{saveToLocalCache}";
        var lazy = _artworkInFlight.GetOrAdd(inFlightKey, _ => new Lazy<Task<ArtworkInfo?>>(
            () => FetchArtworkAsync(id, saveToLocalCache, ct)));
        return lazy.Value;
    }

    private async Task<ArtworkInfo?> FetchArtworkAsync(
        ArtworkId id,
        bool saveToLocalCache,
        CancellationToken ct)
    {
        try
        {
            var data = await _client!.FetchArtworkAsync(id.Id, ct).ConfigureAwait(false);
            if (data is null)
            {
                if (saveToLocalCache)
                {
                    _artworkCache!.Set(id.Id, new ArtworkDiskCache.CachedArtwork(
                        null, null, null, null, 0, null, DateTimeOffset.UtcNow, Failed: true));
                }
                return null;
            }

            var tags = data.Value.Tags
                .Select(t => new ArtworkDiskCache.CachedTag(t.Tag, t.Translation))
                .ToList();

            var entry = new ArtworkDiskCache.CachedArtwork(
                data.Value.UserName, data.Value.UserId, data.Value.Title, data.Value.Description,
                data.Value.XRestrict, tags, DateTimeOffset.UtcNow, Failed: false);
            if (saveToLocalCache)
            {
                _artworkCache!.Set(id.Id, entry);
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
            _host?.Log($"fetch failed for pixiv artwork {id.Id}: {ex.Message}");
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
        _cache?.Dispose();
        _artworkCache?.Dispose();
        _client?.Dispose();
        _sauceNaoClient?.Dispose();
    }
}

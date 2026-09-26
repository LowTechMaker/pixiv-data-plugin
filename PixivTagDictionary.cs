using System.Collections.Concurrent;
using SceneGallery.PluginSdk;

namespace SceneGallery.Plugin.PixivAuthors;

/// <summary>
/// Answers "what does this tag mean, and what is it called in my language"
/// from pixiv's tag search payload, which carries both.
/// </summary>
internal sealed class PixivTagDictionary : IDisposable
{
    private readonly PixivApiClient _client;
    private readonly TagDiskCache _cache;
    private readonly Action<string> _log;
    private readonly PluginOperationDrain _operations;
    private readonly CancellationToken _shutdownToken;
    private readonly ConcurrentDictionary<string, TagOperation> _inFlight = new(StringComparer.Ordinal);

    public PixivTagDictionary(string storageDirectory, Action<string> log, PixivApiClient client,
        PluginOperationDrain operations, CancellationToken shutdownToken)
        : this(new TagDiskCache(storageDirectory, log), log, client, operations, shutdownToken)
    {
    }

    internal PixivTagDictionary(TagDiskCache cache, Action<string> log, PixivApiClient client,
        PluginOperationDrain operations, CancellationToken shutdownToken)
    {
        _cache = cache;
        _log = log;
        _client = client;
        _operations = operations;
        _shutdownToken = shutdownToken;
    }

    /// <summary>Cache-only lookup, so a page can render before any network call.</summary>
    public TagArticle? TryGetCached(string tag, string bcp47)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        _cache.TryGet(tag, PixivTagLanguage.From(bcp47), out var article);
        return article;
    }

    public async Task<TagArticle?> FetchAsync(string tag, string bcp47, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var language = PixivTagLanguage.From(bcp47);
        if (_cache.TryGet(tag, language, out var cached)) return cached;

        // One shared fetch per (tag, language), and deliberately not started
        // with the caller's token: a cloud asks for sixty tags at once and the
        // first caller giving up must not cancel the answer everyone is waiting
        // for. Each caller waits on its own token instead.
        var key = TagDiskCache.KeyOf(tag, language);
        var shared = _inFlight.GetOrAdd(key, _ =>
        {
            var operation = new TagOperation();
            operation.Fetch = new Lazy<Task<TagArticle?>>(
                () => _operations.RunProducerAsync(() => FetchAndCacheAsync(tag, language, key, operation)),
                LazyThreadSafetyMode.ExecutionAndPublication);
            return operation;
        });
        return await shared.Fetch.Value.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<TagArticle?> FetchAndCacheAsync(string tag, string language, string key, TagOperation operation)
    {
        try
        {
            // A completed producer may have filled the cache after this caller's
            // initial miss but before its replacement operation was registered.
            if (_cache.TryGet(tag, language, out var cached)) return cached;

            string? json;
            try
            {
                json = await _client.FetchTagJsonAsync(tag, language, _shutdownToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Transport trouble is not evidence that the tag is unknown, so
                // nothing is written to the cache and the next visit retries.
                _log($"pixiv tag lookup failed for {tag}: {ex.GetType().Name}");
                return null;
            }

            if (json is null)
            {
                // A 404 is pixiv confirming it has no such tag. That is the one
                // answer worth remembering as an absence.
                _cache.Set(tag, language, null);
                return null;
            }

            var article = PixivTagParser.Parse(json, tag, language);
            if (article is null)
            {
                // Parsed nothing out of a 200: the payload drifted. Treating
                // that as "no such tag" would hide a schema change behind a
                // blank page for days, so it is not cached.
                _log($"pixiv tag payload for {tag} did not parse; schema may have changed");
                return null;
            }

            _cache.Set(tag, language, article);
            return article;
        }
        finally
        {
            ((ICollection<KeyValuePair<string, TagOperation>>)_inFlight).Remove(new(key, operation));
        }
    }

    private sealed class TagOperation
    {
        internal Lazy<Task<TagArticle?>> Fetch { get; set; } = null!;
    }

    internal void Flush() => _cache.Flush();

    public void Dispose() => _cache.Dispose();
}

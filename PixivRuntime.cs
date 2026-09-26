using SceneGallery.PluginSdk;

namespace SceneGallery.Plugin.PixivAuthors;

/// <summary>Owns producer admission, HTTP lifetime and the final cache flush.</summary>
internal sealed class PixivRuntime : IDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly PluginOperationDrain _operations;
    private readonly SauceNaoClient? _sauceNao;
    private readonly Action<string> _log;

    internal PixivRuntime(IPluginHost host, PixivApiClient client, SauceNaoClient? sauceNao, TimeSpan? disposeTimeout = null,
        Func<Task>? beforePreviewPromotion = null)
    {
        _sauceNao = sauceNao;
        _log = host.Log;
        _operations = new PluginOperationDrain(
            nameof(PixivRuntime), disposeTimeout ?? TimeSpan.FromSeconds(10),
            () => _shutdown.Cancel(),
            _ =>
            {
                try { Fetches.DisposeExecution(); }
                finally { _sauceNao?.Dispose(); }
                return Task.CompletedTask;
            },
            () =>
            {
                try { Fetches.DisposePersistence(); }
                finally
                {
                    try { Tags.Dispose(); }
                    finally { _shutdown.Dispose(); }
                }
            },
            pending => _log($"Pixiv shutdown timed out with {pending} producers; cache disposal is deferred."),
            error => _log($"Pixiv shutdown failed: {error.GetType().Name}"));
        Fetches = new PixivFetchCoordinator(host.StorageDirectory, host.Log, client, _operations, _shutdown.Token, beforePreviewPromotion,
            () => PixivTagLanguage.From(host.Language));
        Tags = new PixivTagDictionary(host.StorageDirectory, host.Log, client, _operations, _shutdown.Token);
    }

    // Drain callbacks run only after construction has installed this coordinator.
    internal PixivFetchCoordinator Fetches { get; } = null!;

    internal PixivTagDictionary Tags { get; } = null!;
    internal bool IsDisposing => _operations.IsDisposing;
    internal Task Completion => _operations.Completion;

    internal Task<TagArticle?> FetchTagAsync(string tag, string language, CancellationToken ct)
        => _operations.RunProducerAsync(async () =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
            return await Tags.FetchAsync(tag, language, linked.Token).ConfigureAwait(false);
        });

    internal Task<ReverseImageSearchResult?> SearchImageAsync(string imagePath, string apiKey, CancellationToken ct)
        => _operations.RunProducerAsync(async () =>
        {
            if (_sauceNao is null) return null;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
            try
            {
                var result = await _sauceNao.SearchPixivAsync(imagePath, apiKey, linked.Token).ConfigureAwait(false);
                return result is null ? null : new ReverseImageSearchResult(
                    "SauceNao", new ArtworkId(PixivFolderNameParser.ProviderId, result.PixivId),
                    result.Title, result.AuthorName, result.AuthorId, result.Similarity,
                    result.ThumbnailUrl, result.SourceUrl);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // HTTP diagnostics may contain the API-key query. Log the category only.
                _log($"SauceNao search failed for {Path.GetFileName(imagePath)}: {ex.GetType().Name}");
                return null;
            }
        });

    public void Dispose() => _operations.Dispose();
}

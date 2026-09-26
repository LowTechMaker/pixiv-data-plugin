using System.Net;
using System.Text;
using SceneGallery.PluginSdk;

namespace SceneGallery.Plugin.PixivAuthors.Tests;

public sealed class PixivLifetimeTests
{
    [Fact]
    public async Task Dispose_WithAdmittedPreviewPromotion_WaitsForCommitAndFlushesWithoutRefetch()
    {
        var storage = Path.Combine(Path.GetTempPath(), "pixiv-lifetime-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storage);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(new StringContent("""{"body":{"userId":"42","userName":"Artist","title":"Promoted","tags":{"tags":[]}}}"""));
        using var plugin = new PixivAuthorPlugin();
        plugin.InitializeForTests(new Host(storage), new PixivApiClient(new RateLimiter(TimeSpan.Zero), _ => { }, handler, []),
            disposeTimeout: TimeSpan.Zero, beforePreviewPromotion: () => { started.TrySetResult(); return release.Task; });
        var id = new ArtworkId("pixiv", "67890");
        var preview = await plugin.FetchArtworkInfoAsync(id, default, saveToLocalCache: false);
        Assert.False(preview!.IsSavedLocally);
        var promotion = plugin.FetchArtworkInfoAsync(id, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        plugin.Dispose();
        try { Assert.False(plugin.DisposalCompletion.IsCompleted); }
        finally { release.TrySetResult(); }
        Assert.True((await promotion)!.IsSavedLocally);
        await plugin.DisposalCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, handler.RequestCount);
        using (var reloaded = new ArtworkDiskCache(storage, _ => { }))
        {
            Assert.True(reloaded.TryGet(id.Id, out var entry));
            Assert.Equal("Promoted", entry.Title);
        }
        Directory.Delete(storage, recursive: true);
    }

    [Fact]
    public async Task Dispose_WithEncoderStillRunning_DefersPersistenceUntilProducerEnds()
    {
        var storage = Path.Combine(Path.GetTempPath(), "pixiv-lifetime-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storage);
        var image = Path.Combine(storage, "input.png");
        await File.WriteAllBytesAsync(image, [1]);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler();
        var sauce = new SauceNaoClient(new RateLimiter(TimeSpan.Zero), _ => { }, handler, async (_, _) =>
        {
            started.TrySetResult();
            await release.Task;
            return new MemoryStream([1]);
        });
        using var plugin = new PixivAuthorPlugin();
        plugin.InitializeForTests(new Host(storage),
            new PixivApiClient(new RateLimiter(TimeSpan.Zero), _ => { }, new Handler(), []), sauce, TimeSpan.Zero);
        var search = plugin.SearchImageAsync(image, "private-key", default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        plugin.Dispose();
        try
        {
            Assert.True(handler.Disposed);
            Assert.False(plugin.DisposalCompletion.IsCompleted);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => plugin.SearchImageAsync(image, "private-key", default));
        }
        finally { release.TrySetResult(); }
        await Record.ExceptionAsync(async () => await search);
        await plugin.DisposalCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        plugin.Dispose();
        Assert.Equal(1, handler.DisposeCount);
        Directory.Delete(storage, recursive: true);
    }

    [Fact]
    public async Task Dispose_TracksSharedProducerAfterItsOnlyWaiterCancels()
    {
        var storage = Path.Combine(Path.GetTempPath(), "pixiv-lifetime-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storage);
        var body = new PixivRefreshTests.GatedContent("{\"body\":{\"name\":\"Artist\"}}");
        using var plugin = new PixivAuthorPlugin();
        plugin.InitializeForTests(new Host(storage),
            new PixivApiClient(new RateLimiter(TimeSpan.Zero), _ => { }, new Handler(body), []), disposeTimeout: TimeSpan.Zero);
        using var caller = new CancellationTokenSource();
        var fetch = plugin.GetAuthorInfoAsync(new AuthorKey("pixiv", "12345"), false, caller.Token);
        await body.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await fetch);
        plugin.Dispose();
        try { Assert.False(plugin.DisposalCompletion.IsCompleted); }
        finally { body.Release.TrySetResult(); }
        await plugin.DisposalCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        // This transport deliberately ignores cancellation after headers. Its late successful
        // result must still commit before the deferred final flush, even without any waiters.
        using (var reloaded = new AuthorDiskCache(storage, _ => { }))
        {
            Assert.True(reloaded.TryGet("12345", out var entry));
            Assert.Equal("Artist", entry.Name);
        }
        Directory.Delete(storage, recursive: true);
    }

    [Fact]
    public async Task Dispose_TracksTagProducerAfterItsOnlyWaiterCancels()
    {
        var storage = Path.Combine(Path.GetTempPath(), "pixiv-lifetime-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storage);
        var body = new PixivRefreshTests.GatedContent("""{"error":false,"body":{"tag":"test","pixpedia":{"abstract":"article"}}}""");
        using var plugin = new PixivAuthorPlugin();
        plugin.InitializeForTests(new Host(storage),
            new PixivApiClient(new RateLimiter(TimeSpan.Zero), _ => { }, new Handler(body), []), disposeTimeout: TimeSpan.Zero);

        using var caller = new CancellationTokenSource();
        var fetch = plugin.FetchTagAsync("test", "en", caller.Token);
        await body.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await fetch);

        plugin.Dispose();
        try { Assert.False(plugin.DisposalCompletion.IsCompleted); }
        finally { body.Release.TrySetResult(); }
        await plugin.DisposalCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Directory.Delete(storage, recursive: true);
    }

    [Fact]
    public async Task TagFetch_CancelledWaiterDoesNotCancelOtherWaiterOrSharedCacheCommit()
    {
        var storage = Path.Combine(Path.GetTempPath(), "pixiv-lifetime-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storage);
        var body = new PixivRefreshTests.GatedContent("""{"error":false,"body":{"tag":"test","pixpedia":{"abstract":"article"}}}""");
        var handler = new Handler(body);
        using var plugin = new PixivAuthorPlugin();
        plugin.InitializeForTests(new Host(storage),
            new PixivApiClient(new RateLimiter(TimeSpan.Zero), _ => { }, handler, []));

        using var cancelledCaller = new CancellationTokenSource();
        var first = plugin.FetchTagAsync("test", "en", cancelledCaller.Token);
        await body.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = plugin.FetchTagAsync("test", "en", CancellationToken.None);
        cancelledCaller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await first);
        body.Release.TrySetResult();

        var article = await second;
        Assert.NotNull(article);
        Assert.Equal("article", article.Summary);
        Assert.Equal(1, handler.RequestCount);
        plugin.Dispose();
        await plugin.DisposalCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        using (var reloaded = new TagDiskCache(storage, _ => { }))
        {
            Assert.True(reloaded.TryGet("test", "en", out var cachedArticle));
            Assert.Equal("article", cachedArticle!.Summary);
        }
        Directory.Delete(storage, recursive: true);
    }

    [Fact]
    public async Task Dispose_RejectsNewFetches()
    {
        var storage = Path.Combine(Path.GetTempPath(), "pixiv-lifetime-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storage);
        using var plugin = new PixivAuthorPlugin();
        plugin.InitializeForTests(new Host(storage), new PixivApiClient(new RateLimiter(TimeSpan.Zero), _ => { }, new Handler(), []));
        plugin.Dispose();
        try
        {
            await Assert.ThrowsAsync<ObjectDisposedException>(() => plugin.GetAuthorInfoAsync(new AuthorKey("pixiv", "12345"), false, default));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => plugin.FetchArtworkInfoAsync(new ArtworkId("pixiv", "67890"), default));
        }
        finally { Directory.Delete(storage, recursive: true); }
    }

    private sealed class Handler(HttpContent? content = null) : HttpMessageHandler
    {
        internal bool Disposed { get; private set; }
        internal int DisposeCount { get; private set; }
        internal int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content ?? new StringContent("{}", Encoding.UTF8, "application/json") });
        }
        protected override void Dispose(bool disposing) { Disposed = true; DisposeCount++; base.Dispose(disposing); }
    }
    private sealed class Host(string storage) : IPluginHost
    {
        public string StorageDirectory => storage;
        public void Log(string message) { }
    }
}

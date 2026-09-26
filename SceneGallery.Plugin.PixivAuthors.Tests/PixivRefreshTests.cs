using System.Net;
using System.Text;
using SceneGallery.PluginSdk;

namespace SceneGallery.Plugin.PixivAuthors.Tests;

public sealed class PixivRefreshTests
{
    [Fact]
    public async Task Refresh_OlderResponseCannotOverwriteNewerCache()
    {
        var old = new GatedContent(User("Old"));
        using var context = new Context((_, number) => number == 1 ? old : Json(User("New")));
        var original = context.Fetch();
        await old.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.Equal("New", (await context.Fetch(force: true))?.Name);
        }
        finally { old.Release.TrySetResult(); }
        await original;
        Assert.Equal("New", (await context.Fetch())?.Name);
        Assert.Equal(2, context.Handler.Calls);
    }

    [Fact]
    public async Task Refresh_OlderFinallyCannotRemoveReplacementOperation()
    {
        var old = new GatedContent("{\"body\":{}}");
        var current = new GatedContent(User("New"));
        using var context = new Context((_, number) => number switch
        {
            1 => old,
            2 => current,
            _ => Json(User("Unexpected request")),
        });
        var original = context.Fetch();
        await old.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var refresh = context.Fetch(force: true);
        await current.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        old.Release.TrySetResult();
        await original;
        var follower = context.Fetch();
        current.Release.TrySetResult();
        Assert.Equal("New", (await refresh)?.Name);
        Assert.Equal("New", (await follower)?.Name);
        Assert.Equal(2, context.Handler.Calls);
    }

    [Fact]
    public async Task Refresh_OlderAvatarCannotReplaceNewerPublishedImage()
    {
        var oldAvatar = new GatedContent("old-image");
        var userRequests = 0;
        using var context = new Context((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/ajax/user/", StringComparison.Ordinal))
                return Json(Interlocked.Increment(ref userRequests) == 1
                    ? User("Old", "https://image.test/old.jpg")
                    : User("New", "https://image.test/new.jpg"));
            return request.RequestUri.AbsolutePath == "/old.jpg" ? oldAvatar : Json("new-image");
        });
        var original = context.Fetch();
        await oldAvatar.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        AuthorInfo? refreshed;
        try { refreshed = await context.Fetch(force: true); }
        finally { oldAvatar.Release.TrySetResult(); }
        await original;
        var cached = await context.Fetch();
        Assert.Equal("New", cached?.Name);
        Assert.NotNull(refreshed?.AvatarFilePath);
        Assert.Equal(refreshed.AvatarFilePath, cached!.AvatarFilePath);
        Assert.Equal("new-image", await File.ReadAllTextAsync(cached.AvatarFilePath!));
        Assert.Empty(Directory.GetFiles(context.Storage, "*.tmp", SearchOption.AllDirectories));
    }

    private static string User(string name, string? avatar = null)
        => System.Text.Json.JsonSerializer.Serialize(new { error = false, body = new { name, imageBig = avatar } });
    private static HttpContent Json(string value) => new StringContent(value, Encoding.UTF8, "application/json");

    internal sealed class GatedContent(string value) : HttpContent
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            Started.TrySetResult();
            await Release.Task;
            await stream.WriteAsync(Encoding.UTF8.GetBytes(value));
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class Context : IDisposable
    {
        private readonly PixivAuthorPlugin _plugin = new();
        internal string Storage { get; } = Path.Combine(Path.GetTempPath(), "pixiv-refresh-tests", Guid.NewGuid().ToString("N"));
        internal Handler Handler { get; }
        internal Context(Func<HttpRequestMessage, int, HttpContent> respond)
        {
            Directory.CreateDirectory(Storage);
            Handler = new Handler(respond);
            var client = new PixivApiClient(new RateLimiter(TimeSpan.Zero), _ => { }, Handler, []);
            _plugin.InitializeForTests(new Host(Storage), client);
        }
        internal Task<AuthorInfo?> Fetch(bool force = false)
            => _plugin.GetAuthorInfoAsync(new AuthorKey("pixiv", "12345"), force, CancellationToken.None);
        public void Dispose()
        {
            _plugin.Dispose();
            Directory.Delete(Storage, recursive: true);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, int, HttpContent> respond) : HttpMessageHandler
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = respond(request, Interlocked.Increment(ref _calls)) });
    }
    private sealed class Host(string storage) : IPluginHost
    {
        public string StorageDirectory => storage;
        public void Log(string message) { }
    }
}

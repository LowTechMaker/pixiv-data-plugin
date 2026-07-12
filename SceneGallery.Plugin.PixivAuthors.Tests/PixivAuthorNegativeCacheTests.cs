using System.Net;
using System.Text;
using SceneGallery.PluginSdk;

namespace SceneGallery.Plugin.PixivAuthors.Tests;

public sealed class PixivAuthorNegativeCacheTests
{
    private const string ValidUserJson = """
        {"error":false,"body":{"name":"Recovered Artist"}}
        """;

    [Fact]
    public async Task Http404_IsNegativeCached()
    {
        using var context = new PluginTestContext(
            Respond(HttpStatusCode.NotFound));

        Assert.Null(await context.FetchAsync());
        Assert.Null(await context.FetchAsync());

        Assert.Equal(1, context.Handler.CallCount);
    }

    [Fact]
    public async Task ExplicitNotFoundResponse_IsNegativeCached()
    {
        using var context = new PluginTestContext(
            RespondJson("""{"error":true,"message":"User not found"}"""));

        Assert.Null(await context.FetchAsync());
        Assert.Null(await context.FetchAsync());

        Assert.Equal(1, context.Handler.CallCount);
    }

    [Theory]
    [InlineData(23, true)]
    [InlineData(25, false)]
    public void NegativeCache_ExpiresAfter24Hours(int ageInHours, bool expectedHit)
    {
        var storageDirectory = CreateTempDirectory();
        try
        {
            using var cache = new AuthorDiskCache(storageDirectory, _ => { });
            cache.Set("12345", new AuthorDiskCache.CachedAuthor(
                null,
                null,
                DateTimeOffset.UtcNow.AddHours(-ageInHours),
                Failed: true));

            Assert.Equal(expectedHit, cache.TryGet("12345", out _));
        }
        finally
        {
            TryDeleteDirectory(storageDirectory);
        }
    }

    [Fact]
    public async Task MissingBody_DoesNotCacheAndLogsTruncatedResponse()
    {
        var rawResponse = $$"""{"error":false,"unexpected":"{{new string('x', 700)}}"}""";
        using var context = new PluginTestContext(
            RespondJson(rawResponse),
            RespondJson(ValidUserJson));

        Assert.Null(await context.FetchAsync());
        var recovered = await context.FetchAsync();

        Assert.Equal("Recovered Artist", recovered?.Name);
        Assert.Equal(2, context.Handler.CallCount);
        var warning = Assert.Single(context.Logs, message => message.Contains("schema error"));
        var summary = warning[(warning.IndexOf("response: ", StringComparison.Ordinal) + "response: ".Length)..];
        Assert.Equal(500, summary.Length);
        Assert.Equal(rawResponse[..500], summary);
    }

    [Fact]
    public async Task MissingName_DoesNotCacheAndRetriesApi()
    {
        using var context = new PluginTestContext(
            RespondJson("""{"error":false,"body":{"image":"avatar.jpg"}}"""),
            RespondJson(ValidUserJson));

        Assert.Null(await context.FetchAsync());
        var recovered = await context.FetchAsync();

        Assert.Equal("Recovered Artist", recovered?.Name);
        Assert.Equal(2, context.Handler.CallCount);
        Assert.Contains(context.Logs, message => message.Contains("schema error"));
    }

    [Fact]
    public async Task AmbiguousErrorResponse_DoesNotCacheAndRetriesApi()
    {
        using var context = new PluginTestContext(
            RespondJson("""{"error":true,"message":"An unexpected error occurred"}"""),
            RespondJson(ValidUserJson));

        Assert.Null(await context.FetchAsync());
        var recovered = await context.FetchAsync();

        Assert.Equal("Recovered Artist", recovered?.Name);
        Assert.Equal(2, context.Handler.CallCount);
        Assert.Contains(context.Logs, message => message.Contains("schema error"));
    }

    [Fact]
    public async Task NetworkFailure_DoesNotCacheAndRetriesApi()
    {
        using var context = new PluginTestContext(
            Throw(new HttpRequestException("offline")),
            RespondJson(ValidUserJson));

        Assert.Null(await context.FetchAsync());
        var recovered = await context.FetchAsync();

        Assert.Equal("Recovered Artist", recovered?.Name);
        Assert.Equal(2, context.Handler.CallCount);
        Assert.Contains(context.Logs, message => message.Contains("fetch failed"));
    }

    [Fact]
    public async Task ServerError_DoesNotCacheAndRetriesApi()
    {
        using var context = new PluginTestContext(
            Respond(HttpStatusCode.ServiceUnavailable),
            Respond(HttpStatusCode.ServiceUnavailable),
            Respond(HttpStatusCode.ServiceUnavailable),
            RespondJson(ValidUserJson));

        Assert.Null(await context.FetchAsync());
        var recovered = await context.FetchAsync();

        Assert.Equal("Recovered Artist", recovered?.Name);
        Assert.Equal(4, context.Handler.CallCount);
        Assert.Contains(context.Logs, message => message.Contains("fetch failed"));
    }

    [Fact]
    public async Task SharedAuthorFetch_CallerCancellationDoesNotCancelOtherCallerOrCacheWrite()
    {
        var requestStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var context = new PluginTestContext(
            WaitThenRespondJson(requestStarted, releaseResponse, ValidUserJson));
        using var callerCancellation = new CancellationTokenSource();

        var callerA = context.FetchAsync(callerCancellation.Token);
        await requestStarted.Task;
        var callerB = context.FetchAsync();

        callerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await callerA);
        releaseResponse.SetResult(true);

        var resultB = await callerB;
        var cached = await context.FetchAsync();
        Assert.Equal("Recovered Artist", resultB?.Name);
        Assert.Equal("Recovered Artist", cached?.Name);
        Assert.Equal(1, context.Handler.CallCount);
    }

    [Fact]
    public async Task SharedArtworkFetch_CallerCancellationDoesNotCancelOtherCallerOrCacheWrite()
    {
        const string artworkJson = """
            {
              "error": false,
              "body": {
                "userId": "42",
                "userName": "Artwork Artist",
                "title": "Artwork Title",
                "xRestrict": 0,
                "tags": {"tags": []}
              }
            }
            """;
        var requestStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var context = new PluginTestContext(
            WaitThenRespondJson(requestStarted, releaseResponse, artworkJson));
        using var callerCancellation = new CancellationTokenSource();

        var callerA = context.FetchArtworkAsync(callerCancellation.Token);
        await requestStarted.Task;
        var callerB = context.FetchArtworkAsync();

        callerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await callerA);
        releaseResponse.SetResult(true);

        var resultB = await callerB;
        var cached = await context.FetchArtworkAsync();
        Assert.Equal("Artwork Artist", resultB?.AuthorName);
        Assert.Equal("Artwork Artist", cached?.AuthorName);
        Assert.True(cached?.IsSavedLocally);
        Assert.Equal(1, context.Handler.CallCount);
    }

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond(
        HttpStatusCode statusCode)
        => (_, _) => Task.FromResult(new HttpResponseMessage(statusCode));

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> RespondJson(
        string json)
        => (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Throw(
        Exception exception)
        => (_, _) => Task.FromException<HttpResponseMessage>(exception);

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> WaitThenRespondJson(
        TaskCompletionSource<bool> requestStarted,
        TaskCompletionSource<bool> releaseResponse,
        string json)
        => async (_, cancellationToken) =>
        {
            requestStarted.TrySetResult(true);
            await releaseResponse.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        };

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "SceneGallery.Plugin.PixivAuthors.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup for a test-only temporary directory.
        }
    }

    private sealed class PluginTestContext : IDisposable
    {
        private const string UserId = "12345";
        private readonly string _storageDirectory = CreateTempDirectory();
        private readonly PixivAuthorPlugin _plugin = new();

        public PluginTestContext(
            params Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] responses)
        {
            Handler = new SequenceHttpMessageHandler(responses);
            var client = new PixivApiClient(
                new RateLimiter(TimeSpan.Zero),
                Logs.Add,
                Handler,
                [TimeSpan.Zero, TimeSpan.Zero]);
            _plugin.InitializeForTests(new TestPluginHost(_storageDirectory, Logs.Add), client);
        }

        public SequenceHttpMessageHandler Handler { get; }

        public List<string> Logs { get; } = [];

        public Task<AuthorInfo?> FetchAsync(CancellationToken cancellationToken = default)
            => _plugin.GetAuthorInfoAsync(
                new AuthorKey(PixivFolderNameParser.ProviderId, UserId),
                forceRefresh: false,
                cancellationToken);

        public Task<ArtworkInfo?> FetchArtworkAsync(CancellationToken cancellationToken = default)
            => _plugin.FetchArtworkInfoAsync(
                new ArtworkId(PixivFolderNameParser.ProviderId, "67890"),
                cancellationToken,
                saveToLocalCache: true);

        public void Dispose()
        {
            _plugin.Dispose();
            TryDeleteDirectory(_storageDirectory);
        }
    }

    private sealed class SequenceHttpMessageHandler(
        IEnumerable<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> responses)
        : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _responses = new(responses);

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            if (_responses.Count == 0)
                throw new InvalidOperationException("The test issued more HTTP requests than expected.");
            return _responses.Dequeue()(request, cancellationToken);
        }
    }

    private sealed class TestPluginHost(string storageDirectory, Action<string> log) : IPluginHost
    {
        public string StorageDirectory { get; } = storageDirectory;

        public void Log(string message) => log(message);
    }
}

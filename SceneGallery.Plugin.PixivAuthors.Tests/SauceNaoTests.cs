using System.Net;
using System.Text;
using System.Text.Json;

namespace SceneGallery.Plugin.PixivAuthors.Tests;

public sealed class SauceNaoTests
{
    private const string Match = """{"header":{"similarity":"87.5","thumbnail":"https://image.test/thumb.jpg"},"data":{"pixiv_id":123456,"member_id":"42","member_name":"Artist","title":"Title"}}""";

    [Fact]
    public void Parser_SelectsHighestSimilarityCompletePixivResult()
    {
        using var json = JsonDocument.Parse("{\"results\":[null,{}," + Match.Replace("87.5", "10") + "," + Match + "]}");
        var result = SauceNaoResponseParser.Parse(json.RootElement, out var failure);
        Assert.Null(failure);
        Assert.Equal("123456", result?.PixivId);
        Assert.Equal("Artist", result?.AuthorName);
        Assert.Equal(87.5, result?.Similarity);
        Assert.Equal("https://www.pixiv.net/artworks/123456", result?.SourceUrl);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"results\":{}}")]
    [InlineData("{\"results\":[{\"header\":{},\"data\":{\"pixiv_id\":1}}]}")]
    public void Parser_MalformedOrIncompletePayloadDoesNotProduceAMatch(string value)
    {
        using var json = JsonDocument.Parse(value);
        Assert.Null(SauceNaoResponseParser.Parse(json.RootElement, out _));
    }

    [Fact]
    public void Encoder_CompositesTransparentPixelsOverWhite()
    {
        byte[] pixels = [0, 0, 0, 0, 10, 20, 30, 255, 0, 0, 0, 128];
        SauceNaoImageEncoder.CompositeTransparentPixelsOverWhite(pixels);
        Assert.Equal(new byte[] { 255, 255, 255, 255, 10, 20, 30, 255, 127, 127, 127, 255 }, pixels);
    }

    [Fact]
    public async Task Client_UsesInjectedEncoderAndSendsExpectedMultipartRequest()
    {
        var path = Path.GetTempFileName();
        var encoded = new TrackedStream([1, 2, 3]);
        try
        {
            using var client = new SauceNaoClient(new RateLimiter(TimeSpan.Zero), _ => { }, new Handler(async (request, ct) =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("saucenao.com", request.RequestUri!.Host);
                Assert.Contains("api_key=a%2Bb%26c", request.RequestUri.Query);
                Assert.Contains("output_type=2&numres=8&db=999", request.RequestUri.Query);
                var multipart = Assert.IsType<MultipartFormDataContent>(request.Content);
                var image = Assert.Single(multipart);
                Assert.Equal("image/jpeg", image.Headers.ContentType?.MediaType);
                Assert.Equal("file", image.Headers.ContentDisposition?.Name?.Trim('"'));
                Assert.Equal(Path.ChangeExtension(Path.GetFileName(path), ".jpg"), image.Headers.ContentDisposition?.FileName?.Trim('"'));
                Assert.Equal(new byte[] { 1, 2, 3 }, await image.ReadAsByteArrayAsync(ct));
                return Response("{\"results\":[" + Match + "]}");
            }), (actual, _) =>
            {
                Assert.Equal(path, actual);
                return Task.FromResult<Stream>(encoded);
            });
            Assert.Equal("123456", (await client.SearchPixivAsync(path, "a+b&c", default))?.PixivId);
            Assert.True(encoded.Disposed);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Client_ApiErrorDoesNotLogReflectedSecret()
    {
        var path = Path.GetTempFileName();
        var messages = new List<string>();
        try
        {
            using var client = new SauceNaoClient(new RateLimiter(TimeSpan.Zero), messages.Add,
                new Handler((_, _) => Task.FromResult(Response("{\"header\":{\"status\":-1,\"message\":\"secret-key\"}}"))),
                (_, _) => Task.FromResult<Stream>(new MemoryStream([1])));
            Assert.Null(await client.SearchPixivAsync(path, "secret-key", default));
            Assert.Single(messages);
            Assert.DoesNotContain(messages, message => message.Contains("secret-key", StringComparison.Ordinal));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Client_CancellationPropagatesThroughEncoder()
    {
        var path = Path.GetTempFileName();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            using var client = new SauceNaoClient(new RateLimiter(TimeSpan.Zero), _ => { },
                new Handler((_, _) => throw new InvalidOperationException("No HTTP should occur.")),
                (_, ct) => Task.FromCanceled<Stream>(ct));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SearchPixivAsync(path, "key", cancelled.Token));
        }
        finally { File.Delete(path); }
    }

    private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
    private sealed class TrackedStream(byte[] value) : MemoryStream(value)
    {
        internal bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}

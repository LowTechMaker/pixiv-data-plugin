using System.Net;
using System.Text;

namespace SceneGallery.Plugin.PixivAuthors.Tests;

public sealed class PixivApiClientTests
{
    [Fact]
    public async Task FetchUserAsync_ValidResponse_ReturnsAuthor()
    {
        const string json = """
            {
              "error": false,
              "body": {
                "name": "Test Artist",
                "imageBig": "https://i.pximg.net/user-profile/img/test.jpg"
              }
            }
            """;
        var handler = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
        var log = new List<string>();
        using var client = new PixivApiClient(new RateLimiter(TimeSpan.Zero), log.Add, handler);

        var result = await client.FetchUserAsync("12345", CancellationToken.None);

        Assert.Equal(PixivApiClient.UserFetchStatus.Success, result.Status);
        Assert.Equal("Test Artist", result.Name);
        Assert.Equal("https://i.pximg.net/user-profile/img/test.jpg", result.AvatarUrl);
        Assert.Equal("https://www.pixiv.net/ajax/user/12345?full=1&lang=en", handler.RequestUri?.AbsoluteUri);
        Assert.Empty(log);
    }

    private sealed class StubHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(response);
        }
    }
}

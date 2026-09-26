using System.Net;
using System.Text;
using SceneGallery.PluginSdk;

namespace SceneGallery.Plugin.PixivAuthors.Tests;

/// <summary>
/// The re-fetch behind the host's "fetch artwork data again" run, and the
/// language every artwork request is made in.
/// </summary>
public sealed class PixivArtworkRefreshTests : IDisposable
{
    private static readonly ArtworkId Artwork = new(PixivFolderNameParser.ProviderId, "67890");

    private readonly string _storage = Directory.CreateTempSubdirectory("pixiv-refresh").FullName;
    private readonly List<string> _urls = [];
    private readonly Queue<Func<HttpResponseMessage>> _responses = new();
    private PixivAuthorPlugin? _plugin;

    public void Dispose()
    {
        _plugin?.Dispose();
        try { Directory.Delete(_storage, recursive: true); } catch (IOException) { }
    }

    private PixivAuthorPlugin Plugin(string language = "en")
    {
        var client = new PixivApiClient(
            new RateLimiter(TimeSpan.Zero), _ => { }, new Handler(this), [TimeSpan.Zero, TimeSpan.Zero]);
        _plugin = new PixivAuthorPlugin();
        _plugin.InitializeForTests(new Host(_storage, language), client);
        return _plugin;
    }

    private void Answer(string title)
        => _responses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"error":false,"body":{"userId":"42","userName":"Artist","title":"TITLE",
                 "xRestrict":0,"tags":{"tags":[{"tag":"コイカツ!","translation":{"en":"戀活！"}}]}}}
                """.Replace("TITLE", title, StringComparison.Ordinal), Encoding.UTF8, "application/json"),
        });

    [Fact]
    public async Task ARefreshGoesToPixivEvenWithACachedCopyAndReplacesIt()
    {
        var plugin = Plugin();
        Answer("Old");
        Answer("New");

        Assert.Equal("Old", (await plugin.FetchArtworkInfoAsync(Artwork, default))?.Title);
        // The ordinary lookup is answered from the cache from here on, which
        // is exactly why a re-fetch needs its own way past it.
        Assert.Equal("Old", (await plugin.FetchArtworkInfoAsync(Artwork, default))?.Title);
        Assert.Single(_urls);

        Assert.Equal("New", (await plugin.RefreshArtworkAsync(Artwork, default)).Info?.Title);
        Assert.Equal(2, _urls.Count);
        Assert.Equal("New", (await plugin.FetchArtworkInfoAsync(Artwork, default))?.Title);
        Assert.Equal(2, _urls.Count);
    }

    [Fact]
    public async Task ARefreshThatFindsNothingKeepsWhatWasCached()
    {
        // A deleted or private artwork answers 404. The re-fetch reports it,
        // but must not throw away what the original import did get.
        var plugin = Plugin();
        Answer("Kept");
        _responses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.NotFound));

        await plugin.FetchArtworkInfoAsync(Artwork, default);
        Assert.Equal(ArtworkRefreshStatus.Gone, (await plugin.RefreshArtworkAsync(Artwork, default)).Status);
        Assert.Equal("Kept", (await plugin.FetchArtworkInfoAsync(Artwork, default))?.Title);
    }

    [Theory]
    [InlineData("zh-Hant", "lang=zh_tw")]
    [InlineData("ja-JP", "lang=ja")]
    [InlineData("en-US", "lang=en")]
    public async Task ArtworksAreRequestedInTheLanguageTheHostReports(string uiLanguage, string expected)
    {
        // Was hard-coded to English, so every sidecar written at import got
        // English tag translations whatever language the app was in.
        var plugin = Plugin(uiLanguage);
        Answer("Title");

        await plugin.RefreshArtworkAsync(Artwork, default);

        Assert.Contains(expected, Assert.Single(_urls), StringComparison.Ordinal);
    }

    // The first real run stopped itself within seconds of resuming: every
    // deleted artwork counted as a failure, and a resumed missing-only run
    // retries exactly those first. Deleted and failed must stay apart.
    [Fact]
    public async Task ANotFoundArtworkIsGone()
    {
        var plugin = Plugin();
        _responses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"error":true,"message":"","body":[]}""", Encoding.UTF8, "application/json"),
        });

        Assert.Equal(ArtworkRefreshStatus.Gone, (await plugin.RefreshArtworkAsync(Artwork, default)).Status);
    }

    [Fact]
    public async Task AnErrorFlagWithSuccessStatusIsGone()
    {
        var plugin = Plugin();
        _responses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"error":true,"message":"","body":[]}""", Encoding.UTF8, "application/json"),
        });

        Assert.Equal(ArtworkRefreshStatus.Gone, (await plugin.RefreshArtworkAsync(Artwork, default)).Status);
    }

    [Fact]
    public async Task AServerErrorIsAFailureNotAGoneArtwork()
    {
        var plugin = Plugin();
        for (var i = 0; i < 3; i++)
            _responses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        Assert.Equal(ArtworkRefreshStatus.Failed, (await plugin.RefreshArtworkAsync(Artwork, default)).Status);
    }

    [Fact]
    public async Task AnAnswerWithoutAnAuthorIsAFailureNotAGoneArtwork()
    {
        // A changed response shape says nothing about the artwork; treating it
        // as deleted would stop asking about artworks that are still there.
        var plugin = Plugin();
        _responses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"error":false,"body":{"title":"x"}}""", Encoding.UTF8, "application/json"),
        });

        Assert.Equal(ArtworkRefreshStatus.Failed, (await plugin.RefreshArtworkAsync(Artwork, default)).Status);
    }

    private sealed class Handler(PixivArtworkRefreshTests owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            owner._urls.Add(request.RequestUri!.ToString());
            if (owner._responses.Count == 0)
                throw new InvalidOperationException("The test issued more HTTP requests than expected.");
            return Task.FromResult(owner._responses.Dequeue()());
        }
    }

    private sealed class Host(string storage, string language) : IPluginHost
    {
        public string StorageDirectory { get; } = storage;
        public string Language { get; } = language;
        public void Log(string message) { }
    }
}

using System.Net;
using System.Text.Json;

namespace SceneGallery.Plugin.PixivAuthors;

/// <summary>Owns SauceNao HTTP requests; encoding and JSON interpretation are independent seams.</summary>
internal sealed class SauceNaoClient : IDisposable
{
    private const string SearchUrl = "https://saucenao.com/search.php";
    private const string UserAgent = "KoikatsuSceneGallery/1.0 (+https://saucenao.com/)";
    private readonly HttpClient _http;
    private readonly RateLimiter _rateLimiter;
    private readonly Action<string> _log;
    private readonly Func<string, CancellationToken, Task<Stream>> _encodeImage;

    public SauceNaoClient(RateLimiter rateLimiter, Action<string> log)
        : this(rateLimiter, log, new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All }, SauceNaoImageEncoder.EncodeAsync)
    {
    }

    internal SauceNaoClient(RateLimiter rateLimiter, Action<string> log, HttpMessageHandler handler,
        Func<string, CancellationToken, Task<Stream>> encodeImage)
    {
        _rateLimiter = rateLimiter;
        _log = log;
        _encodeImage = encodeImage;
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(45) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
    }

    public async Task<SauceNaoPixivResult?> SearchPixivAsync(string imagePath, string apiKey, CancellationToken ct)
    {
        if (!File.Exists(imagePath)) return null;
        var url = $"{SearchUrl}?output_type=2&numres=8&db=999&api_key={Uri.EscapeDataString(apiKey)}";
        using var jpeg = await _encodeImage(imagePath, ct).ConfigureAwait(false);
        using var content = new MultipartFormDataContent();
        using var imageContent = new StreamContent(jpeg);
        imageContent.Headers.ContentType = new("image/jpeg");
        content.Add(imageContent, "file", Path.ChangeExtension(Path.GetFileName(imagePath), ".jpg"));
        using (await _rateLimiter.AcquireAsync(ct).ConfigureAwait(false))
        using (var response = await _http.PostAsync(url, content, ct).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var result = SauceNaoResponseParser.Parse(doc.RootElement, out var failureStatus);
            if (failureStatus is { } status)
                _log($"SauceNao returned status {status}.");
            return result;
        }
    }

    public void Dispose() => _http.Dispose();
}

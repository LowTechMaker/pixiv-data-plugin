using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace SceneGallery.Plugin.PixivAuthors;

internal sealed class SauceNaoClient : IDisposable
{
    private const string SearchUrl = "https://saucenao.com/search.php";
    private const string UserAgent =
        "KoikatsuSceneGallery/1.0 (+https://saucenao.com/)";

    private readonly HttpClient _http;
    private readonly RateLimiter _rateLimiter;
    private readonly Action<string> _log;

    public SauceNaoClient(RateLimiter rateLimiter, Action<string> log)
    {
        _rateLimiter = rateLimiter;
        _log = log;
        _http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromSeconds(45),
        };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
    }

    public async Task<SauceNaoPixivResult?> SearchPixivAsync(
        string imagePath,
        string apiKey,
        CancellationToken ct)
    {
        if (!File.Exists(imagePath))
            return null;

        var url = $"{SearchUrl}?output_type=2&numres=8&db=999&api_key={Uri.EscapeDataString(apiKey)}";
        using var jpeg = await ConvertToJpegAsync(imagePath, ct).ConfigureAwait(false);
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
            return ParsePixivResult(doc.RootElement);
        }
    }

    private static async Task<MemoryStream> ConvertToJpegAsync(string imagePath, CancellationToken ct)
    {
        var file = await StorageFile.GetFileFromPathAsync(imagePath).AsTask(ct).ConfigureAwait(false);
        using var input = await file.OpenReadAsync().AsTask(ct).ConfigureAwait(false);
        var decoder = await BitmapDecoder.CreateAsync(input).AsTask(ct).ConfigureAwait(false);

        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight,
            new BitmapTransform(),
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage).AsTask(ct).ConfigureAwait(false);

        var pixels = pixelData.DetachPixelData();
        CompositeTransparentPixelsOverWhite(pixels);

        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(
            BitmapEncoder.JpegEncoderId, output).AsTask(ct).ConfigureAwait(false);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            decoder.PixelWidth,
            decoder.PixelHeight,
            decoder.DpiX,
            decoder.DpiY,
            pixels);
        await encoder.FlushAsync().AsTask(ct).ConfigureAwait(false);

        var bytes = new byte[output.Size];
        output.Seek(0);
        using var reader = new DataReader(output.GetInputStreamAt(0));
        await reader.LoadAsync((uint)output.Size).AsTask(ct).ConfigureAwait(false);
        reader.ReadBytes(bytes);
        return new MemoryStream(bytes, writable: false);
    }

    private static void CompositeTransparentPixelsOverWhite(byte[] pixels)
    {
        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            if (alpha == 255)
                continue;

            pixels[i] = CompositeChannel(pixels[i], alpha);
            pixels[i + 1] = CompositeChannel(pixels[i + 1], alpha);
            pixels[i + 2] = CompositeChannel(pixels[i + 2], alpha);
            pixels[i + 3] = 255;
        }
    }

    private static byte CompositeChannel(byte channel, byte alpha)
    {
        var value = (channel * alpha + 255 * (255 - alpha)) / 255;
        return (byte)value;
    }

    private SauceNaoPixivResult? ParsePixivResult(JsonElement root)
    {
        if (root.TryGetProperty("header", out var header)
            && header.TryGetProperty("status", out var status)
            && status.ValueKind == JsonValueKind.Number
            && status.GetInt32() < 0)
        {
            var message = header.TryGetProperty("message", out var msg) ? msg.GetString() : null;
            _log($"SauceNao returned status {status.GetInt32()}: {message}");
            return null;
        }

        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return null;

        SauceNaoPixivResult? best = null;
        foreach (var result in results.EnumerateArray())
        {
            var parsed = TryParsePixivResult(result);
            if (parsed is null)
                continue;

            if (best is null || parsed.Similarity > best.Similarity)
                best = parsed;
        }

        return best;
    }

    private static SauceNaoPixivResult? TryParsePixivResult(JsonElement result)
    {
        if (!result.TryGetProperty("header", out var header)
            || !result.TryGetProperty("data", out var data))
            return null;

        var similarityText = header.TryGetProperty("similarity", out var sim)
            ? sim.GetString()
            : null;
        if (!double.TryParse(similarityText, NumberStyles.Float, CultureInfo.InvariantCulture, out var similarity))
            similarity = 0;

        var pixivId = GetString(data, "pixiv_id");
        var authorId = GetString(data, "member_id");
        var authorName = GetString(data, "member_name");
        if (string.IsNullOrWhiteSpace(pixivId)
            || string.IsNullOrWhiteSpace(authorId)
            || string.IsNullOrWhiteSpace(authorName))
        {
            return null;
        }

        var sourceUrl = GetFirstString(data, "ext_urls")
            ?? $"https://www.pixiv.net/artworks/{pixivId}";
        var thumbnailUrl = GetString(header, "thumbnail");
        return new SauceNaoPixivResult(
            pixivId,
            GetString(data, "title"),
            authorName,
            authorId,
            similarity,
            thumbnailUrl,
            sourceUrl);
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            _ => null,
        };
    }

    private static string? GetFirstString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in property.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String)
                return item.GetString();

        return null;
    }

    public void Dispose() => _http.Dispose();
}

internal sealed record SauceNaoPixivResult(
    string PixivId,
    string? Title,
    string AuthorName,
    string AuthorId,
    double Similarity,
    string? ThumbnailUrl,
    string? SourceUrl);

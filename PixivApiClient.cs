using System.Net;
using System.Text.Json;
using SceneGallery.PluginSdk;

namespace SceneGallery.Plugin.PixivAuthors;

/// <summary>
/// Anonymous client for pixiv's web JSON endpoints. Presents itself as a
/// regular browser (UA + Referer) because both www.pixiv.net/ajax and the
/// i.pximg.net image host reject clients without them. All requests pass
/// through a shared <see cref="RateLimiter"/>.
/// </summary>
internal sealed class PixivApiClient : IDisposable
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0.0.0 Safari/537.36";
    private const string Referer = "https://www.pixiv.net/";

    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20)];

    private readonly HttpClient _http;
    private readonly RateLimiter _rateLimiter;
    private readonly Action<string> _log;
    private readonly IReadOnlyList<TimeSpan> _retryDelays;

    internal enum UserFetchStatus
    {
        Success,
        NotFound,
        SchemaError,
    }

    internal readonly record struct UserFetchResult(
        UserFetchStatus Status,
        string? Name = null,
        string? AvatarUrl = null);

    public PixivApiClient(RateLimiter rateLimiter, Action<string> log)
        : this(rateLimiter, log, new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
        })
    {
    }

    internal PixivApiClient(
        RateLimiter rateLimiter,
        Action<string> log,
        HttpMessageHandler handler,
        IReadOnlyList<TimeSpan>? retryDelays = null)
    {
        _rateLimiter = rateLimiter;
        _log = log;
        _retryDelays = retryDelays ?? RetryDelays;
        _http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", Referer);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
    }

    /// <summary>
    /// Fetches public profile info and classifies the response as success,
    /// confirmed not-found, or schema error. Transport-level failures throw.
    /// </summary>
    public async Task<UserFetchResult> FetchUserAsync(
        string userId, CancellationToken ct)
    {
        var url = $"https://www.pixiv.net/ajax/user/{userId}?full=1&lang=en";
        using var response = await SendWithRetryAsync(url, "application/json", ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new UserFetchResult(UserFetchStatus.NotFound);
        response.EnsureSuccessStatusCode();

        var rawResponse = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        // The response shape is unofficial and drifts; probe with JsonDocument
        // instead of binding a model so unrelated changes don't break parsing.
        using var doc = JsonDocument.Parse(rawResponse);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var error) && error.GetBoolean())
        {
            if (IsExplicitNotFound(root))
                return new UserFetchResult(UserFetchStatus.NotFound);

            LogSchemaError(userId, "API returned error=true without a confirmed not-found message", rawResponse);
            return new UserFetchResult(UserFetchStatus.SchemaError);
        }
        if (!root.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Object)
        {
            LogSchemaError(userId, "response is missing an object body", rawResponse);
            return new UserFetchResult(UserFetchStatus.SchemaError);
        }

        var name = body.TryGetProperty("name", out var n) ? n.GetString() : null;
        if (string.IsNullOrWhiteSpace(name))
        {
            LogSchemaError(userId, "response body is missing name", rawResponse);
            return new UserFetchResult(UserFetchStatus.SchemaError);
        }

        string? avatarUrl = null;
        if (body.TryGetProperty("imageBig", out var img) && img.ValueKind == JsonValueKind.String)
            avatarUrl = img.GetString();
        else if (body.TryGetProperty("image", out var imgSmall) && imgSmall.ValueKind == JsonValueKind.String)
            avatarUrl = imgSmall.GetString();

        return new UserFetchResult(UserFetchStatus.Success, name, avatarUrl);
    }

    private static bool IsExplicitNotFound(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var messageElement)
            || messageElement.ValueKind != JsonValueKind.String)
            return false;

        var message = messageElement.GetString() ?? "";
        return message.Contains("not found", StringComparison.OrdinalIgnoreCase)
               || message.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
               || message.Contains("存在しません", StringComparison.Ordinal)
               || message.Contains("不存在", StringComparison.Ordinal);
    }

    private void LogSchemaError(string userId, string reason, string rawResponse)
    {
        const int maxSummaryLength = 500;
        var summary = rawResponse.Length <= maxSummaryLength
            ? rawResponse
            : rawResponse[..maxSummaryLength];
        _log($"warning: pixiv user {userId} schema error ({reason}); response: {summary}");
    }

    internal readonly record struct PixivArtworkData(
        string UserId,
        string UserName,
        string? Title,
        string? Description,
        int XRestrict,
        IReadOnlyList<(string Tag, string? Translation)> Tags);

    /// <summary>
    /// Fetches public artwork info. Returns parsed data, or null when pixiv
    /// reports the artwork as missing (deleted/private — cacheable).
    /// </summary>
    public async Task<PixivArtworkData?> FetchArtworkAsync(
        string artworkId, CancellationToken ct)
    {
        var url = $"https://www.pixiv.net/ajax/illust/{artworkId}?lang=en";
        using var response = await SendWithRetryAsync(url, "application/json", ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var error) && error.GetBoolean())
        {
            _log($"pixiv artwork {artworkId}: API returned error=true");
            return null;
        }
        if (!root.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Object)
            return null;

        var userId = body.TryGetProperty("userId", out var uid) ? uid.GetString() : null;
        var userName = body.TryGetProperty("userName", out var un) ? un.GetString() : null;
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(userName))
            return null;

        string? title = body.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() : null;

        string? description = body.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.String
            ? desc.GetString() : null;

        int xRestrict = 0;
        if (body.TryGetProperty("xRestrict", out var xr))
            xRestrict = xr.GetInt32();

        var tags = new List<(string Tag, string? Translation)>();
        if (body.TryGetProperty("tags", out var tagsObj)
            && tagsObj.TryGetProperty("tags", out var tagsArr)
            && tagsArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var tagEl in tagsArr.EnumerateArray())
            {
                var tagName = tagEl.TryGetProperty("tag", out var tn) ? tn.GetString() : null;
                if (string.IsNullOrEmpty(tagName)) continue;

                string? translation = null;
                if (tagEl.TryGetProperty("translation", out var tr)
                    && tr.ValueKind == JsonValueKind.Object
                    && tr.TryGetProperty("en", out var en)
                    && en.ValueKind == JsonValueKind.String)
                {
                    translation = en.GetString();
                }
                tags.Add((tagName, translation));
            }
        }

        return new PixivArtworkData(userId!, userName!, title, description, xRestrict, tags);
    }

    /// <summary>
    /// Downloads an avatar to <paramref name="destinationPath"/> (extension is
    /// appended from the URL). Writes via a temp file + move so a half-written
    /// image is never visible to the app. Returns the final path, or null when
    /// the download fails in a non-retryable way (e.g. image gone).
    /// </summary>
    public async Task<string?> DownloadAvatarAsync(
        string avatarUrl, string destinationPathWithoutExtension, CancellationToken ct)
    {
        var extension = Path.GetExtension(new Uri(avatarUrl).AbsolutePath);
        if (string.IsNullOrEmpty(extension) || extension.Length > 5) extension = ".jpg";
        var finalPath = destinationPathWithoutExtension + extension;

        using var response = await SendWithRetryAsync(avatarUrl, "image/*", ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _log($"avatar download failed ({(int)response.StatusCode}): {avatarUrl}");
            return null;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        var tempPath = finalPath + ".tmp";
        try
        {
            await using (var file = File.Create(tempPath))
                await response.Content.CopyToAsync(file, ct).ConfigureAwait(false);
            File.Move(tempPath, finalPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                try { File.Delete(tempPath); } catch { /* best effort */ }
        }
        return finalPath;
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        string url, string accept, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using (await _rateLimiter.AcquireAsync(ct).ConfigureAwait(false))
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("Accept", accept);
                var response = await _http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                var transient = response.StatusCode == HttpStatusCode.TooManyRequests
                                || (int)response.StatusCode >= 500;
                if (!transient || attempt >= _retryDelays.Count)
                    return response;

                _log($"pixiv returned {(int)response.StatusCode}, retrying in {_retryDelays[attempt].TotalSeconds}s: {url}");
                response.Dispose();
            }
            await Task.Delay(_retryDelays[attempt], ct).ConfigureAwait(false);
        }
    }

    public void Dispose() => _http.Dispose();
}

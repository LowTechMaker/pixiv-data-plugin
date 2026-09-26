using System.Globalization;
using System.Text.Json;

namespace SceneGallery.Plugin.PixivAuthors;

/// <summary>Pure response policy: choose the highest-similarity complete Pixiv match.</summary>
internal static class SauceNaoResponseParser
{
    internal static SauceNaoPixivResult? Parse(JsonElement root, out int? failureStatus)
    {
        failureStatus = null;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (root.TryGetProperty("header", out var header) && header.ValueKind == JsonValueKind.Object
            && header.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Number
            && status.TryGetInt32(out var statusCode) && statusCode < 0)
        {
            failureStatus = statusCode;
            return null;
        }
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return null;
        SauceNaoPixivResult? best = null;
        foreach (var result in results.EnumerateArray())
        {
            var parsed = TryParse(result);
            if (parsed is not null && (best is null || parsed.Similarity > best.Similarity))
                best = parsed;
        }
        return best;
    }

    private static SauceNaoPixivResult? TryParse(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("header", out var header) || header.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return null;
        var similarityText = GetString(header, "similarity");
        if (!double.TryParse(similarityText, NumberStyles.Float, CultureInfo.InvariantCulture, out var similarity))
            similarity = 0;
        var pixivId = GetString(data, "pixiv_id");
        var authorId = GetString(data, "member_id");
        var authorName = GetString(data, "member_name");
        if (string.IsNullOrWhiteSpace(pixivId) || string.IsNullOrWhiteSpace(authorId) || string.IsNullOrWhiteSpace(authorName))
            return null;
        return new SauceNaoPixivResult(pixivId, GetString(data, "title"), authorName, authorId, similarity,
            GetString(header, "thumbnail"), GetFirstString(data, "ext_urls") ?? $"https://www.pixiv.net/artworks/{pixivId}");
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)) return null;
        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            _ => null,
        };
    }

    private static string? GetFirstString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in property.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String) return item.GetString();
        return null;
    }
}

internal sealed record SauceNaoPixivResult(string PixivId, string? Title, string AuthorName, string AuthorId,
    double Similarity, string? ThumbnailUrl, string? SourceUrl);

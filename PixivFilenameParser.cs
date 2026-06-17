using System.Text.RegularExpressions;
using SceneGallery.PluginSdk;

namespace SceneGallery.Plugin.PixivAuthors;

/// <summary>
/// Extracts a pixiv artwork id from downloaded filenames like "123456789_p0.png",
/// "123456789_ArtworkTitle_p0.png", or manga format "001_123456789_p0.png".
/// Pure string work, safe on hot paths.
/// </summary>
public static partial class PixivFilenameParser
{
    // Pixiv artwork id is 6-12 digits followed by an underscore or hyphen.
    // It appears at the start ("123456789_p0.png") or after a short numeric
    // prefix used by manga downloads ("001_123456789_p0.png").
    [GeneratedRegex(@"^(?:\d{1,5}_)?(?<id>\d{6,12})(?:_|-)", RegexOptions.CultureInvariant)]
    private static partial Regex ArtworkFilename();

    public static ArtworkId? TryParse(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;

        var match = ArtworkFilename().Match(fileName);
        if (!match.Success) return null;

        var id = match.Groups["id"].Value.TrimStart('0');
        if (id.Length == 0) return null;

        return new ArtworkId(PixivFolderNameParser.ProviderId, id);
    }

    public static ArtworkId? TryParseUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || !IsPixivHost(uri.Host))
        {
            return null;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();

        var artworksIndex = Array.FindIndex(segments, s => string.Equals(s, "artworks", StringComparison.OrdinalIgnoreCase));
        if (artworksIndex >= 0 && artworksIndex + 1 < segments.Length)
            return CreateArtworkId(segments[artworksIndex + 1]);

        var shortIndex = Array.FindIndex(segments, s => string.Equals(s, "i", StringComparison.OrdinalIgnoreCase));
        if (shortIndex >= 0 && shortIndex + 1 < segments.Length)
            return CreateArtworkId(segments[shortIndex + 1]);

        return CreateArtworkId(GetQueryValue(uri.Query, "illust_id"));
    }

    private static bool IsPixivHost(string host) =>
        string.Equals(host, "pixiv.net", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".pixiv.net", StringComparison.OrdinalIgnoreCase);

    private static string? GetQueryValue(string query, string key)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var name = Uri.UnescapeDataString(parts[0]);
            if (!string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
                continue;

            return parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
        }

        return null;
    }

    private static ArtworkId? CreateArtworkId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var id = value.Trim();
        if (!id.All(char.IsDigit)) return null;

        id = id.TrimStart('0');
        return id.Length == 0 ? null : new ArtworkId(PixivFolderNameParser.ProviderId, id);
    }
}

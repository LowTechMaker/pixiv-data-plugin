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
}

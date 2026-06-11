using System.Text.RegularExpressions;
using SceneGallery.PluginSdk;

namespace SceneGallery.Plugin.PixivAuthors;

/// <summary>
/// Extracts a pixiv artwork id from downloaded filenames like "123456789_p0.png"
/// or "123456789_ArtworkTitle_p0.png". Pure string work, safe on hot paths.
/// </summary>
public static partial class PixivFilenameParser
{
    // Pixiv downloads and gallery-dl both put the numeric artwork id at the
    // start of the filename, followed by an underscore or hyphen. We require
    // 6-12 digits to avoid false positives on short numbers.
    [GeneratedRegex(@"^(?<id>\d{6,12})(?:_|-)", RegexOptions.CultureInvariant)]
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

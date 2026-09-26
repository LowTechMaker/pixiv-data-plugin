namespace SceneGallery.Plugin.PixivAuthors;

/// <summary>
/// Maps the app's BCP-47 language onto the codes pixiv's endpoints accept.
/// </summary>
/// <remarks>
/// Kept here rather than in the app: which codes exist, and that traditional
/// Chinese is "zh_tw" rather than "zh-Hant", is pixiv's business. The app only
/// ever says what language the user reads.
/// </remarks>
internal static class PixivTagLanguage
{
    /// <summary>What pixiv is asked for when nothing better matches.</summary>
    public const string Default = "en";

    /// <summary>
    /// The pixiv language code for <paramref name="bcp47"/>. Falls back to
    /// English, which is the language pixiv has most translations in — an
    /// English label still beats showing the raw Japanese tag.
    /// </summary>
    public static string From(string? bcp47)
    {
        if (string.IsNullOrWhiteSpace(bcp47)) return Default;

        var tag = bcp47.Trim().Replace('_', '-');

        // Script subtag first: zh-Hant-HK and zh-TW must land on the same code,
        // and a region test alone would miss the former.
        if (tag.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return HasSubtag(tag, "Hant") || HasSubtag(tag, "TW") || HasSubtag(tag, "HK") || HasSubtag(tag, "MO")
                ? "zh_tw"
                : "zh";
        }

        var primary = tag.Split('-')[0].ToLowerInvariant();
        return primary switch
        {
            "ja" => "ja",
            "ko" => "ko",
            "en" => "en",
            _ => Default,
        };
    }

    private static bool HasSubtag(string tag, string subtag)
    {
        foreach (var part in tag.Split('-'))
        {
            if (string.Equals(part, subtag, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}

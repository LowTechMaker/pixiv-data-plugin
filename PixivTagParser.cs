using System.Text.Json;
using SceneGallery.PluginSdk;

namespace SceneGallery.Plugin.PixivAuthors;

/// <summary>
/// Reads one <c>/ajax/search/tags/{tag}</c> response into a <see cref="TagArticle"/>.
/// </summary>
/// <remarks>
/// Pure string work so the whole shape — including the redirect rule, which is
/// the subtle part — is testable without a network.
///
/// The response is probed with <see cref="JsonDocument"/> rather than bound to
/// a model, matching <see cref="PixivApiClient"/>: the endpoint is unofficial
/// and an unrelated field appearing must not break the parse.
/// </remarks>
internal static class PixivTagParser
{
    /// <summary>
    /// Parses <paramref name="json"/>. Returns null on a malformed body or an
    /// error response; returns an article with no <see cref="TagArticle.Summary"/>
    /// when pixiv simply has nothing written about the tag, which is ordinary
    /// and still carries a usable translation.
    /// </summary>
    public static TagArticle? Parse(string json, string requestedTag, string pixivLanguage)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.True)
            {
                return null;
            }
            if (!root.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Object)
                return null;

            var parents = ParentTags(BreadcrumbTags(body), requestedTag);
            var pediaParent = body.TryGetProperty("pixpedia", out var pp) && pp.ValueKind == JsonValueKind.Object
                ? NonEmpty(pp, "parentTag")
                : null;
            // Either source names the same tag; pixpedia is used when the
            // breadcrumb is missing, which happens for tags with no category.
            var nearestParent = parents.Count > 0 ? parents[^1] : pediaParent;
            var pedia = body.TryGetProperty("pixpedia", out var p) && p.ValueKind == JsonValueKind.Object
                ? p
                : default;

            return new TagArticle(
                Tag: requestedTag,
                Translation: Translation(body, requestedTag, pixivLanguage),
                Reading: NonEmpty(pedia, "yomigana"),
                Summary: NonEmpty(pedia, "abstract"),
                ImageUrl: NonEmpty(pedia, "image"),
                ParentTags: parents,
                ChildTags: StringList(pedia, "childrenTags"),
                ArticleUrl: ArticleUrl(requestedTag),
                AliasOf: PixivAliasDetector.IsAliasOf(NonEmpty(pedia, "abstract"), nearestParent)
                    ? nearestParent
                    : null);
        }
    }

    /// <summary>Browser address of the encyclopedia article for a tag.</summary>
    public static string ArticleUrl(string tag) => "https://dic.pixiv.net/a/" + Uri.EscapeDataString(tag);

    /// <summary>
    /// The category path leading to the tag, outermost first and without the
    /// tag itself.
    /// </summary>
    /// <remarks>
    /// The breadcrumb path always ends at the tag that was asked for — including
    /// for an alias, where the entry it points at sits one step before the end.
    /// So the innermost parent is what an alias redirects to, and also what a
    /// genuine sub-topic belongs under; the payload gives no way to tell those
    /// apart, which is why nothing here tries to.
    ///
    /// The trailing entry is dropped defensively rather than blindly: nothing is
    /// removed if the path does not in fact end at the requested tag, so a
    /// change in pixiv's shape costs an extra breadcrumb rather than a missing
    /// parent.
    /// </remarks>
    private static IReadOnlyList<string> ParentTags(IReadOnlyList<string> chain, string requestedTag)
    {
        if (chain.Count == 0) return [];
        var withoutSelf = string.Equals(chain[^1], requestedTag, StringComparison.Ordinal)
            ? chain.Take(chain.Count - 1)
            : chain;
        return [.. withoutSelf];
    }

    private static IReadOnlyList<string> BreadcrumbTags(JsonElement body)
    {
        if (!body.TryGetProperty("breadcrumbs", out var crumbs) || crumbs.ValueKind != JsonValueKind.Object)
            return [];
        if (!crumbs.TryGetProperty("successor", out var successor) || successor.ValueKind != JsonValueKind.Array)
            return [];

        var tags = new List<string>();
        foreach (var step in successor.EnumerateArray())
        {
            if (step.ValueKind != JsonValueKind.Object) continue;
            if (step.TryGetProperty("tag", out var tag) && tag.ValueKind == JsonValueKind.String
                && tag.GetString() is { Length: > 0 } name)
            {
                tags.Add(name);
            }
        }
        return tags;
    }

    /// <summary>
    /// The tag in the requested language.
    /// </summary>
    /// <remarks>
    /// Two sources disagree, and the less obvious one is the right one.
    /// <c>tagTranslation</c> holds only translations written specifically for
    /// each language and leaves the rest as empty strings — asking for zh_tw
    /// gives "" for 東方Project. The breadcrumb entry for the same tag carries
    /// the value pixiv itself would display, already fallen back to English
    /// ("Touhou Project"). So breadcrumbs first, tagTranslation second, and an
    /// empty string is treated as absent in both.
    /// </remarks>
    private static string? Translation(JsonElement body, string requestedTag, string pixivLanguage)
    {
        if (body.TryGetProperty("breadcrumbs", out var crumbs) && crumbs.ValueKind == JsonValueKind.Object
            && crumbs.TryGetProperty("successor", out var successor) && successor.ValueKind == JsonValueKind.Array)
        {
            foreach (var step in successor.EnumerateArray())
            {
                if (step.ValueKind != JsonValueKind.Object) continue;
                if (!step.TryGetProperty("tag", out var tag) || tag.ValueKind != JsonValueKind.String) continue;
                if (!string.Equals(tag.GetString(), requestedTag, StringComparison.Ordinal)) continue;
                if (step.TryGetProperty("translation", out var translation)
                    && translation.ValueKind == JsonValueKind.Object)
                {
                    var value = NonEmpty(translation, pixivLanguage);
                    if (value is not null) return Differing(value, requestedTag);
                }
            }
        }

        if (body.TryGetProperty("tagTranslation", out var all) && all.ValueKind == JsonValueKind.Object
            && all.TryGetProperty(requestedTag, out var forTag) && forTag.ValueKind == JsonValueKind.Object)
        {
            var value = NonEmpty(forTag, pixivLanguage) ?? NonEmpty(forTag, "en");
            if (value is not null) return Differing(value, requestedTag);
        }

        return null;
    }

    /// <summary>
    /// Drops a "translation" that is just the tag again, so the app can rely on
    /// a non-null translation meaning there is something else to show.
    /// </summary>
    private static string? Differing(string value, string requestedTag)
        => string.Equals(value, requestedTag, StringComparison.Ordinal) ? null : value;

    private static string? NonEmpty(JsonElement parent, string property)
    {
        if (parent.ValueKind != JsonValueKind.Object) return null;
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static IReadOnlyList<string> StringList(JsonElement parent, string property)
    {
        if (parent.ValueKind != JsonValueKind.Object) return [];
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
            return [];

        var items = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
                items.Add(text);
        }
        return items;
    }
}

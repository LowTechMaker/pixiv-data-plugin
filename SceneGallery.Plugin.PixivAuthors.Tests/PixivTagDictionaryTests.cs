using SceneGallery.PluginSdk;
using System.Text.Json;

namespace SceneGallery.Plugin.PixivAuthors.Tests;

/// <summary>
/// The tag encyclopedia payload, parsed offline. The samples are trimmed
/// copies of real <c>/ajax/search/tags/</c> responses — including the two
/// shapes that are easy to get wrong: a tag that redirects, and a tag pixiv
/// has never heard of.
/// </summary>
public sealed class PixivTagParserTests
{
    /// <summary>
    /// An ordinary tag: the breadcrumb path ends at the tag itself, so it is
    /// nobody's alias.
    /// </summary>
    private const string TouhouProject = """
    {"error":false,"body":{
      "tag":"東方Project","word":"東方Project",
      "pixpedia":{"abstract":"『東方Project』とは、同人サークル「上海アリス幻樂団」が展開する作品群である。",
        "image":"https://i.pximg.net/x.jpg","id":1,"yomigana":"とうほうぷろじぇくと",
        "childrenTags":["東方","幻想郷"],"tag":"東方Project"},
      "breadcrumbs":{"successor":[
        {"tag":"同人","translation":{"zh_tw":"doujin"}},
        {"tag":"東方Project","translation":{"zh_tw":"Touhou Project"}}],"current":[]},
      "tagTranslation":{"東方Project":{"en":"","zh_tw":"","romaji":"touhoupurojekuto"}}}}
    """;

    /// <summary>
    /// An alias. Verbatim shape from the live endpoint: the breadcrumb ends at
    /// the requested word like every other tag, and the entry it points at is
    /// the one before it. Nothing in the payload marks it as an alias — only
    /// the prose does.
    /// </summary>
    private const string TouhouAlias = """
    {"error":false,"body":{
      "tag":"Touhou","word":"Touhou",
      "pixpedia":{"abstract":"「東方」のローマ字表記。","parentTag":"東方","tag":"Touhou"},
      "breadcrumbs":{"successor":[
        {"tag":"同人","translation":{"zh_tw":"doujin"}},
        {"tag":"東方","translation":{"zh_tw":"東方"}},
        {"tag":"Touhou","translation":{"zh_tw":"Touhou"}}],"current":[]},
      "tagTranslation":{}}}
    """;

    /// <summary>A tag with no article: no error, just an empty abstract.</summary>
    private const string Unknown = """
    {"error":false,"body":{
      "tag":"zzqqxx","word":"zzqqxx","pixpedia":{"abstract":"","tag":"zzqqxx"},
      "breadcrumbs":{"successor":[{"tag":"zzqqxx","translation":{"zh_tw":"zzqqxx"}}],"current":[]},
      "tagTranslation":{}}}
    """;

    [Fact]
    public void TheArticleLinkPointsAtTheTagThatWasAskedFor()
    {
        var article = PixivTagParser.Parse(TouhouProject, "東方Project", "zh_tw");

        Assert.NotNull(article);
        Assert.True(article.HasArticle);
        Assert.Equal("https://dic.pixiv.net/a/" + Uri.EscapeDataString("東方Project"), article.ArticleUrl);
    }

    [Fact]
    public void AnAliasOffersWhatItPointsAtAsItsInnermostParent()
    {
        // The only handle on a redirect this payload gives. "Touhou"'s own
        // article says just that it is a romanisation, so the parent is what
        // the reader actually wants, and the app links it.
        var article = PixivTagParser.Parse(TouhouAlias, "Touhou", "zh_tw");

        Assert.NotNull(article);
        Assert.Equal("東方", article.ParentTags[^1]);
    }

    [Fact]
    public void TheTagsOwnEntryIsNotListedAsItsParent()
    {
        var article = PixivTagParser.Parse(TouhouProject, "東方Project", "zh_tw");

        Assert.Equal(["同人"], article!.ParentTags);
        Assert.Equal(["東方", "幻想郷"], article.ChildTags);
    }

    [Fact]
    public void ABreadcrumbThatDoesNotEndAtTheTagKeepsAllOfIt()
    {
        // Defensive: the path ending elsewhere would mean pixiv changed shape,
        // and dropping a real parent is worse than keeping an extra one.
        const string oddShape = """
        {"error":false,"body":{"tag":"x","word":"x","pixpedia":{"abstract":"a"},
          "breadcrumbs":{"successor":[{"tag":"p"},{"tag":"q"}],"current":[]},
          "tagTranslation":{}}}
        """;

        Assert.Equal(["p", "q"], PixivTagParser.Parse(oddShape, "x", "en")!.ParentTags);
    }

    [Fact]
    public void TheBreadcrumbTranslationWinsOverTheEmptyOne()
    {
        // tagTranslation carries "" for zh_tw here, while the breadcrumb has the
        // value pixiv itself would show. Reading the first would leave the tag
        // untranslated for no reason.
        var article = PixivTagParser.Parse(TouhouProject, "東方Project", "zh_tw");

        Assert.Equal("Touhou Project", article!.Translation);
    }

    [Fact]
    public void ATranslationEqualToTheTagCountsAsNoTranslation()
    {
        // pixiv echoes the tag back when it has nothing; the app relies on a
        // non-null translation meaning there is something else to show.
        var article = PixivTagParser.Parse(Unknown, "zzqqxx", "zh_tw");

        Assert.NotNull(article);
        Assert.Null(article.Translation);
    }

    [Fact]
    public void AnUnknownTagParsesWithoutAnArticle()
    {
        var article = PixivTagParser.Parse(Unknown, "zzqqxx", "zh_tw");

        Assert.NotNull(article);
        Assert.False(article.HasArticle);
    }

    [Fact]
    public void AnErrorResponseIsNotAnAnswer()
        => Assert.Null(PixivTagParser.Parse("""{"error":true,"message":"nope","body":[]}""", "x", "en"));

    [Fact]
    public void MalformedJsonIsNotAnAnswer()
        => Assert.Null(PixivTagParser.Parse("{ not json", "x", "en"));

    [Fact]
    public void AnUnexpectedShapeIsNotAnAnswer()
        => Assert.Null(PixivTagParser.Parse("""{"error":false}""", "x", "en"));

    [Fact]
    public void UnrelatedNewFieldsDoNotBreakTheParse()
    {
        using var doc = JsonDocument.Parse(TouhouProject);
        var padded = TouhouProject.Replace(
            """"tag":"東方Project","word"""", """"somethingNew":{"a":1},"tag":"東方Project","word"""");

        Assert.NotNull(PixivTagParser.Parse(padded, "東方Project", "zh_tw"));
    }
}

public sealed class PixivTagLanguageTests
{
    [Theory]
    [InlineData("zh-Hant", "zh_tw")]
    [InlineData("zh-TW", "zh_tw")]
    [InlineData("zh-Hant-HK", "zh_tw")]
    [InlineData("zh-Hans", "zh")]
    [InlineData("zh-CN", "zh")]
    [InlineData("zh", "zh")]
    [InlineData("en-US", "en")]
    [InlineData("ja-JP", "ja")]
    [InlineData("ko", "ko")]
    public void AppLanguagesMapOntoPixivCodes(string bcp47, string expected)
        => Assert.Equal(expected, PixivTagLanguage.From(bcp47));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("de-DE")]
    public void AnythingElseAsksForEnglish(string? bcp47)
        // English is what pixiv has most translations in, so an English label
        // still beats showing the raw Japanese tag.
        => Assert.Equal("en", PixivTagLanguage.From(bcp47));

    [Fact]
    public void TraditionalAndSimplifiedDoNotCollide()
        => Assert.NotEqual(PixivTagLanguage.From("zh-Hant"), PixivTagLanguage.From("zh-Hans"));
}

public sealed class TagDiskCacheTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tagcache").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private TagDiskCache NewCache() => new(_dir, _ => { }, (path, write) =>
    {
        using var stream = File.Create(path);
        write(stream);
    });

    private static TagArticle Article(string tag, string? translation) =>
        new(tag, translation, null, "summary", null, [], [], "https://dic.pixiv.net/a/" + tag);

    [Fact]
    public void OneLanguageDoesNotAnswerForAnother()
    {
        // Without the language in the key, switching the UI to Chinese would
        // keep handing back the English labels already cached.
        using var cache = NewCache();
        cache.Set("原神", "en", Article("原神", "Genshin Impact"));

        Assert.False(cache.TryGet("原神", "zh_tw", out _));
        Assert.True(cache.TryGet("原神", "en", out var english));
        Assert.Equal("Genshin Impact", english!.Translation);
    }

    [Fact]
    public void ACachedMissIsRememberedAsKnownRatherThanUnknown()
    {
        using var cache = NewCache();
        cache.Set("zzqqxx", "en", null);

        Assert.True(cache.TryGet("zzqqxx", "en", out var article));
        Assert.Null(article);
    }

    [Fact]
    public void EntriesSurviveAReload()
    {
        using (var cache = NewCache())
        {
            cache.Set("原神", "zh_tw", Article("原神", "原神"));
            cache.Flush();
        }

        using var reloaded = NewCache();
        Assert.True(reloaded.TryGet("原神", "zh_tw", out var article));
        Assert.Equal("原神", article!.Translation);
    }

    [Fact]
    public void EveryFieldSurvivesTheCache()
    {
        // Written after AliasOf was added to the contract but not to the cache
        // record, so the alias judgement was made correctly and then dropped on
        // the way to disk — the feature silently did nothing. Reflection rather
        // than a field list, so the next field added fails here instead.
        var article = new TagArticle(
            Tag: "koikatsu!",
            Translation: "戀活！",
            Reading: "こいかつ",
            Summary: "「コイカツ!」のローマ字表記。",
            ImageUrl: "https://i.pximg.net/x.jpg",
            ParentTags: ["ゲーム", "コイカツ!"],
            ChildTags: ["子"],
            ArticleUrl: "https://dic.pixiv.net/a/koikatsu!",
            AliasOf: "コイカツ!");

        using (var cache = NewCache())
        {
            cache.Set(article.Tag, "zh_tw", article);
            cache.Flush();
        }

        using var reloaded = NewCache();
        Assert.True(reloaded.TryGet(article.Tag, "zh_tw", out var restored));

        foreach (var property in typeof(TagArticle).GetProperties())
        {
            var expected = property.GetValue(article);
            var actual = property.GetValue(restored);
            if (expected is System.Collections.IEnumerable and not string)
                Assert.Equal((System.Collections.IEnumerable)expected!, (System.Collections.IEnumerable)actual!);
            else
                Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void AnEntryWrittenBeforeTheCurrentSchemaIsFetchedAgain()
    {
        // Verbatim shape of a real entry cached before AliasOf existed. Found
        // articles never expire, so it answered "no alias" for good and
        // koikatsu was never folded into コイカツ!.
        File.WriteAllText(Path.Combine(_dir, "tags.json"), """
            {"zh_tw\u0000koikatsu":{"Tag":"koikatsu","Translation":null,"Reading":null,
             "Summary":"「コイカツ!」のローマ字表記。","ImageUrl":null,"ParentTags":["コイカツ!"],
             "ChildTags":[],"ArticleUrl":"https://dic.pixiv.net/a/koikatsu",
             "FetchedAt":"2026-09-22T09:12:13+00:00","Missing":false}}
            """);

        using var cache = NewCache();
        Assert.False(cache.TryGet("koikatsu", "zh_tw", out _));

        cache.Set("koikatsu", "zh_tw", Article("koikatsu", null));
        Assert.True(cache.TryGet("koikatsu", "zh_tw", out _));
    }

    [Fact]
    public void AnUnreadableCacheStartsFreshInsteadOfThrowing()
    {
        File.WriteAllText(Path.Combine(_dir, "tags.json"), "{ not json");

        using var cache = NewCache();
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void ATagCannotForgeAnotherLanguagesEntry()
    {
        // Keys are language-first and separated by a character no tag contains.
        using var cache = NewCache();
        Assert.NotEqual(TagDiskCache.KeyOf("a", "enb"), TagDiskCache.KeyOf("ba", "en"));
    }
}

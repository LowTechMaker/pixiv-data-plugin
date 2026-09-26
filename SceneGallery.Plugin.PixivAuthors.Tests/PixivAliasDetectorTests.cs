namespace SceneGallery.Plugin.PixivAuthors.Tests;

/// <summary>
/// Every case here is a real pixiv article, taken verbatim from the tags of an
/// actual library, together with the parent pixiv reports for it.
/// </summary>
/// <remarks>
/// The whole feature rests on telling these two groups apart, and pixiv marks
/// neither. They were collected precisely because the parent relation does not
/// separate them: every tag below has a parent that is also a tag in the same
/// cloud, so a rule based on the parent alone merges all of them.
///
/// The rejections matter more than the acceptances. シーン配布(コイカツ!) is
/// the largest tag in that library at 2224 artworks and its parent is コイカツ!;
/// claiming it as an alias would make those cards disappear into another tag.
/// </remarks>
public sealed class PixivAliasDetectorTests
{
    [Theory]
    // A romanisation, the case that started this.
    [InlineData("「コイカツ!」のローマ字表記。", "コイカツ!")]
    // The same article reached through a different spelling of the tag.
    [InlineData("「コイカツ!」の漢字表記。", "コイカツ!")]
    // An English title rather than a transliteration.
    [InlineData("『原神』の英語タイトル。", "原神")]
    // An abbreviation, buried after a clause — the naming phrase still governs.
    [InlineData("複数の意味があるが、pixivでは『Fate/GrandOrder』の略として使われている。", "Fate/GrandOrder")]
    // The mirrored shape: the naming noun first, the real title after は.
    [InlineData("ILLUSIONの3Dアダルトゲーム。正式タイトルは『コイカツ!』", "コイカツ!")]
    // An orthographic variant, unquoted.
    [InlineData("けもみみとは、獣耳の表記揺れである。", "獣耳")]
    public void AnArticleThatOnlyNamesAnotherTagIsAnAlias(string summary, string parent)
        => Assert.True(PixivAliasDetector.IsAliasOf(summary, parent));

    [Theory]
    // 2224 artworks in the library this came from. Its parent is コイカツ!,
    // and folding it away would be the worst thing this feature could do.
    [InlineData("ILLUSIONの3Dアダルトゲーム『コイカツ!』で作成され、利用可能なシーンデータに付けられるタグ。", "コイカツ!")]
    [InlineData("コイカツ!で使用できる衣装を配布した作品につけられるタグ。", "コイカツ!")]
    // The game itself, under the company that makes it.
    [InlineData("ILLUSIONの3Dアダルトゲーム。", "ILLUSION")]
    // A genuine sub-topic: the setting of a work, not another name for it.
    [InlineData("「東方Project」の舞台。", "東方Project")]
    // A naming noun IS present (省略形) but it names ロリータ, not the parent.
    [InlineData("ロリとはロリータの省略形、少女ないし幼女の意味で使われる。", "少女")]
    // The parent appears, preceded by a verb rather than governing a name.
    [InlineData("輪の形の回転機構。また、それを用いた乗り物。", "乗り物")]
    // The parent appears with に, as a place of belonging.
    [InlineData("樋口楓は、「にじさんじ」に所属するバーチャルライバーのひとりである。", "にじさんじ")]
    // A narrower age band, described in its own terms.
    [InlineData("年少の女子。7歳前後から18歳前後までの、成年に達しない女子を指す。乙女。", "女の子")]
    public void AnArticleThatDescribesItsOwnTopicIsNotAnAlias(string summary, string parent)
        => Assert.False(PixivAliasDetector.IsAliasOf(summary, parent));

    [Theory]
    [InlineData(null, "コイカツ!")]
    [InlineData("", "コイカツ!")]
    [InlineData("   ", "コイカツ!")]
    [InlineData("「コイカツ!」のローマ字表記。", null)]
    [InlineData("「コイカツ!」のローマ字表記。", "")]
    public void NothingToGoOnMeansNoClaim(string? summary, string? parent)
        => Assert.False(PixivAliasDetector.IsAliasOf(summary, parent));

    [Fact]
    public void ANamingNounFarFromTheParentDoesNotCount()
    {
        // The window after の exists so a noun elsewhere in a long descriptive
        // sentence cannot be read as the predicate.
        const string summary = "コイカツ!のユーザーが作った作品で、作者によっては別名を名乗ることもある。";

        Assert.False(PixivAliasDetector.IsAliasOf(summary, "コイカツ!"));
    }

    [Fact]
    public void AParentMentionedOnlyInPassingDoesNotCount()
        => Assert.False(PixivAliasDetector.IsAliasOf("原神に登場するキャラクター。", "原神"));
}

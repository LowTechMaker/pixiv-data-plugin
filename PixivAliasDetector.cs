namespace SceneGallery.Plugin.PixivAuthors;

/// <summary>
/// Decides whether a tag's article says "this is another way of writing X"
/// rather than describing a topic of its own.
/// </summary>
/// <remarks>
/// pixiv marks aliases nowhere in its data. <c>parentTag</c> and the breadcrumb
/// path point at the target for a romanisation exactly as they point at the
/// broader topic for a genuine sub-tag — 幻想郷's parent is 東方Project and
/// koikatsu!'s parent is コイカツ!, and nothing but the prose separates them.
/// Merging on the parent alone would fold this library's largest tag,
/// シーン配布(コイカツ!), into コイカツ!.
///
/// What does separate them is grammatical. An alias article's whole assertion
/// is a genitive naming phrase: 「コイカツ!」<b>の</b>ローマ字表記 — "X's
/// romanisation". So the test is for the parent standing immediately before
/// の and a naming noun following it, plus the mirrored 正式タイトルは『X』.
/// A describing article never has that shape: ILLUSIONの3Dアダルトゲーム ends
/// in a thing, not in a name for something else.
///
/// This is a judgement about prose and it is deliberately conservative. A tag
/// it misses stays separate, which costs a duplicate word in the cloud; a tag
/// it wrongly claims would silently swallow a real tag's cards, which is worse.
/// Every rule here is pinned by a real article in the tests.
/// </remarks>
internal static class PixivAliasDetector
{
    /// <summary>
    /// Nouns that name a way of referring to something, rather than naming a
    /// thing. 表記 covers ローマ字表記 / 漢字表記 / 別表記 / 表記揺れ, and 略
    /// covers 略 / 略称 / 略記.
    /// </summary>
    private static readonly string[] NamingNouns =
    [
        "表記", "タイトル", "略", "別名", "別称", "呼称", "愛称", "読み",
        "旧称", "旧名", "綴り", "英題", "邦題", "誤字", "異名",
    ];

    /// <summary>
    /// How far past の a naming noun may sit. Long enough for a qualifier
    /// (ローマ字, 英語, 正式), short enough that a noun elsewhere in a
    /// descriptive sentence cannot be mistaken for the predicate.
    /// </summary>
    private const int NounWindow = 8;

    /// <summary>
    /// Whether <paramref name="summary"/> declares the tag to be a name for
    /// <paramref name="parent"/>.
    /// </summary>
    public static bool IsAliasOf(string? summary, string? parent)
    {
        if (string.IsNullOrWhiteSpace(summary) || string.IsNullOrWhiteSpace(parent)) return false;
        return DeclaredAsGenitiveName(summary, parent) || DeclaredAsNamedTitle(summary, parent);
    }

    /// <summary>「X」のローマ字表記 — the parent, then の, then a naming noun.</summary>
    private static bool DeclaredAsGenitiveName(string summary, string parent)
    {
        var from = 0;
        while (true)
        {
            var at = summary.IndexOf(parent, from, StringComparison.Ordinal);
            if (at < 0) return false;

            var after = at + parent.Length;
            // The parent is usually quoted; step over the closing bracket so
            // 『原神』の英語タイトル reads the same as 獣耳の表記揺れ.
            if (after < summary.Length && IsClosingQuote(summary[after])) after++;

            if (after < summary.Length && summary[after] == 'の'
                && ContainsNamingNoun(summary, after + 1, NounWindow))
            {
                return true;
            }

            from = at + 1;
        }
    }

    /// <summary>正式タイトルは『X』 — the naming noun first, the parent after は.</summary>
    private static bool DeclaredAsNamedTitle(string summary, string parent)
    {
        foreach (var noun in NamingNouns)
        {
            var from = 0;
            while (true)
            {
                var at = summary.IndexOf(noun, from, StringComparison.Ordinal);
                if (at < 0) break;

                var after = at + noun.Length;
                if (after < summary.Length && summary[after] == 'は')
                {
                    var rest = after + 1;
                    if (rest < summary.Length && IsOpeningQuote(summary[rest])) rest++;
                    if (rest < summary.Length
                        && summary.AsSpan(rest).StartsWith(parent, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }

                from = at + 1;
            }
        }
        return false;
    }

    private static bool ContainsNamingNoun(string summary, int start, int window)
    {
        if (start >= summary.Length) return false;
        var length = Math.Min(window, summary.Length - start);
        var slice = summary.Substring(start, length);
        foreach (var noun in NamingNouns)
        {
            if (slice.Contains(noun, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static bool IsClosingQuote(char c) => c is '」' or '』' or '"' or '\'' or '）' or ')';

    private static bool IsOpeningQuote(char c) => c is '「' or '『' or '"' or '\'' or '（' or '(';
}

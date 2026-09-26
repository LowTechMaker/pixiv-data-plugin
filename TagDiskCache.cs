using System.Collections.Concurrent;
using System.Text.Json;
using SceneGallery.PluginSdk;

namespace SceneGallery.Plugin.PixivAuthors;

/// <summary>
/// Disk-backed tag encyclopedia cache (tags.json in the plugin's storage
/// directory). Same shape as <see cref="ArtworkDiskCache"/>: debounced saves
/// through an injectable atomic writer, misses kept with a TTL.
/// </summary>
/// <remarks>
/// This cache is not an optimisation, it is what makes the feature usable. A
/// cloud is sixty tags and the shared rate limiter spaces requests two to five
/// seconds apart, so a cold lookup of a whole platform takes minutes. Filling
/// it once and keeping it is the difference between "the labels arrive while
/// you look at the page" and "every visit waits".
///
/// Entries are keyed per language: the same tag answers differently for zh_tw
/// and en, and a single-key cache would hand the user the other language's
/// label after a switch.
/// </remarks>
internal sealed class TagDiskCache : IDisposable
{
    public sealed record CachedTagArticle(
        string Tag,
        string? Translation,
        string? Reading,
        string? Summary,
        string? ImageUrl,
        IReadOnlyList<string>? ParentTags,
        IReadOnlyList<string>? ChildTags,
        string? ArticleUrl,
        DateTimeOffset FetchedAt,
        bool Missing,
        string? AliasOf = null,
        int Schema = 0)
    {
        public TagArticle ToArticle() => new(
            Tag, Translation, Reading, Summary, ImageUrl,
            ParentTags ?? [], ChildTags ?? [], ArticleUrl, AliasOf);

        public static CachedTagArticle From(TagArticle article) => new(
            article.Tag, article.Translation, article.Reading,
            article.Summary, article.ImageUrl, article.ParentTags, article.ChildTags,
            article.ArticleUrl, DateTimeOffset.UtcNow, Missing: false, AliasOf: article.AliasOf,
            Schema: CurrentSchema);

        public static CachedTagArticle Miss(string tag) => new(
            tag, null, null, null, null, null, null, null,
            DateTimeOffset.UtcNow, Missing: true, Schema: CurrentSchema);
    }

    /// <summary>
    /// Bumped whenever an article gains something the old entries could not
    /// have recorded. A found article is otherwise kept forever, so without
    /// this an entry written before a field existed answers "none" for it for
    /// good: every tag cached before <c>AliasOf</c> — コイカツ, koikatsu,
    /// koikatsu! among them — kept a null alias and the fold never happened.
    /// Older entries (no Schema on disk reads as 0) are treated as absent and
    /// fetched again.
    /// </summary>
    /// <remarks>1: AliasOf.</remarks>
    internal const int CurrentSchema = 1;

    /// <summary>
    /// How long a "pixiv has nothing for this tag" answer is trusted. Shorter
    /// than the artwork cache's week: encyclopedia articles get written, and a
    /// tag that gained one should not stay blank for long.
    /// </summary>
    private static readonly TimeSpan MissTtl = TimeSpan.FromDays(3);

    private readonly ConcurrentDictionary<string, CachedTagArticle> _entries = new(StringComparer.Ordinal);
    private readonly string _cachePath;
    private readonly Action<string> _log;
    private readonly DebouncedDiskPersistence _persistence;

    public TagDiskCache(string storageDirectory, Action<string> log)
        : this(storageDirectory, log, AtomicFileWriter.Write)
    {
    }

    internal TagDiskCache(
        string storageDirectory,
        Action<string> log,
        Action<string, Action<Stream>> writeAtomically)
    {
        _cachePath = Path.Combine(storageDirectory, "tags.json");
        _log = log;
        _persistence = new DebouncedDiskPersistence(
            _cachePath,
            stream => JsonSerializer.Serialize(stream, _entries),
            ex => _log($"tag cache save failed: {ex.Message}"),
            writeAtomically);
        Load();
    }

    /// <summary>
    /// Language first so the key cannot collide across languages, and a tag
    /// containing the separator cannot forge another entry.
    /// </summary>
    internal static string KeyOf(string tag, string pixivLanguage) => pixivLanguage + "\u0000" + tag;

    /// <summary>
    /// The cached article, or null when absent or expired. A cached miss
    /// resolves to a non-null <paramref name="known"/> with a null article, so
    /// callers can tell "nothing yet" from "asked already, nothing there".
    /// </summary>
    public bool TryGet(string tag, string pixivLanguage, out TagArticle? article)
    {
        article = null;
        var key = KeyOf(tag, pixivLanguage);
        if (!_entries.TryGetValue(key, out var entry)) return false;

        if (entry.Schema < CurrentSchema)
        {
            _entries.TryRemove(key, out _);
            return false;
        }

        if (entry.Missing)
        {
            if (DateTimeOffset.UtcNow - entry.FetchedAt > MissTtl)
            {
                _entries.TryRemove(key, out _);
                return false;
            }
            return true;
        }

        article = entry.ToArticle();
        return true;
    }

    public void Set(string tag, string pixivLanguage, TagArticle? article)
    {
        _entries[KeyOf(tag, pixivLanguage)] =
            article is null ? CachedTagArticle.Miss(tag) : CachedTagArticle.From(article);
        _persistence.MarkDirty();
    }

    internal bool IsDirty => _persistence.IsDirty;

    internal int Count => _entries.Count;

    private void Load()
    {
        try
        {
            if (!File.Exists(_cachePath)) return;
            using var stream = File.OpenRead(_cachePath);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, CachedTagArticle>>(stream);
            if (loaded is null) return;
            foreach (var (key, entry) in loaded)
                _entries[key] = entry;
        }
        catch (Exception ex)
        {
            _log($"tag cache unreadable, starting fresh: {ex.Message}");
        }
    }

    internal void Flush() => _persistence.Flush();

    public void Dispose() => _persistence.Dispose();
}

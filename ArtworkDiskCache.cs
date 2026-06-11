using System.Collections.Concurrent;
using System.Text.Json;
using SceneGallery.PluginSdk;

namespace SceneGallery.Plugin.PixivAuthors;

/// <summary>
/// Disk-backed artwork cache (artworks.json in the plugin's storage directory).
/// Same pattern as <see cref="AuthorDiskCache"/>: successful entries live until
/// a forced refresh; failed lookups are cached with a 7-day TTL. Saves are
/// debounced and flushed via temp-file + move.
/// </summary>
internal sealed class ArtworkDiskCache : IDisposable
{
    public sealed record CachedArtwork(
        string? AuthorName,
        string? AuthorId,
        int XRestrict,
        IReadOnlyList<CachedTag>? Tags,
        DateTimeOffset FetchedAt,
        bool Failed);

    public sealed record CachedTag(string Name, string? TranslatedName);

    private static readonly TimeSpan SaveDebounce = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FailedEntryTtl = TimeSpan.FromDays(7);

    private readonly ConcurrentDictionary<string, CachedArtwork> _entries = new();
    private readonly string _cachePath;
    private readonly Action<string> _log;
    private readonly Timer _saveTimer;
    private readonly Lock _saveLock = new();
    private volatile bool _dirty;

    public ArtworkDiskCache(string storageDirectory, Action<string> log)
    {
        _cachePath = Path.Combine(storageDirectory, "artworks.json");
        _log = log;
        _saveTimer = new Timer(_ => Flush(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Load();
    }

    public bool TryGet(string artworkId, out CachedArtwork entry)
    {
        if (!_entries.TryGetValue(artworkId, out entry!)) return false;
        if (entry.Failed && DateTimeOffset.UtcNow - entry.FetchedAt > FailedEntryTtl)
        {
            _entries.TryRemove(artworkId, out _);
            return false;
        }
        return true;
    }

    public void Set(string artworkId, CachedArtwork entry)
    {
        _entries[artworkId] = entry;
        _dirty = true;
        _saveTimer.Change(SaveDebounce, Timeout.InfiniteTimeSpan);
    }

    public IReadOnlyList<ArtworkTag>? GetCachedTags(string artworkId)
    {
        if (!_entries.TryGetValue(artworkId, out var entry) || entry.Tags is null)
            return null;
        return entry.Tags.Select(t => new ArtworkTag(t.Name, t.TranslatedName)).ToList();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_cachePath)) return;
            using var stream = File.OpenRead(_cachePath);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, CachedArtwork>>(stream);
            if (loaded is null) return;
            foreach (var (id, entry) in loaded)
                _entries[id] = entry;
        }
        catch (Exception ex)
        {
            _log($"artwork cache unreadable, starting fresh: {ex.Message}");
        }
    }

    private void Flush()
    {
        if (!_dirty) return;
        _dirty = false;
        try
        {
            lock (_saveLock)
            {
                var tempPath = _cachePath + ".tmp";
                using (var stream = File.Create(tempPath))
                    JsonSerializer.Serialize(stream, _entries);
                File.Move(tempPath, _cachePath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            _log($"artwork cache save failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _saveTimer.Dispose();
        Flush();
    }
}

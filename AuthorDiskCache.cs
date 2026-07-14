using System.Collections.Concurrent;
using System.Text.Json;

namespace SceneGallery.Plugin.PixivAuthors;

/// <summary>
/// Disk-backed author cache (authors.json in the plugin's storage directory).
/// Successful entries live until a forced refresh; failed lookups are cached
/// with a TTL so dead ids aren't re-queried every launch. Saves are debounced
/// and flushed via temp-file + move. A corrupt cache file is discarded.
/// </summary>
internal sealed class AuthorDiskCache : IDisposable
{
    public sealed record CachedAuthor(
        string? Name,
        string? AvatarFile,
        DateTimeOffset FetchedAt,
        bool Failed);

    private static readonly TimeSpan FailedEntryTtl = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<string, CachedAuthor> _entries = new();
    private readonly string _cachePath;
    private readonly Action<string> _log;
    private readonly DebouncedDiskPersistence _persistence;

    public AuthorDiskCache(string storageDirectory, Action<string> log)
        : this(storageDirectory, log, AtomicFileWriter.Write)
    {
    }

    internal AuthorDiskCache(
        string storageDirectory,
        Action<string> log,
        Action<string, Action<Stream>> writeAtomically)
    {
        _cachePath = Path.Combine(storageDirectory, "authors.json");
        _log = log;
        _persistence = new DebouncedDiskPersistence(
            _cachePath,
            stream => JsonSerializer.Serialize(stream, _entries),
            ex => _log($"author cache save failed: {ex.Message}"),
            writeAtomically);
        Load();
    }

    public bool TryGet(string id, out CachedAuthor entry)
    {
        if (!_entries.TryGetValue(id, out entry!)) return false;
        if (entry.Failed && DateTimeOffset.UtcNow - entry.FetchedAt > FailedEntryTtl)
        {
            _entries.TryRemove(id, out _);
            return false;
        }
        return true;
    }

    public void Set(string id, CachedAuthor entry)
    {
        _entries[id] = entry;
        _persistence.MarkDirty();
    }

    internal bool IsDirty => _persistence.IsDirty;

    internal TimeSpan RetryDelay => _persistence.RetryDelay;

    private void Load()
    {
        try
        {
            if (!File.Exists(_cachePath)) return;
            using var stream = File.OpenRead(_cachePath);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, CachedAuthor>>(stream);
            if (loaded is null) return;
            foreach (var (id, entry) in loaded)
                _entries[id] = entry;
        }
        catch (Exception ex)
        {
            _log($"author cache unreadable, starting fresh: {ex.Message}");
        }
    }

    internal void Flush() => _persistence.Flush();

    public void Dispose() => _persistence.Dispose();
}

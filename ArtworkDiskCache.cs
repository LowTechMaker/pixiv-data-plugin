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
        string? Title,
        string? Description,
        int XRestrict,
        IReadOnlyList<CachedTag>? Tags,
        DateTimeOffset FetchedAt,
        bool Failed);

    public sealed record CachedTag(string Name, string? TranslatedName);

    private static readonly TimeSpan SaveDebounce = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FailedEntryTtl = TimeSpan.FromDays(7);

    private readonly ConcurrentDictionary<string, CachedArtwork> _entries = new();
    private readonly string _cachePath;
    private readonly Action<string> _log;
    private readonly Timer _saveTimer;
    private readonly Lock _saveLock = new();
    private readonly Action<string, Action<Stream>> _writeAtomically;
    private bool _dirty;
    private bool _disposed;
    private long _generation;
    private TimeSpan _retryDelay;

    public ArtworkDiskCache(string storageDirectory, Action<string> log)
        : this(storageDirectory, log, WriteAtomically)
    {
    }

    internal ArtworkDiskCache(
        string storageDirectory,
        Action<string> log,
        Action<string, Action<Stream>> writeAtomically)
    {
        _cachePath = Path.Combine(storageDirectory, "artworks.json");
        _log = log;
        _writeAtomically = writeAtomically;
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
        MarkDirty();
    }

    internal bool IsDirty
    {
        get
        {
            lock (_saveLock)
                return _dirty;
        }
    }

    internal TimeSpan RetryDelay
    {
        get
        {
            lock (_saveLock)
                return _retryDelay;
        }
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

    private void MarkDirty()
    {
        Interlocked.Increment(ref _generation);
        lock (_saveLock)
        {
            _dirty = true;
            ScheduleFlush(SaveDebounce);
        }
    }

    internal void Flush()
    {
        lock (_saveLock)
        {
            if (!_dirty) return;

            var generation = Volatile.Read(ref _generation);
            try
            {
                _writeAtomically(_cachePath, stream => JsonSerializer.Serialize(stream, _entries));
                _retryDelay = TimeSpan.Zero;
                if (generation == Volatile.Read(ref _generation))
                {
                    _dirty = false;
                }
                else
                {
                    _dirty = true;
                    ScheduleFlush(SaveDebounce);
                }
            }
            catch (Exception ex)
            {
                _dirty = true;
                _retryDelay = _retryDelay == TimeSpan.Zero
                    ? InitialRetryDelay
                    : TimeSpan.FromTicks(Math.Min(_retryDelay.Ticks * 2, MaxRetryDelay.Ticks));
                ScheduleFlush(_retryDelay);
                _log($"artwork cache save failed: {ex.Message}");
            }
        }
    }

    private void ScheduleFlush(TimeSpan delay)
    {
        if (!_disposed)
            _saveTimer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private static void WriteAtomically(string path, Action<Stream> serialize)
    {
        var tempPath = path + ".tmp";
        using (var stream = File.Create(tempPath))
            serialize(stream);
        File.Move(tempPath, path, overwrite: true);
    }

    public void Dispose()
    {
        lock (_saveLock)
        {
            if (_disposed) return;
            _disposed = true;
            _saveTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        Flush();
        _saveTimer.Dispose();
    }
}

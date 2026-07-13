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

    private static readonly TimeSpan SaveDebounce = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FailedEntryTtl = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<string, CachedAuthor> _entries = new();
    private readonly string _cachePath;
    private readonly Action<string> _log;
    private readonly Timer _saveTimer;
    private readonly Lock _saveLock = new();
    private readonly Action<string, Action<Stream>> _writeAtomically;
    private bool _dirty;
    private bool _disposed;
    private long _generation;
    private TimeSpan _retryDelay;

    public AuthorDiskCache(string storageDirectory, Action<string> log)
        : this(storageDirectory, log, WriteAtomically)
    {
    }

    internal AuthorDiskCache(
        string storageDirectory,
        Action<string> log,
        Action<string, Action<Stream>> writeAtomically)
    {
        _cachePath = Path.Combine(storageDirectory, "authors.json");
        _log = log;
        _writeAtomically = writeAtomically;
        _saveTimer = new Timer(_ => Flush(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
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
                _log($"author cache save failed: {ex.Message}");
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

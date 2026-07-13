using System.Text.Json;

namespace SceneGallery.Plugin.PixivAuthors.Tests;

public sealed class AuthorDiskCachePersistenceTests
{
    [Fact]
    public void FailedWrite_KeepsDirtyAndDoublesBackoff()
    {
        using var directory = new TempDirectory();
        var logs = new List<string>();
        using var cache = new AuthorDiskCache(
            directory.Path,
            logs.Add,
            (_, _) => throw new IOException("disk unavailable"));
        cache.Set("1", Entry("Artist"));

        cache.Flush();

        Assert.True(cache.IsDirty);
        Assert.Equal(TimeSpan.FromSeconds(5), cache.RetryDelay);
        cache.Flush();
        Assert.True(cache.IsDirty);
        Assert.Equal(TimeSpan.FromSeconds(10), cache.RetryDelay);
        for (var i = 0; i < 5; i++) cache.Flush();
        Assert.Equal(TimeSpan.FromMinutes(5), cache.RetryDelay);
        Assert.Contains(logs, message => message.Contains("disk unavailable"));
    }

    [Fact]
    public void RetryAfterFailure_PersistsAndResetsBackoff()
    {
        using var directory = new TempDirectory();
        var attempts = 0;
        using var cache = new AuthorDiskCache(directory.Path, _ => { }, (path, serialize) =>
        {
            if (++attempts == 1) throw new IOException("first attempt fails");
            CacheTestFiles.WriteAtomically(path, serialize);
        });
        cache.Set("1", Entry("Recovered"));

        cache.Flush();
        cache.Flush();

        Assert.False(cache.IsDirty);
        Assert.Equal(TimeSpan.Zero, cache.RetryDelay);
        Assert.Equal(2, attempts);
        Assert.Equal("Recovered", Read(directory.Path, "1").Name);
    }

    [Fact]
    public void SuccessfulWrite_ClearsDirty()
    {
        using var directory = new TempDirectory();
        using var cache = new AuthorDiskCache(directory.Path, _ => { }, CacheTestFiles.WriteAtomically);
        cache.Set("1", Entry("Artist"));

        cache.Flush();

        Assert.False(cache.IsDirty);
        Assert.Equal("Artist", Read(directory.Path, "1").Name);
    }

    [Fact]
    public void MutationDuringWrite_KeepsDirtyUntilLatestGenerationPersists()
    {
        using var directory = new TempDirectory();
        var attempts = 0;
        AuthorDiskCache? target = null;
        using var cache = new AuthorDiskCache(directory.Path, _ => { }, (path, serialize) =>
        {
            CacheTestFiles.WriteAtomically(path, serialize);
            if (++attempts == 1)
                target!.Set("1", Entry("New"));
        });
        target = cache;
        cache.Set("1", Entry("Old"));

        cache.Flush();

        Assert.True(cache.IsDirty);
        Assert.Equal("Old", Read(directory.Path, "1").Name);
        cache.Flush();
        Assert.False(cache.IsDirty);
        Assert.Equal("New", Read(directory.Path, "1").Name);
    }

    private static AuthorDiskCache.CachedAuthor Entry(string name)
        => new(name, null, DateTimeOffset.UtcNow, Failed: false);

    private static AuthorDiskCache.CachedAuthor Read(string directory, string id)
    {
        var json = File.ReadAllText(System.IO.Path.Combine(directory, "authors.json"));
        return JsonSerializer.Deserialize<Dictionary<string, AuthorDiskCache.CachedAuthor>>(json)![id];
    }
}

public sealed class ArtworkDiskCachePersistenceTests
{
    [Fact]
    public void FailedWrite_KeepsDirtyAndDoublesBackoff()
    {
        using var directory = new TempDirectory();
        var logs = new List<string>();
        using var cache = new ArtworkDiskCache(
            directory.Path,
            logs.Add,
            (_, _) => throw new IOException("disk unavailable"));
        cache.Set("1", Entry("Artist"));

        cache.Flush();

        Assert.True(cache.IsDirty);
        Assert.Equal(TimeSpan.FromSeconds(5), cache.RetryDelay);
        cache.Flush();
        Assert.True(cache.IsDirty);
        Assert.Equal(TimeSpan.FromSeconds(10), cache.RetryDelay);
        for (var i = 0; i < 5; i++) cache.Flush();
        Assert.Equal(TimeSpan.FromMinutes(5), cache.RetryDelay);
        Assert.Contains(logs, message => message.Contains("disk unavailable"));
    }

    [Fact]
    public void RetryAfterFailure_PersistsAndResetsBackoff()
    {
        using var directory = new TempDirectory();
        var attempts = 0;
        using var cache = new ArtworkDiskCache(directory.Path, _ => { }, (path, serialize) =>
        {
            if (++attempts == 1) throw new IOException("first attempt fails");
            CacheTestFiles.WriteAtomically(path, serialize);
        });
        cache.Set("1", Entry("Recovered"));

        cache.Flush();
        cache.Flush();

        Assert.False(cache.IsDirty);
        Assert.Equal(TimeSpan.Zero, cache.RetryDelay);
        Assert.Equal(2, attempts);
        Assert.Equal("Recovered", Read(directory.Path, "1").AuthorName);
    }

    [Fact]
    public void SuccessfulWrite_ClearsDirty()
    {
        using var directory = new TempDirectory();
        using var cache = new ArtworkDiskCache(directory.Path, _ => { }, CacheTestFiles.WriteAtomically);
        cache.Set("1", Entry("Artist"));

        cache.Flush();

        Assert.False(cache.IsDirty);
        Assert.Equal("Artist", Read(directory.Path, "1").AuthorName);
    }

    [Fact]
    public void MutationDuringWrite_KeepsDirtyUntilLatestGenerationPersists()
    {
        using var directory = new TempDirectory();
        var attempts = 0;
        ArtworkDiskCache? target = null;
        using var cache = new ArtworkDiskCache(directory.Path, _ => { }, (path, serialize) =>
        {
            CacheTestFiles.WriteAtomically(path, serialize);
            if (++attempts == 1)
                target!.Set("1", Entry("New"));
        });
        target = cache;
        cache.Set("1", Entry("Old"));

        cache.Flush();

        Assert.True(cache.IsDirty);
        Assert.Equal("Old", Read(directory.Path, "1").AuthorName);
        cache.Flush();
        Assert.False(cache.IsDirty);
        Assert.Equal("New", Read(directory.Path, "1").AuthorName);
    }

    private static ArtworkDiskCache.CachedArtwork Entry(string authorName)
        => new(authorName, "42", "Title", null, 0, [], DateTimeOffset.UtcNow, Failed: false);

    private static ArtworkDiskCache.CachedArtwork Read(string directory, string id)
    {
        var json = File.ReadAllText(System.IO.Path.Combine(directory, "artworks.json"));
        return JsonSerializer.Deserialize<Dictionary<string, ArtworkDiskCache.CachedArtwork>>(json)![id];
    }
}

internal static class CacheTestFiles
{
    public static void WriteAtomically(string path, Action<Stream> serialize)
    {
        var tempPath = path + ".test.tmp";
        using (var stream = File.Create(tempPath))
            serialize(stream);
        File.Move(tempPath, path, overwrite: true);
    }
}

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "SceneGallery.Plugin.PixivAuthors.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup for a test-only temporary directory.
        }
    }
}

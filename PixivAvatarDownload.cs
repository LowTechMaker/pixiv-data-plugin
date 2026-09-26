namespace SceneGallery.Plugin.PixivAuthors;

/// <summary>A private staging file. Only the coordinator may publish it after validating generation.</summary>
internal sealed class PixivAvatarDownload(string temporaryPath, string finalPath) : IDisposable
{
    internal string Publish()
    {
        File.Move(temporaryPath, finalPath, overwrite: true);
        return finalPath;
    }

    public void Dispose()
    {
        try { File.Delete(temporaryPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace SceneGallery.Plugin.PixivAuthors;

internal sealed class PluginSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string DestinationFolderName { get; set; } = "Pixiv";
    public bool UsesRatingFolders { get; set; } = true;
    public string? SauceNaoApiKey { get; set; }

    public static PluginSettings Load(string storageDirectory, Action<string> log)
    {
        var path = GetPath(storageDirectory);
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var document = JsonSerializer.Deserialize<SettingsDocument>(json, JsonOptions) ?? new();
                return new PluginSettings
                {
                    DestinationFolderName = document.DestinationFolderName,
                    UsesRatingFolders = document.UsesRatingFolders,
                    SauceNaoApiKey = DpapiSecretProtector.Unprotect(
                        document.SauceNaoApiKey,
                        "sauceNaoApiKey",
                        log,
                        out _),
                };
            }
        }
        catch (Exception ex)
        {
            log($"settings unreadable, using defaults: {ex.Message}");
        }

        var defaults = new PluginSettings();
        defaults.Save(storageDirectory, log);
        return defaults;
    }

    public void Save(string storageDirectory, Action<string> log)
    {
        var path = GetPath(storageDirectory);
        try
        {
            var tempPath = path + ".tmp";
            var document = new SettingsDocument
            {
                DestinationFolderName = DestinationFolderName,
                UsesRatingFolders = UsesRatingFolders,
                SauceNaoApiKey = DpapiSecretProtector.Protect(SauceNaoApiKey),
            };
            File.WriteAllText(tempPath, JsonSerializer.Serialize(document, JsonOptions));
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            log($"settings save failed: {ex.Message}");
        }
    }

    private static string GetPath(string storageDirectory)
        => Path.Combine(storageDirectory, "settings.json");

    private sealed class SettingsDocument
    {
        public string DestinationFolderName { get; set; } = "Pixiv";
        public bool UsesRatingFolders { get; set; } = true;
        public string? SauceNaoApiKey { get; set; }
    }
}

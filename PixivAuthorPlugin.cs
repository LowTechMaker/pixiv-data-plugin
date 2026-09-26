using System.Reflection;
using SceneGallery.PluginSdk;

[assembly: AssemblyMetadata("PluginDescription", "Resolves Pixiv author info, artwork metadata, and reverse image search")]
[assembly: AssemblyMetadata("PluginUpdateUrl", "https://github.com/LowTechMaker/pixiv-data-plugin")]

namespace SceneGallery.Plugin.PixivAuthors;

/// <summary>
/// Resolves pixiv author info for folders named like "ArtistName (12345678)".
/// Anonymous web requests only (no account); rate-limited and disk-cached so
/// each author is fetched at most once until a forced refresh.
/// </summary>
public sealed class PixivAuthorPlugin : IFolderAuthorProvider, ICardImportProvider, IArtworkMetadataRefresher, IImportDestinationProvider, IReverseImageSearchProvider, IPluginSettingsProvider, ITagDictionaryProvider, IDisposable
{
    private static readonly TimeSpan MinRequestInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxJitter = TimeSpan.FromSeconds(3);

    private IPluginHost? _host;
    private PixivRuntime? _runtime;
    private PluginSettings? _settings;

    public string Name => "Pixiv Authors";

    public string Version => typeof(PixivAuthorPlugin).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public IReadOnlyList<PluginSettingDefinition> Settings { get; } =
    [
        new(
            "destinationFolderName",
            "Destination folder",
            "Folder inserted below the organized import subfolder. Leave empty to skip the provider folder.",
            PluginSettingValueType.Text,
            "Pixiv"),
        new(
            "usesRatingFolders",
            "Use rating folders",
            "Split Pixiv imports into G / R-18 / R-18G folders.",
            PluginSettingValueType.Boolean,
            "True"),
        new(
            "sauceNaoApiKey",
            "SauceNao API key",
            "Used by Pixiv reverse image search on the import page.",
            PluginSettingValueType.Secret),
    ];

    public void Initialize(IPluginHost host)
    {
        InitializeForTests(
            host,
            new PixivApiClient(new RateLimiter(MinRequestInterval, MaxJitter), host.Log),
            new SauceNaoClient(new RateLimiter(MinRequestInterval, MaxJitter), host.Log));
    }

    internal void InitializeForTests(IPluginHost host, PixivApiClient client, SauceNaoClient? sauceNao = null, TimeSpan? disposeTimeout = null,
        Func<Task>? beforePreviewPromotion = null)
    {
        _host = host;
        _runtime = new PixivRuntime(host, client, sauceNao, disposeTimeout, beforePreviewPromotion);
        _settings = PluginSettings.Load(host.StorageDirectory, host.Log);
    }

    public ParsedAuthor? TryParseFolderName(string folderName)
        => PixivFolderNameParser.TryParse(folderName);

    public string GetProfileUrl(AuthorKey key) => PixivFetchCoordinator.GetProfileUrl(key);

    public Task<AuthorInfo?> GetAuthorInfoAsync(AuthorKey key, bool forceRefresh, CancellationToken ct)
        => _runtime?.Fetches.GetAuthorInfoAsync(key, forceRefresh, ct)
           ?? Task.FromResult<AuthorInfo?>(null);

    // ── ITagDictionaryProvider ───────────────────────────────────────

    public Task<TagArticle?> FetchTagAsync(string tag, string language, CancellationToken ct)
        => _runtime?.FetchTagAsync(tag, language, ct) ?? Task.FromResult<TagArticle?>(null);

    public TagArticle? TryGetCached(string tag, string language)
        => _runtime?.Tags.TryGetCached(tag, language);

    // ── ICardImportProvider ──────────────────────────────────────────

    public string ProviderId => PixivFolderNameParser.ProviderId;

    public string DestinationFolderName => _settings?.DestinationFolderName ?? "Pixiv";

    public bool UsesRatingFolders => _settings?.UsesRatingFolders ?? true;

    public string? GetSettingValue(string key) => key switch
    {
        "destinationFolderName" => DestinationFolderName,
        "usesRatingFolders" => UsesRatingFolders.ToString(),
        "sauceNaoApiKey" => _settings?.SauceNaoApiKey,
        _ => null,
    };

    public void SetSettingValue(string key, string? value)
    {
        if (_host is null || _settings is null || _runtime?.IsDisposing == true)
            return;

        switch (key)
        {
            case "destinationFolderName":
                _settings.DestinationFolderName = value?.Trim() ?? "";
                break;
            case "usesRatingFolders":
                _settings.UsesRatingFolders = bool.TryParse(value, out var usesRatingFolders) && usesRatingFolders;
                break;
            case "sauceNaoApiKey":
                _settings.SauceNaoApiKey = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                break;
        }

        _settings.Save(_host.StorageDirectory, _host.Log);
    }

    public ArtworkId? TryParseFilename(string fileName)
        => PixivFilenameParser.TryParse(fileName);

    public ArtworkId? TryParseUrl(string url)
        => PixivFilenameParser.TryParseUrl(url);

    public ArtworkId? TryParseArtworkFolderName(string folderName)
    {
        var parsed = PixivFolderNameParser.TryParse(folderName);
        if (parsed is null) return null;
        return new ArtworkId(ProviderId, parsed.Key.Id);
    }

    public string GetArtworkUrl(ArtworkId id) => $"https://www.pixiv.net/artworks/{id.Id}";

    public Task<ReverseImageSearchResult?> SearchImageAsync(
        string imagePath,
        string apiKey,
        CancellationToken ct)
        => _runtime?.SearchImageAsync(imagePath, apiKey, ct)
           ?? Task.FromResult<ReverseImageSearchResult?>(null);

    public Task<ArtworkInfo?> FetchArtworkInfoAsync(
        ArtworkId id,
        CancellationToken ct,
        bool saveToLocalCache = true)
        => _runtime?.Fetches.FetchArtworkInfoAsync(id, ct, saveToLocalCache)
           ?? Task.FromResult<ArtworkInfo?>(null);

    public Task<ArtworkRefreshResult> RefreshArtworkAsync(ArtworkId id, CancellationToken ct)
        => _runtime?.Fetches.RefreshArtworkAsync(id, ct)
           ?? Task.FromResult(ArtworkRefreshResult.Failed);

    internal Task DisposalCompletion => _runtime?.Completion ?? Task.CompletedTask;

    public void Dispose() => _runtime?.Dispose();
}

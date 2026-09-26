using System.Reflection;
using NetArchTest.Rules;
using SceneGallery.PluginSdk;

namespace SceneGallery.Plugin.PixivAuthors.Tests
{
    public sealed class ArchitectureTests
    {
        private static readonly Assembly Plugin = typeof(PixivAuthorPlugin).Assembly;

        [Fact]
        public void EveryProductionTypeIsAccountedForByTheLayerContract()
        {
            var providerNamespace = typeof(PixivAuthorPlugin).Namespace!;
            var types = Plugin.GetTypes()
                .Where(type => !type.IsNested && (type.Namespace == providerNamespace ||
                    type.Namespace?.StartsWith(providerNamespace + ".", StringComparison.Ordinal) == true))
                .Select(type => type.Name).OrderBy(name => name, StringComparer.Ordinal);
            Assert.Equal(new[]
            {
                nameof(ArtworkDiskCache), nameof(AuthorDiskCache), nameof(PixivAliasDetector), nameof(PixivApiClient),
                nameof(PixivAuthorPlugin), nameof(PixivAvatarDownload), nameof(PixivFetchCoordinator), nameof(PixivFilenameParser),
                nameof(PixivFolderNameParser), nameof(PixivRuntime), nameof(PixivTagDictionary), nameof(PixivTagLanguage),
                nameof(PixivTagParser), nameof(PluginSettings), nameof(SauceNaoClient), nameof(SauceNaoImageEncoder),
                nameof(SauceNaoPixivResult), nameof(SauceNaoResponseParser), nameof(TagDiskCache),
            }.OrderBy(name => name, StringComparer.Ordinal), types);
        }

        [Fact]
        public void PublicContractAndCapabilitiesRemainCompatible()
        {
            Assert.Equal("SceneGallery.Plugin.PixivAuthors", Plugin.GetName().Name);
            // The 1.3.0 NuGet package deliberately keeps the original binary assembly identity.
            Assert.Equal(new Version(1, 0, 0, 0),
                Assert.Single(Plugin.GetReferencedAssemblies(), reference => reference.Name == "SceneGallery.PluginSdk").Version);
            Assert.Equal(new[] { typeof(PixivAuthorPlugin), typeof(PixivFilenameParser), typeof(PixivFolderNameParser) }.OrderBy(t => t.FullName),
                Plugin.GetExportedTypes().OrderBy(t => t.FullName));
            var entry = typeof(PixivAuthorPlugin);
            Assert.NotNull(entry.GetConstructor(Type.EmptyTypes));
            Type[] capabilities = [typeof(IPlugin), typeof(IFolderAuthorProvider), typeof(ICardImportProvider),
                typeof(IArtworkMetadataRefresher), typeof(IImportDestinationProvider), typeof(IReverseImageSearchProvider), typeof(IPluginSettingsProvider),
                typeof(ITagDictionaryProvider), typeof(IDisposable)];
            Assert.Equal(capabilities.OrderBy(t => t.FullName), entry.GetInterfaces().OrderBy(t => t.FullName));
            var contractMethods = capabilities.SelectMany(t => entry.GetInterfaceMap(t).TargetMethods).Distinct().OrderBy(m => m.ToString());
            Assert.Equal(contractMethods, entry.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).OrderBy(m => m.ToString()));
            Assert.Equal(["TryParse", "TryParseUrl"], typeof(PixivFilenameParser).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly).Select(m => m.Name).Order());
            Assert.Equal(typeof(ArtworkId), typeof(PixivFilenameParser).GetMethod("TryParse", [typeof(string)])!.ReturnType);
            Assert.Equal(typeof(ArtworkId), typeof(PixivFilenameParser).GetMethod("TryParseUrl", [typeof(string)])!.ReturnType);
            var folder = Assert.Single(typeof(PixivFolderNameParser).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly));
            Assert.Equal("TryParse", folder.Name);
            Assert.Equal(typeof(ParsedAuthor), folder.ReturnType);
            Assert.Equal(typeof(string), Assert.Single(folder.GetParameters()).ParameterType);
            Assert.Equal("pixiv", typeof(PixivFolderNameParser).GetField("ProviderId")!.GetRawConstantValue());
            using var instance = new PixivAuthorPlugin();
            Assert.Equal("Pixiv Authors", instance.Name);
            Assert.Equal("pixiv", instance.ProviderId);
            Assert.Equal("https://github.com/LowTechMaker/pixiv-data-plugin", Plugin.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "PluginUpdateUrl").Value);
            Assert.Equal(typeof(Task<TagArticle>), typeof(ITagDictionaryProvider).GetMethod("FetchTagAsync")!.ReturnType);
            Assert.Equal(typeof(TagArticle), typeof(ITagDictionaryProvider).GetMethod("TryGetCached")!.ReturnType);
        }

        [Fact]
        public void ShippingAssemblyHasNoHostPluginCommonRuntimeOrTestReferences()
            => Assert.DoesNotContain(Plugin.GetReferencedAssemblies(), reference =>
                reference.Name!.StartsWith("KoikatsuSceneGallery", StringComparison.Ordinal)
                || reference.Name.StartsWith("SceneGallery.", StringComparison.Ordinal) && reference.Name != "SceneGallery.PluginSdk"
                || reference.Name.StartsWith("NetArchTest", StringComparison.Ordinal)
                || reference.Name.StartsWith("xunit", StringComparison.OrdinalIgnoreCase));

        [Fact]
        public void ParsersRemainPure()
            => AssertNoDependencies([typeof(PixivFilenameParser), typeof(PixivFolderNameParser), typeof(SauceNaoResponseParser), typeof(SauceNaoPixivResult),
                typeof(PixivTagParser), typeof(PixivTagLanguage), typeof(PixivAliasDetector)],
                "System.Net.Http", "System.IO.File", "System.IO.Directory", "Windows.",
                typeof(PixivFetchCoordinator).FullName!, typeof(PixivRuntime).FullName!, typeof(PixivApiClient).FullName!,
                typeof(SauceNaoClient).FullName!, typeof(AuthorDiskCache).FullName!, typeof(ArtworkDiskCache).FullName!, typeof(IPluginHost).FullName!);

        [Fact]
        public void TransportCannotOwnProviderPolicyOrPersistence()
            => AssertNoDependencies([typeof(PixivApiClient), typeof(SauceNaoClient)],
                typeof(PixivRuntime).FullName!, typeof(PixivFetchCoordinator).FullName!, typeof(AuthorDiskCache).FullName!,
                typeof(ArtworkDiskCache).FullName!, typeof(PluginSettings).FullName!, typeof(IPluginHost).FullName!);

        [Fact]
        public void PersistenceCannotDependOnTransportOrRuntime()
            => AssertNoDependencies([typeof(AuthorDiskCache), typeof(ArtworkDiskCache), typeof(TagDiskCache), typeof(PluginSettings)],
                typeof(PixivApiClient).FullName!, typeof(SauceNaoClient).FullName!, typeof(PixivRuntime).FullName!, typeof(PixivFetchCoordinator).FullName!);

        [Fact]
        public void EncoderCannotDependOnNetworkOrProviderPolicy()
            => AssertNoDependencies([typeof(SauceNaoImageEncoder)], "System.Net.Http", typeof(SauceNaoClient).FullName!,
                typeof(PixivRuntime).FullName!, typeof(PixivFetchCoordinator).FullName!, typeof(SauceNaoResponseParser).FullName!);

        [Fact]
        public void FetchCoordinationAndAvatarStagingCannotReachHostBrowserOrReverseSearch()
            => AssertNoDependencies([typeof(PixivFetchCoordinator), typeof(PixivTagDictionary), typeof(PixivAvatarDownload)],
                typeof(IPluginHost).FullName!, "Windows.", typeof(SauceNaoClient).FullName!,
                typeof(SauceNaoImageEncoder).FullName!);

        [Fact]
        public void TagDictionaryAndAvatarStagingCannotOwnRuntimeOrAnotherCache()
            => AssertNoDependencies([typeof(PixivTagDictionary), typeof(PixivAvatarDownload)],
                typeof(PixivRuntime).FullName!, typeof(AuthorDiskCache).FullName!, typeof(ArtworkDiskCache).FullName!,
                typeof(PixivFetchCoordinator).FullName!);

        [Fact]
        public void LowerLayersCannotDependOnEntryPoint()
        {
            var selected = Types.InAssembly(Plugin).That().HaveNameMatching("^(?!PixivAuthorPlugin$).*$");
            Assert.NotEmpty(selected.GetTypes());
            Assert.True(selected.ShouldNot().HaveDependencyOn(typeof(PixivAuthorPlugin).FullName!).GetResult().IsSuccessful);
        }

        [Fact]
        public void CommonSourcesRemainProviderAgnostic()
        {
            var selected = Types.InAssembly(Plugin).That().ResideInNamespace("SceneGallery.PluginCommon");
            Assert.NotEmpty(selected.GetTypes());
            Assert.True(selected.ShouldNot().HaveDependencyOnAny("SceneGallery.PluginSdk", "SceneGallery.Plugin.PixivAuthors").GetResult().IsSuccessful);
        }

        private static void AssertNoDependencies(Type[] types, params string[] forbidden)
        {
            var pattern = "^(" + string.Join("|", types.Select(t => System.Text.RegularExpressions.Regex.Escape(t.Name))) + ")$";
            var selected = Types.InAssembly(Plugin).That().HaveNameMatching(pattern);
            Assert.Equal(types.Length, selected.GetTypes().Count());
            var result = selected.ShouldNot().HaveDependencyOnAny(forbidden).GetResult();
            Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypes?.Select(t => t.FullName) ?? []));
        }

        [Theory]
        [InlineData(nameof(Fixtures.Construction))]
        [InlineData(nameof(Fixtures.StaticCall))]
        [InlineData(nameof(Fixtures.AsyncCall))]
        public void GuardDetectsOrdinaryStaticAndAsyncDependencies(string name)
        {
            var selected = Types.InAssembly(typeof(ArchitectureTests).Assembly).That().HaveName(name);
            Assert.Single(selected.GetTypes());
            Assert.False(selected.ShouldNot().HaveDependencyOn(typeof(Fixtures.Dependency).FullName!).GetResult().IsSuccessful);
        }

        [Fact]
        public void GuardAcceptsPureFixture()
        {
            var selected = Types.InAssembly(typeof(ArchitectureTests).Assembly).That().HaveName(nameof(Fixtures.Pure));
            Assert.Single(selected.GetTypes());
            Assert.True(selected.ShouldNot().HaveDependencyOn(typeof(Fixtures.Dependency).FullName!).GetResult().IsSuccessful);
        }
    }
}

namespace SceneGallery.Plugin.PixivAuthors.Tests.Fixtures
{
    internal sealed class Dependency { internal static int Read() => Environment.TickCount; }
    internal sealed class Construction { internal object Run() => new Dependency(); }
    internal sealed class StaticCall { internal int Run() => Dependency.Read(); }
    internal sealed class AsyncCall { internal async Task<int> Run() { await Task.Yield(); return Dependency.Read(); } }
    internal sealed class Pure { internal int Run(int value) => value + 1; }
}

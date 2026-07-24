using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WolfEngine.AssetPipeline;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;
using WolfEngine.Audio;
using WolfEngine.Editor.Tooling;

namespace WolfEngine.Build;

public sealed class GameBuildContext
{
	public required string ProjectRootPath { get; init; }
	public required string GameplayProjectPath { get; init; }
	public required AssetDatabase AssetDatabase { get; init; }
}

public readonly record struct GameBuildOptions(string OutputPath, bool Debug = false);

public readonly record struct GameBuildResult(string OutputPath, int CookedAssetCount);

public static class GameBuilder
{
	private static readonly Guid GameplayAssemblyId = new("ca55e2d4-f348-5e92-a173-00f3f1d2bda4");
	private static readonly Guid GameplaySymbolsId = new("ca55e2d4-f348-5e92-a173-00f3f1d2bda5");
	private static readonly Guid RuntimeSettingsId = new("e1317bc4-b53c-59ea-982f-3b8170b61f10");

	public static GameBuildResult Build(GameBuildContext context, GameBuildOptions options)
	{
		ArgumentNullException.ThrowIfNull(context);
		var projectRoot = Path.GetFullPath(context.ProjectRootPath);
		var output = Path.GetFullPath(options.OutputPath);
		var configPath = Path.Combine(projectRoot, "WolfEngineBuild.json");
		var config = JsonSerializer.Deserialize<WolfEngineBuildConfig>(File.ReadAllBytes(configPath), AssetJson.SerializerOptions)
			?? throw new InvalidDataException("WolfEngineBuild.json is invalid.");
		if (config.Version is < 1 or > WolfEngineBuildConfig.CurrentVersion)
			throw new InvalidDataException("WolfEngineBuild.json version is invalid.");
		var sceneIds = config.GetSceneIds();
		if (sceneIds.Count == 0)
			throw new InvalidDataException("WolfEngineBuild.json must contain at least one project scene.");
		if (!string.Equals(config.Target, "current-host", StringComparison.Ordinal))
			throw new InvalidDataException("The first build pipeline supports only target 'current-host'.");
		var configuration = options.Debug ? "Debug" : config.Configuration;
		Console.WriteLine($"Building {configuration} game ({(options.Debug ? "debug symbols enabled" : "symbols stripped")}).");

		var engineSolutionRoot = FindEngineSolutionRoot();
		var services = new ServiceCollection();
		global::WolfEngine.WolfEngine.ConfigureServices(services);
		services.AddEditorToolingShaders(
			new EngineShaderOptions { EngineContentRoot = Path.Combine(engineSolutionRoot, "WolfEngine") });
		using var provider = services.BuildServiceProvider();
		var database = context.AssetDatabase.Assets.ToDictionary(asset => asset.Id);
		foreach (var sceneId in sceneIds)
		{
			if (!database.TryGetValue(sceneId, out var scene) || scene.Type != AssetType.Scene)
				throw new InvalidOperationException($"Project scene '{sceneId}' was not found.");
		}

		var gameplayProject = context.GameplayProjectPath;
		var gameplayBuildArgs = new List<string>
		{
			"build",
			gameplayProject,
			"--configuration",
			configuration,
			"--no-incremental",
			"/m:1"
		};
		AddSymbolProperties(gameplayBuildArgs, options.Debug);
		Run("dotnet", gameplayBuildArgs, projectRoot);
		var gameplayDll = FindGameplayDll(gameplayProject, configuration);
		ValidateGameplayAssembly(gameplayDll);
		var gameplayPdb = Path.ChangeExtension(gameplayDll, ".pdb");
		if (options.Debug && !File.Exists(gameplayPdb))
			throw new FileNotFoundException("The debug gameplay build did not produce a portable PDB.", gameplayPdb);

		var staging = output + ".staging";
		if (Directory.Exists(staging))
			Directory.Delete(staging, true);

		Directory.CreateDirectory(staging);
		var rid = GetCurrentRid();
		var runtimeProject = Path.Combine(engineSolutionRoot, "WolfEngine.Runtime", "WolfEngine.Runtime.csproj");
		var publishArgs = new List<string>
		{
			"publish",
			runtimeProject,
			"--configuration",
			configuration,
			"--runtime",
			rid,
			"--self-contained",
			config.SelfContained ? "true" : "false",
			"--output",
			staging,
			"/m:1"
		};
		AddSymbolProperties(publishArgs, options.Debug);
		Run("dotnet", publishArgs, engineSolutionRoot);
		var leakedEngineAssets = Path.Combine(staging, "Assets");
		if (Directory.Exists(leakedEngineAssets))
			Directory.Delete(leakedEngineAssets, true);

		var graph = provider.GetRequiredService<IAssetPipelineIndex>().GetDependencies(projectRoot)
			.GroupBy(edge => edge.FromNodeId)
			.ToDictionary(
				group => group.Key,
				group => group.Where(edge => edge.IsHard).Select(edge => edge.ToNodeId).ToHashSet());
		foreach (var asset in database.Values)
			AddSerializedDependencies(projectRoot, asset, graph);

		var closure = ComputeClosure(sceneIds, database, graph);
		var contentRoot = Path.Combine(staging, "Content");
		Directory.CreateDirectory(contentRoot);
		var groups = closure
			.Select(id => CreateSource(projectRoot, database[id], graph.GetValueOrDefault(id) ?? []))
			.GroupBy(source => GetPackName(database[source.Id].Type))
			.ToDictionary(group => group.Key, group => group.ToList());

		var shaderSources = CookShaders(provider, GetBackend());
		groups["shaders"] = shaderSources;
		groups["bootstrap"] =
		[
			new WolfPackSource(GameplayAssemblyId, "GameplayAssembly", File.ReadAllBytes(gameplayDll), []),
			new WolfPackSource(
				RuntimeSettingsId,
				"RuntimeSettings",
				JsonSerializer.SerializeToUtf8Bytes(config.RuntimeSettings, AssetJson.SerializerOptions),
				[])
		];
		if (options.Debug)
		{
			groups["bootstrap"].Add(
				new WolfPackSource(GameplaySymbolsId, "GameplaySymbols", File.ReadAllBytes(gameplayPdb), []));
		}

		var manifest = new WolfBootstrapManifest
		{
			Target = rid,
			RuntimeVersion = Environment.Version.ToString(),
			BuildConfiguration = configuration,
			InitialSceneId = sceneIds[0],
			GameplayAssemblyId = GameplayAssemblyId,
			GameplaySymbolsId = options.Debug ? GameplaySymbolsId : Guid.Empty,
			RuntimeSettingsId = RuntimeSettingsId
		};
		foreach (var group in groups.OrderBy(group => group.Key, StringComparer.Ordinal))
		{
			var fileName = group.Key + ".wolfpack";
			var path = Path.Combine(contentRoot, fileName);
			WolfPackFile.Write(path, group.Value);
			using var packHashStream = File.OpenRead(path);
			manifest.Packs.Add(new WolfManifestPack
			{
				Name = group.Key,
				FileName = fileName,
				Sha256 = Convert.ToHexString(SHA256.HashData(packHashStream)),
				ByteSize = new FileInfo(path).Length
			});
		}
		manifest.Roots =
		[
			..sceneIds.Select((sceneId, index) => new WolfManifestRoot
			{
				Kind = index == 0 ? "InitialScene" : "Scene",
				Id = sceneId,
				Dependencies = ComputeClosure([sceneId], database, graph).Where(id => id != sceneId).Order().ToList()
			}),
			new WolfManifestRoot { Kind = "GameplayAssembly", Id = GameplayAssemblyId },
			..(options.Debug
				? new[] { new WolfManifestRoot { Kind = "GameplaySymbols", Id = GameplaySymbolsId } }
				: []),
			new WolfManifestRoot { Kind = "RuntimeSettings", Id = RuntimeSettingsId }
		];
		File.WriteAllBytes(Path.Combine(contentRoot, "bootstrap.wolfmanifest"),
			JsonSerializer.SerializeToUtf8Bytes(manifest, AssetJson.SerializerOptions));
		ValidateCleanOutput(staging, projectRoot);
		if (Directory.Exists(output))
			Directory.Delete(output, true);

		Directory.Move(staging, output);
		Console.WriteLine($"Built {closure.Count} cooked assets to '{output}'.");
		return new GameBuildResult(output, closure.Count);
	}

	private static WolfPackSource CreateSource(string projectRoot, AssetDatabaseEntry asset, IReadOnlyCollection<Guid> dependencies)
	{
		string path;
		if (asset.Type == AssetType.Texture2D)
		{
			var target = OperatingSystem.IsMacOS() ? "metal" : "d3d12";
			path = asset.Artifacts.FirstOrDefault(item => item.Kind == "RuntimeTexture" && item.Target == target)?.RelativePath
				?? throw new InvalidOperationException($"Texture '{asset.Id}' has no {target} runtime artifact.");
		}
		else if (asset.Type == AssetType.AudioClip)
		{
			path = asset.Artifacts.FirstOrDefault(item => item.Kind == AudioAssetConstants.RuntimeArtifactKind)?.RelativePath
			       ?? throw new InvalidOperationException($"Audio clip '{asset.Id}' has no runtime artifact.");
		}
		else
		{
			path = asset.RelativeAssetPath;
		}

		var absolute = Path.GetFullPath(Path.Combine(projectRoot, path.Replace('/', Path.DirectorySeparatorChar)));
		if (!File.Exists(absolute))
			throw new FileNotFoundException($"Cooked input for '{asset.Id}' is missing.", absolute);

		return WolfPackSource.FromFile(asset.Id, asset.Type.ToString(), absolute, dependencies);
	}

	private static void AddSerializedDependencies(string root, AssetDatabaseEntry asset, Dictionary<Guid, HashSet<Guid>> graph)
	{
		if (asset.Type is AssetType.Texture2D or AssetType.Mesh or AssetType.Model3D)
			return;

		var path = Path.Combine(root, asset.RelativeAssetPath.Replace('/', Path.DirectorySeparatorChar));
		if (!File.Exists(path) || !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
			return;

		using var json = JsonDocument.Parse(File.ReadAllBytes(path));
		var found = graph.GetValueOrDefault(asset.Id);
		if (found is null)
			graph[asset.Id] = found = [];

		Scan(json.RootElement, found);

		static void Scan(JsonElement element, HashSet<Guid> result)
		{
			if (element.ValueKind == JsonValueKind.Object)
			{
				foreach (var property in element.EnumerateObject())
				{
					var isAssetReference = property.NameEquals("NodeId")
					                       || property.NameEquals("PrefabAssetId")
					                       || property.NameEquals("CellId")
					                       || property.NameEquals("GlobalCellId");
					if (isAssetReference && property.Value.ValueKind == JsonValueKind.String
					    && Guid.TryParse(property.Value.GetString(), out var id) && id != Guid.Empty)
					{
						result.Add(id);
					}
					else
					{
						Scan(property.Value, result);
					}
				}
			}
			else if (element.ValueKind == JsonValueKind.Array)
			{
				foreach (var child in element.EnumerateArray())
					Scan(child, result);
			}
		}
	}

	private static HashSet<Guid> ComputeClosure(IEnumerable<Guid> roots, Dictionary<Guid, AssetDatabaseEntry> database, Dictionary<Guid, HashSet<Guid>> graph)
	{
		var result = new HashSet<Guid>();
		var pending = new Stack<Guid>();
		foreach (var root in roots)
			pending.Push(root);
		while (pending.TryPop(out var id))
		{
			if (!result.Add(id))
				continue;
			if (!database.ContainsKey(id))
				throw new InvalidOperationException($"Hard dependency '{id}' is missing from the asset database.");
			if (graph.TryGetValue(id, out var dependencies))
				foreach (var dependency in dependencies.OrderDescending())
					pending.Push(dependency);
		}

		return result;
	}

	private static List<WolfPackSource> CookShaders(IServiceProvider provider, GraphicsBackendKind backend)
	{
		var shaderProvider = provider.GetRequiredService<IShaderProvider>();
		var catalog = provider.GetRequiredService<EngineShaderCatalog>();
		var result = new List<WolfPackSource>();
		foreach (var request in catalog.GetDeclaredRuntimeRequests(backend))
		{
			var artifact = shaderProvider.GetArtifact(request);
			using var stream = new MemoryStream();
			ShaderArtifactSerializer.Write(stream, artifact);
			result.Add(new WolfPackSource(CreateDeterministicId("shader:" + artifact.ContentKey), "Shader", stream.ToArray(), []));
		}

		return result;
	}

	private static string GetPackName(AssetType type) => type switch
	{
		AssetType.Texture2D => "textures",
		AssetType.Mesh or AssetType.Model3D => "meshes-models",
		AssetType.Scene or AssetType.SceneCell or AssetType.Prefab => "scenes-prefabs",
		AssetType.Material or AssetType.DataAsset or AssetType.Terrain => "materials-data-terrain",
		AssetType.AudioClip => "audio",
		_ => throw new InvalidOperationException($"Asset type '{type}' has no cooked pack class.")
	};

	private static Guid CreateDeterministicId(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));

	private static GraphicsBackendKind GetBackend()
	{
		if (OperatingSystem.IsMacOS())
			return GraphicsBackendKind.Metal;
		if (OperatingSystem.IsWindows())
			return GraphicsBackendKind.D3D12;

		throw new PlatformNotSupportedException();
	}

	private static string GetCurrentRid()
	{
		if (OperatingSystem.IsMacOS())
			return System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
			       == System.Runtime.InteropServices.Architecture.Arm64
				? "osx-arm64"
				: "osx-x64";
		if (OperatingSystem.IsWindows())
			return System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
			       == System.Runtime.InteropServices.Architecture.Arm64
				? "win-arm64"
				: "win-x64";

		throw new PlatformNotSupportedException();
	}

	private static string FindGameplayDll(string project, string configuration)
	{
		var outputDirectory = Path.Combine(Path.GetDirectoryName(project)!, "bin", configuration);
		var assemblyName = Path.GetFileNameWithoutExtension(project) + ".dll";
		return Directory.EnumerateFiles(outputDirectory, assemblyName, SearchOption.AllDirectories)
			.OrderByDescending(File.GetLastWriteTimeUtc)
			.First();
	}

	private static void AddSymbolProperties(List<string> args, bool debug)
	{
		if (debug)
			args.AddRange(["-p:DebugType=portable", "-p:DebugSymbols=true", "-p:Optimize=false"]);
		else
			args.AddRange(["-p:DebugType=None", "-p:DebugSymbols=false"]);
	}

	private static void ValidateGameplayAssembly(string path)
	{
		var assembly = System.Reflection.Assembly.Load(File.ReadAllBytes(path));
		var hasEntrypoint = assembly.GetTypes().Any(type =>
			type.GetMethod(
				"CreateModule",
				System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static) is not null);
		if (!hasEntrypoint)
			throw new InvalidOperationException("Gameplay assembly has no public static CreateModule entrypoint.");
	}

	private static void Run(string file, IReadOnlyList<string> args, string cwd)
	{
		var info = new ProcessStartInfo(file)
		{
			WorkingDirectory = cwd,
			UseShellExecute = false
		};
		foreach (var arg in args)
			info.ArgumentList.Add(arg);

		using var process = Process.Start(info)
			?? throw new InvalidOperationException($"Could not start {file}.");
		process.WaitForExit();
		if (process.ExitCode != 0)
			throw new InvalidOperationException($"{file} exited with code {process.ExitCode}.");
	}

	private static string FindEngineSolutionRoot()
	{
		var current = new DirectoryInfo(Directory.GetCurrentDirectory());
		while (current is not null)
		{
			var candidate = Path.Combine(current.FullName, "WolfEngine", "WolfEngine", "WolfEngine.csproj");
			if (File.Exists(candidate))
				return Path.Combine(current.FullName, "WolfEngine");

			candidate = Path.Combine(current.FullName, "WolfEngine", "WolfEngine.csproj");
			if (File.Exists(candidate))
				return current.FullName;

			current = current.Parent;
		}

		throw new DirectoryNotFoundException("Could not locate the WolfEngine solution root.");
	}

	private static void ValidateCleanOutput(string root, string projectRoot)
	{
		var forbidden = new[] { ".meta", ".slang", ".sqlite", ".cs" };
		foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
		{
			var isEditorAssembly = file.Contains("WolfEngine.Editor", StringComparison.OrdinalIgnoreCase);
			var isSourceFile = forbidden.Any(extension => file.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
			if (isEditorAssembly || isSourceFile)
				throw new InvalidOperationException($"Editor/source file leaked into output: {file}");
		}

		foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
		{
			if (new[] { "Assets", "Library", "Gameplay" }.Contains(
				    Path.GetFileName(directory),
				    StringComparer.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException($"Source directory leaked into output: {directory}");
			}
		}

		_ = projectRoot;
	}

}

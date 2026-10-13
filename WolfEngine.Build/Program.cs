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
using WolfEngine.Serialization;

namespace WolfEngine.Build;

public sealed class GameBuildContext
{
	public required string ProjectRootPath { get; init; }
	public required string GameplayProjectPath { get; init; }
	public required AssetDatabase AssetDatabase { get; init; }
	public IAssetCatalog? AssetCatalog { get; init; }
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
		var catalog = context.AssetCatalog ?? new AssetCatalog([
			new DirectoryAssetMount("project", "Project", projectRoot, false, context.AssetDatabase)
		]);
		var database = catalog.Assets.ToDictionary(asset => asset.Asset.Id);
		foreach (var sceneId in sceneIds)
		{
			if (!database.TryGetValue(sceneId, out var scene) || scene.Asset.Type != AssetType.Scene)
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
		// The engine project copies its loose content next to its assembly for the editor. A game gets those assets
		// from the cooked packs, so the publish output's copies are removed.
		foreach (var leakedFolder in new[] { "Assets", "BuiltInContent" })
		{
			var leakedPath = Path.Combine(staging, leakedFolder);
			if (Directory.Exists(leakedPath))
				Directory.Delete(leakedPath, true);
		}

		var graph = catalog.Mounts.SelectMany(mount => mount.Dependencies)
			.GroupBy(edge => edge.FromNodeId)
			.ToDictionary(
				group => group.Key,
				group => group.Where(edge => edge.IsHard).Select(edge => edge.ToNodeId).ToHashSet());
		foreach (var asset in database.Values)
			AddSerializedDependencies(asset, graph);

		var fontSources = ResolveCssFonts(gameplayDll, database);
		var cooker = CreateContentCooker(gameplayDll);
		var roots = sceneIds.Concat(config.GetExplicitAssetIds()).Concat(fontSources.Values).Distinct().ToArray();
		var closure = ComputeClosure(roots, database, graph);
		var contentRoot = Path.Combine(staging, "Content");
		Directory.CreateDirectory(contentRoot);
		var groups = closure
			.Select(id => CreateSource(database[id], graph.GetValueOrDefault(id) ?? [], cooker))
			.GroupBy(source => GetPackName(database[source.Id].Asset.Type))
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
			FontSources = fontSources,
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
			..config.GetExplicitAssetIds().Select(assetId => new WolfManifestRoot
			{
				Kind = "ExplicitAsset",
				Id = assetId,
				Dependencies = ComputeClosure([assetId], database, graph).Where(id => id != assetId).Order().ToList()
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

	private static WolfPackSource CreateSource(
		MountedAsset mountedAsset,
		IReadOnlyCollection<Guid> dependencies,
		ContentCooker cooker)
	{
		var asset = mountedAsset.Asset;
		if (asset.Type is AssetType.SceneCell or AssetType.Prefab or AssetType.DataAsset)
		{
			return new WolfPackSource(asset.Id, asset.Type.ToString(), CookAuthoredJson(mountedAsset, cooker), dependencies);
		}

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
		else if (asset.Type == AssetType.ColorLookupTable)
		{
			path = asset.Artifacts.FirstOrDefault(item => item.Kind == ColorLookupTableArtifactSerializer.ArtifactKind)?.RelativePath
			       ?? throw new InvalidOperationException($"Colour lookup table '{asset.Id}' has no runtime artifact.");
		}
		else
		{
			path = asset.RelativeAssetPath;
		}

		var absolute = mountedAsset.Mount.GetAbsolutePath(path);
		if (!File.Exists(absolute))
			throw new FileNotFoundException($"Cooked input for '{asset.Id}' is missing.", absolute);

		return WolfPackSource.FromFile(asset.Id, asset.Type.ToString(), absolute, dependencies);
	}

	private static void AddSerializedDependencies(MountedAsset mountedAsset, Dictionary<Guid, HashSet<Guid>> graph)
	{
		var asset = mountedAsset.Asset;
		if (asset.Type is AssetType.Texture2D or AssetType.Mesh or AssetType.Model3D)
			return;

		if (string.IsNullOrWhiteSpace(asset.RelativeAssetPath))
			return;
		var path = mountedAsset.Mount.GetAbsolutePath(asset.RelativeAssetPath);
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

	private static HashSet<Guid> ComputeClosure(IEnumerable<Guid> roots, Dictionary<Guid, MountedAsset> database, Dictionary<Guid, HashSet<Guid>> graph)
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
		AssetType.Texture2D or AssetType.ColorLookupTable => "textures",
		AssetType.Mesh or AssetType.Model3D or AssetType.Skeleton or AssetType.AnimationClip => "meshes-models",
		AssetType.Scene or AssetType.SceneCell or AssetType.Prefab => "scenes-prefabs",
		AssetType.Material or AssetType.DataAsset or AssetType.Terrain => "materials-data-terrain",
		AssetType.AudioClip => "audio",
		AssetType.Font => "fonts",
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

	internal static Dictionary<string, Guid> ResolveCssFonts(string assemblyPath, Dictionary<Guid, MountedAsset> database)
	{
		var assembly = System.Reflection.Assembly.Load(File.ReadAllBytes(assemblyPath));
		var result = new Dictionary<string, Guid>(StringComparer.Ordinal);
		foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".css", StringComparison.OrdinalIgnoreCase)))
		{
			using var stream = assembly.GetManifestResourceStream(resource)!;
			using var reader = new StreamReader(stream);
			foreach (var source in FontSourceReferences.Find(reader.ReadToEnd()))
			{
				var candidates = database.Values.Where(a => a.Mount.Id == "project" && a.Asset.Type == AssetType.Font && a.Asset.RelativeSourcePath == source).ToArray();
				if (candidates.Length != 1) throw new InvalidOperationException($"CSS resource '{resource}' references missing or ambiguous font '/{source}'.");
				result[source] = candidates[0].Asset.Id;
			}
		}
		return result;
	}

	/// <summary>
	/// Scene cells, prefabs and data assets ship in the binary format the generated serializers read, so a game
	/// never parses JSON or uses reflection to load them.
	/// </summary>
	private static byte[] CookAuthoredJson(MountedAsset mountedAsset, ContentCooker cooker)
	{
		var asset = mountedAsset.Asset;
		var absolute = mountedAsset.Mount.GetAbsolutePath(asset.RelativeAssetPath);
		if (!File.Exists(absolute))
			throw new FileNotFoundException($"Cooked input for '{asset.Id}' is missing.", absolute);

		var json = File.ReadAllBytes(absolute);
		try
		{
			return asset.Type switch
			{
				AssetType.SceneCell => cooker.CookSceneCell(asset.Id, DeserializeDocument<SceneCellDocument>(json)),
				AssetType.Prefab => cooker.CookPrefab(asset.Id, DeserializeDocument<PrefabDocument>(json)),
				AssetType.DataAsset => cooker.CookDataAsset(asset.Id, DeserializeDocument<DataAssetFile>(json)),
				_ => throw new InvalidOperationException($"Asset type '{asset.Type}' is not cooked from JSON.")
			};
		}
		catch (Exception exception) when (exception is JsonException or InvalidDataException or InvalidOperationException)
		{
			throw new InvalidDataException($"Could not cook '{asset.RelativeAssetPath}' ({asset.Id}): {exception.Message}", exception);
		}
	}

	private static T DeserializeDocument<T>(byte[] json) where T : class
	{
		return JsonSerializer.Deserialize<T>(json, AssetJson.SerializerOptions)
		       ?? throw new InvalidDataException($"The file is not a valid {typeof(T).Name}.");
	}

	/// <summary>
	/// Loads the gameplay assembly the cooker resolves gameplay types from, and makes sure it and every engine
	/// assembly that declares components have registered their generated serializers.
	/// </summary>
	private static ContentCooker CreateContentCooker(string gameplayDll)
	{
		var gameplayAssembly = System.Reflection.Assembly.Load(File.ReadAllBytes(gameplayDll));
		var gameplayAssemblyName = gameplayAssembly.GetName().Name;
		SerializedTypeRegistry.EnsureRegistered(typeof(global::WolfEngine.ECS.World).Assembly);
		SerializedTypeRegistry.EnsureRegistered(typeof(Camera).Assembly);
		SerializedTypeRegistry.EnsureRegistered(typeof(global::WolfEngine.Physics.RigidbodySystem).Assembly);
		SerializedTypeRegistry.EnsureRegistered(typeof(AudioService).Assembly);
		SerializedTypeRegistry.EnsureRegistered(typeof(global::WolfEngine.UI.ServiceCollectionExtensions).Assembly);
		SerializedTypeRegistry.EnsureRegistered(gameplayAssembly);

		return new ContentCooker((typeId, typeName) =>
			ResolveCookedType(typeId, gameplayAssembly, gameplayAssemblyName) ??
			ResolveCookedType(typeName, gameplayAssembly, gameplayAssemblyName));
	}

	/// <summary>
	/// Resolves a saved type name. Gameplay types are looked up in the loaded gameplay assembly, both by their
	/// <c>gameplay:</c> stable id and by an assembly-qualified name naming the gameplay assembly, because asking
	/// the runtime to load the gameplay assembly by name would find a different copy, or none.
	/// </summary>
	private static Type? ResolveCookedType(string name, System.Reflection.Assembly gameplayAssembly, string? gameplayAssemblyName)
	{
		const string gameplayPrefix = "gameplay:";
		if (string.IsNullOrWhiteSpace(name))
			return null;
		if (name.StartsWith(gameplayPrefix, StringComparison.Ordinal))
			return gameplayAssembly.GetType(name[gameplayPrefix.Length..], false);

		var separator = name.IndexOf(',');
		if (separator > 0)
		{
			var assemblyPart = name[(separator + 1)..].Trim();
			var assemblySimpleName = assemblyPart.Split(',')[0].Trim();
			if (string.Equals(assemblySimpleName, gameplayAssemblyName, StringComparison.Ordinal))
				return gameplayAssembly.GetType(name[..separator].Trim(), false);
		}

		return Type.GetType(name, false);
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
		// Forward child output through Console rather than letting the child inherit this process's stdout.
		// A host can then route it: the editor automation server reserves stdout for its protocol.
		var info = new ProcessStartInfo(file)
		{
			WorkingDirectory = cwd,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		foreach (var arg in args)
			info.ArgumentList.Add(arg);

		using var process = Process.Start(info)
			?? throw new InvalidOperationException($"Could not start {file}.");
		process.OutputDataReceived += (_, line) =>
		{
			if (line.Data is not null)
				Console.Out.WriteLine(line.Data);
		};
		process.ErrorDataReceived += (_, line) =>
		{
			if (line.Data is not null)
				Console.Error.WriteLine(line.Data);
		};
		process.BeginOutputReadLine();
		process.BeginErrorReadLine();
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

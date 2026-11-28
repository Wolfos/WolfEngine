using System.Numerics;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Editor.Projects;
using WolfEngine.Editor.Tooling.Imaging;
using WolfEngine.Importing;
using WolfEngine.Mathematics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Editor.UnityImport;

/// <param name="ExportFolder">The folder the Unity exporter wrote, holding <c>scene.json</c>.</param>
/// <param name="TargetFolder">Project folder, under Assets, that the imported assets are written to.</param>
/// <param name="SceneName">Name of the new scene, saved as <c>Assets/Scenes/Name/Name.scene.json</c>.</param>
/// <param name="UnityProjectPath">The Unity project the export came from, which its textures are read from.</param>
/// <param name="RenderConfigPath">Optional project path of a RenderConfig for the scene's world settings.</param>
/// <param name="LightIntensityScale">Multiplies Unity light intensities, whose units differ from WolfEngine's.</param>
/// <param name="PlacePrefabInstances">Converts the prefabs the scene places and places them, as well as the terrain.</param>
public sealed record UnitySceneImportRequest(
	string ExportFolder,
	string TargetFolder,
	string SceneName,
	string UnityProjectPath,
	string? RenderConfigPath = null,
	float LightIntensityScale = 2.5f,
	bool PlacePrefabInstances = true);

/// <param name="UnresolvedTreePrefabs">Unity tree prefabs that could not be converted, and their tree counts.</param>
/// <param name="ConvertedPrefabs">Unity prefab paths and the WolfEngine prefabs they were converted to.</param>
/// <param name="PrefabInstancesPlaced">Unity prefab instances placed in the scene.</param>
/// <param name="SkippedPrefabInstances">Instances not placed, counted per reason and prefab.</param>
/// <param name="InstanceOverrides">
/// Overrides Unity placements made to their prefabs, other than static flags, counted per kind. Placements keep only
/// their transform, so these are what the import leaves out.
/// </param>
/// <param name="DiscardedLayerWeight">Mean layer weight per texel beyond the four layers a texel holds, from 0 to 1.</param>
/// <param name="DetailFits">
/// Per Unity detail prototype converted to detail foliage, how its painted density was fitted to the layers.
/// </param>
/// <param name="SkippedDetailPrototypes">Detail prototypes not converted, with why.</param>
public sealed record UnitySceneImportReport(
	Guid SceneId,
	string ScenePath,
	int TerrainCount,
	int LayerCount,
	int TreesImported,
	int TreesSkipped,
	IReadOnlyDictionary<string, int> UnresolvedTreePrefabs,
	IReadOnlyList<string> CreatedFoliageTypes,
	IReadOnlyDictionary<string, UnityConvertedPrefab> ConvertedPrefabs,
	int PrefabInstancesPlaced,
	IReadOnlyDictionary<string, int> SkippedPrefabInstances,
	IReadOnlyDictionary<string, int> InstanceOverrides,
	float DiscardedLayerWeight,
	IReadOnlyList<UnityDetailPrototypeReport> DetailFits,
	IReadOnlyList<string> SkippedDetailPrototypes,
	IReadOnlyList<string> Warnings);

/// <summary>How a Unity detail prototype became detail foliage.</summary>
/// <param name="Densities">Plants per square metre on each layer, by layer name, for the layers that grow it.</param>
/// <param name="PaintedPlants">Plants Unity placed across the terrain.</param>
/// <param name="FittedPlants">Plants the layer densities grow across the terrain, before distance and clumping.</param>
/// <param name="RelativeError">See <see cref="UnityDetailFit.RelativeError"/>.</param>
public sealed record UnityDetailPrototypeReport(
	string Prefab,
	IReadOnlyDictionary<string, float> Densities,
	double PaintedPlants,
	double FittedPlants,
	float RelativeError);

/// <summary>
/// Imports a scene exported from Unity with WolfEngine.UnityExporter into the open project: each terrain with its
/// layers, layer maps and trees, the prefab instances it places, the directional lights and the main camera, as a new
/// scene. The prefabs are converted from the export by <see cref="UnityPrefabImporter"/>.
/// </summary>
public sealed class UnitySceneImporter
{
	private readonly IEditorProjectService _projectService;
	private readonly IAssetMetadataStore _metadataStore;
	private readonly IProjectAssetPipelineService _assetPipelineService;
	private readonly ITerrainAssetCreator _terrainAssetCreator;
	private readonly IDataAssetCreator _dataAssetCreator;
	private readonly IEditorSceneFactory _sceneFactory;
	private readonly UnityPrefabImporter _prefabImporter;

	public UnitySceneImporter(
		IEditorProjectService projectService,
		IAssetMetadataStore metadataStore,
		IProjectAssetPipelineService assetPipelineService,
		ITerrainAssetCreator terrainAssetCreator,
		IDataAssetCreator dataAssetCreator,
		IEditorSceneFactory sceneFactory,
		UnityPrefabImporter prefabImporter)
	{
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
		_metadataStore = metadataStore ?? throw new ArgumentNullException(nameof(metadataStore));
		_assetPipelineService = assetPipelineService ?? throw new ArgumentNullException(nameof(assetPipelineService));
		_terrainAssetCreator = terrainAssetCreator ?? throw new ArgumentNullException(nameof(terrainAssetCreator));
		_dataAssetCreator = dataAssetCreator ?? throw new ArgumentNullException(nameof(dataAssetCreator));
		_sceneFactory = sceneFactory ?? throw new ArgumentNullException(nameof(sceneFactory));
		_prefabImporter = prefabImporter ?? throw new ArgumentNullException(nameof(prefabImporter));
	}

	public UnitySceneImportReport Import(UnitySceneImportRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);

		if (_projectService.HasOpenProject == false)
		{
			throw new InvalidOperationException("Open a project before importing a Unity scene.");
		}

		if (Directory.Exists(request.UnityProjectPath) == false)
		{
			throw new DirectoryNotFoundException($"The Unity project '{request.UnityProjectPath}' was not found.");
		}

		var export = UnitySceneExport.Load(request.ExportFolder);
		var targetFolder = ProjectPathUtility.NormalizeAssetsFolderPath(request.TargetFolder);
		var context = new ImportContext(request, export, targetFolder);
		var scene = _sceneFactory.New();
		scene.Name = request.SceneName;
		scene.RelativeAssetPath = $"Assets/Scenes/{request.SceneName}/{request.SceneName}{EditorSceneAssetFile.FileExtension}";
		if (File.Exists(_projectService.GetAbsolutePath(scene.RelativeAssetPath)))
		{
			throw new InvalidOperationException($"A scene already exists at '{scene.RelativeAssetPath}'.");
		}

		var prefabPaths = export.Terrains
			.SelectMany(terrain => terrain.TreePrototypes)
			.Select(prototype => prototype.Prefab)
			.Concat(export.Terrains
				.SelectMany(terrain => terrain.DetailPrototypes)
				.Where(prototype => prototype.UsePrototypeMesh)
				.Select(prototype => prototype.Prefab))
			.OfType<string>();
		if (request.PlacePrefabInstances)
		{
			prefabPaths = prefabPaths.Concat(export.PrefabInstances.Select(instance => instance.PrefabPath));
		}

		var groundCoverPrefabPaths = export.Terrains
			.SelectMany(terrain => terrain.DetailPrototypes)
			.Where(prototype => prototype.UsePrototypeMesh)
			.Select(prototype => prototype.Prefab)
			.OfType<string>()
			.ToHashSet(StringComparer.Ordinal);
		context.ConvertedPrefabs = _prefabImporter.Import(
			export,
			request.UnityProjectPath,
			targetFolder,
			prefabPaths,
			context.Warnings,
			groundCoverPrefabPaths);

		foreach (var terrain in export.Terrains)
		{
			ImportTerrain(context, scene, terrain);
		}

		if (request.PlacePrefabInstances)
		{
			PlacePrefabInstances(context, scene);
		}

		AddLights(context, scene);
		AddMainCamera(context, scene);
		AddWorldSettings(context, scene);

		_sceneFactory.Save(scene);

		return new UnitySceneImportReport(
			scene.AssetId,
			scene.RelativeAssetPath,
			export.Terrains.Count,
			context.LayerCount,
			context.TreesImported,
			context.UnresolvedTreePrefabs.Values.Sum(),
			context.UnresolvedTreePrefabs,
			context.CreatedFoliageTypes,
			context.ConvertedPrefabs,
			context.PrefabInstancesPlaced,
			context.SkippedPrefabInstances,
			context.InstanceOverrides,
			context.DiscardedLayerWeight,
			context.DetailFits,
			context.SkippedDetailPrototypes,
			context.Warnings);
	}

	private void ImportTerrain(ImportContext context, EditorScene scene, UnityTerrainExport terrain)
	{
		var name = SanitizeFileName(string.IsNullOrWhiteSpace(terrain.Name) ? "Terrain" : terrain.Name);
		var terrainFolder = $"{context.TargetFolder}/Terrain";
		var layerSetId = ImportLayerSet(context, terrain, name, terrainFolder);

		var terrainAsset = UnityTerrainConversion.CreateTerrainAsset(
			name,
			terrain,
			context.Export.ReadHeights(terrain),
			context.Export.ReadAlphamaps(terrain),
			out var discardedWeight);
		context.DiscardedLayerWeight = MathF.Max(context.DiscardedLayerWeight, discardedWeight);
		var terrainAssetId = RequireCreated(_terrainAssetCreator.CreateTerrainAsset(terrainFolder, terrainAsset), $"terrain '{name}'");
		var foliageId = ImportTrees(context, terrain, name, terrainFolder);

		var entity = scene.World.CreateEntity(name, UnityTerrainConversion.GetEntityPosition(terrain), Quaternion.Identity, Vector3.One);
		var component = new TerrainComponent();
		component.ApplyDefaultValues(scene.World, entity);
		UnityTerrainConversion.ApplyTo(ref component, terrain);
		component.TerrainAsset = new AssetRef<TerrainAsset> { NodeId = terrainAssetId };
		component.LayerSetAsset = new AssetRef<TerrainLayerSet> { NodeId = layerSetId };
		if (foliageId != Guid.Empty)
		{
			component.Foliage = new AssetRef<TerrainFoliage> { NodeId = foliageId };
		}

		scene.World.AddComponent(entity, component);
	}

	/// <summary>
	/// Places each Unity prefab instance as a linked WolfEngine prefab instance with its world transform, under one parent
	/// so the hierarchy stays usable. Both engines share a space and converted meshes are Unity's own, so the transform
	/// carries over as it is.
	/// </summary>
	private void PlacePrefabInstances(ImportContext context, EditorScene scene)
	{
		if (context.Export.PrefabInstances.Count == 0) return;

		var world = scene.World;
		var group = world.CreateEntity("Props", Vector3.Zero, Quaternion.Identity, Vector3.One);
		foreach (var instance in context.Export.PrefabInstances)
		{
			foreach (var kind in instance.OverrideKinds)
			{
				// Unity records static flag changes on a placement's children as GameObject overrides; they change nothing here.
				if (string.Equals(kind, "modified GameObject", StringComparison.Ordinal) == false)
				{
					context.InstanceOverrides[kind] = context.InstanceOverrides.GetValueOrDefault(kind) + 1;
				}
			}

			if (context.ConvertedPrefabs.TryGetValue(instance.PrefabPath, out var prefab) == false)
			{
				Skip(context, "not converted", instance.PrefabPath);
				continue;
			}

			if (prefab.RendererCount == 0)
			{
				var components = prefab.UnsupportedComponents.Count > 0 ? string.Join(", ", prefab.UnsupportedComponents) : "nothing";
				Skip(context, $"draws nothing (has {components})", instance.PrefabPath);
				continue;
			}

			var root = _assetPipelineService.InstantiatePrefab(_projectService.ProjectRootPath!, prefab.PrefabId, scene);
			if (root == default)
			{
				Skip(context, "empty prefab", instance.PrefabPath);
				continue;
			}

			world.SetLocalPosition(root, UnitySpace.ToVector3(instance.Position));
			world.SetLocalRotation(root, UnitySpace.ToQuaternion(instance.Rotation));
			world.SetLocalScale(root, UnitySpace.ToVector3(instance.Scale));
			world.SetParent(root, group);
			context.PrefabInstancesPlaced++;
		}
	}

	private static void Skip(ImportContext context, string reason, string prefabPath)
	{
		var key = $"{reason}: {Path.GetFileName(prefabPath)}";
		context.SkippedPrefabInstances[key] = context.SkippedPrefabInstances.GetValueOrDefault(key) + 1;
	}

	private Guid ImportLayerSet(ImportContext context, UnityTerrainExport terrain, string terrainName, string terrainFolder)
	{
		var layerSet = new TerrainLayerSet
		{
			ActiveLayerCount = Math.Max(terrain.Layers.Count, 1),
			Layers = []
		};
		foreach (var layer in terrain.Layers)
		{
			layerSet.Layers.Add(ImportLayer(context, layer, $"{terrainFolder}/Textures"));
		}

		var biomes = ImportDetailBiomes(context, terrain, terrainName, terrainFolder);
		for (var i = 0; i < biomes.Length; i++)
		{
			layerSet.Layers[i].Biome = biomes[i];
		}

		context.LayerCount += terrain.Layers.Count;

		return RequireCreated(
			_dataAssetCreator.CreateDataAsset(typeof(TerrainLayerSet), terrainFolder, $"{terrainName} Layers", layerSet),
			$"the layer set of '{terrainName}'");
	}

	private TerrainLayerDefinition ImportLayer(ImportContext context, UnityTerrainLayerExport layer, string textureFolder)
	{
		var definition = new TerrainLayerDefinition
		{
			Name = layer.Name,
			Scale = layer.TileSize.Length > 0 ? layer.TileSize[0] : 1.0f
		};
		if (layer.Missing)
		{
			context.Warnings.Add("A terrain layer slot is empty in Unity; it imports as an empty layer.");
			return definition;
		}

		if (layer.TileSize.Length > 1 && MathF.Abs(layer.TileSize[0] - layer.TileSize[1]) > 0.001f)
		{
			context.Warnings.Add($"Layer '{layer.Name}' tiles {layer.TileSize[0]} × {layer.TileSize[1]} m; WolfEngine layers tile squarely, so it uses {layer.TileSize[0]} m.");
		}

		if (MathF.Abs(layer.NormalScale - 1.0f) > 0.001f)
		{
			context.Warnings.Add($"Layer '{layer.Name}' scales its normals by {layer.NormalScale}; the normal map imports unscaled.");
		}

		var baseName = SanitizeFileName(layer.Name);
		var diffuse = LoadUnityImage(context, layer.DiffuseTexture);
		if (diffuse is not null)
		{
			definition.Albedo = WriteTexture(CreateAlbedo(diffuse, layer), $"{textureFolder}/{baseName}_Albedo.png", TextureSemantic.BaseColor);
		}

		var normal = LoadUnityImage(context, layer.NormalTexture);
		if (normal is not null)
		{
			definition.Normal = WriteTexture(normal, $"{textureFolder}/{baseName}_Normal.png", TextureSemantic.Normal);
		}

		var mask = LoadUnityImage(context, layer.MaskTexture);
		if (mask is not null)
		{
			definition.Orm = WriteTexture(CreateOrm(mask, layer), $"{textureFolder}/{baseName}_ORM.png", TextureSemantic.MetallicRoughness);
			definition.Height = WriteTexture(CreateHeight(mask, layer), $"{textureFolder}/{baseName}_Height.png", TextureSemantic.Occlusion);
		}
		else
		{
			context.Warnings.Add($"Layer '{layer.Name}' has no mask map, so it imports without ORM and height.");
		}

		return definition;
	}

	/// <summary>The diffuse colour with Unity's remap applied, opaque. Its alpha holds smoothness, which the ORM map carries.</summary>
	private static RgbaImage CreateAlbedo(RgbaImage diffuse, UnityTerrainLayerExport layer)
	{
		var output = new RgbaImage(diffuse.Width, diffuse.Height);
		for (var y = 0; y < diffuse.Height; y++)
		{
			for (var x = 0; x < diffuse.Width; x++)
			{
				var pixel = diffuse[x, y];
				output[x, y] = new RgbaPixel(
					Remap(pixel.R, layer.DiffuseRemapMin, layer.DiffuseRemapMax, 0),
					Remap(pixel.G, layer.DiffuseRemapMin, layer.DiffuseRemapMax, 1),
					Remap(pixel.B, layer.DiffuseRemapMin, layer.DiffuseRemapMax, 2));
			}
		}

		return output;
	}

	/// <summary>
	/// Unity mask maps hold metallic, occlusion, height and smoothness in R, G, B and A, each remapped into the layer's
	/// mask range. WolfEngine's ORM map holds occlusion, roughness and metallic.
	/// </summary>
	private static RgbaImage CreateOrm(RgbaImage mask, UnityTerrainLayerExport layer)
	{
		var output = new RgbaImage(mask.Width, mask.Height);
		for (var y = 0; y < mask.Height; y++)
		{
			for (var x = 0; x < mask.Width; x++)
			{
				var pixel = mask[x, y];
				var smoothness = Remap(pixel.A, layer.MaskMapRemapMin, layer.MaskMapRemapMax, 3);
				output[x, y] = new RgbaPixel(
					Remap(pixel.G, layer.MaskMapRemapMin, layer.MaskMapRemapMax, 1),
					(byte)(255 - smoothness),
					Remap(pixel.R, layer.MaskMapRemapMin, layer.MaskMapRemapMax, 0));
			}
		}

		return output;
	}

	private static RgbaImage CreateHeight(RgbaImage mask, UnityTerrainLayerExport layer)
	{
		var output = new RgbaImage(mask.Width, mask.Height);
		for (var y = 0; y < mask.Height; y++)
		{
			for (var x = 0; x < mask.Width; x++)
			{
				var height = Remap(mask[x, y].B, layer.MaskMapRemapMin, layer.MaskMapRemapMax, 2);
				output[x, y] = new RgbaPixel(height, height, height);
			}
		}

		return output;
	}

	private static byte Remap(byte value, float[] min, float[] max, int channel)
	{
		var low = channel < min.Length ? min[channel] : 0.0f;
		var high = channel < max.Length ? max[channel] : 1.0f;
		var remapped = low + ((value / 255.0f) * (high - low));

		return (byte)Math.Clamp((int)MathF.Round(remapped * 255.0f), 0, 255);
	}

	private Guid ImportTrees(ImportContext context, UnityTerrainExport terrain, string terrainName, string terrainFolder)
	{
		if (terrain.Trees.Count == 0) return Guid.Empty;

		var foliage = new TerrainFoliage();
		var typeIndices = new int[terrain.TreePrototypes.Count];
		for (var i = 0; i < terrain.TreePrototypes.Count; i++)
		{
			typeIndices[i] = -1;
			var prefabPath = terrain.TreePrototypes[i].Prefab;
			if (string.IsNullOrWhiteSpace(prefabPath))
			{
				continue;
			}

			if (context.ConvertedPrefabs.TryGetValue(prefabPath, out var converted) == false ||
			    _projectService.TryGetAsset(converted.PrefabId, out var prefab) == false)
			{
				continue;
			}

			typeIndices[i] = foliage.Types.Count;
			foliage.Types.Add(GetOrCreateFoliageType(context, prefab));
		}

		var size = terrain.GetSize();
		var instances = new List<FoliageInstance>(terrain.Trees.Count);
		foreach (var tree in terrain.Trees)
		{
			var typeIndex = (uint)tree.Prototype < (uint)typeIndices.Length ? typeIndices[tree.Prototype] : -1;
			if (typeIndex < 0)
			{
				var prefabPath = (uint)tree.Prototype < (uint)terrain.TreePrototypes.Count
					? terrain.TreePrototypes[tree.Prototype].Prefab ?? "(no prefab)"
					: $"(prototype {tree.Prototype})";
				context.UnresolvedTreePrefabs[prefabPath] = context.UnresolvedTreePrefabs.GetValueOrDefault(prefabPath) + 1;
				continue;
			}

			instances.Add(UnityTerrainConversion.CreateTreeInstance(tree, size, typeIndex));
		}

		if (foliage.Types.Count == 0) return Guid.Empty;

		foliage.SetInstances(instances);
		context.TreesImported += instances.Count;

		return RequireCreated(
			_dataAssetCreator.CreateDataAsset(typeof(TerrainFoliage), terrainFolder, $"{terrainName} Foliage", foliage),
			$"the foliage of '{terrainName}'");
	}

	/// <summary>
	/// Converts the terrain's mesh detail prototypes into detail foliage types, fits each one's painted density to the
	/// layers, and writes a biome per layer that grows anything. Unity's detail maps themselves are not kept.
	/// </summary>
	/// <returns>Per layer, its biome, or an empty reference for a layer that grows no detail.</returns>
	private AssetRef<FoliageBiome>[] ImportDetailBiomes(
		ImportContext context,
		UnityTerrainExport terrain,
		string terrainName,
		string terrainFolder)
	{
		var biomes = new AssetRef<FoliageBiome>[terrain.Layers.Count];
		var details = context.Export.ReadDetails(terrain);
		if (details.Length == 0 || terrain.Layers.Count == 0)
		{
			return biomes;
		}

		var alphamaps = context.Export.ReadAlphamaps(terrain);
		var size = terrain.GetSize();
		var terrainArea = size.X * size.Z;
		var cellArea = terrainArea / ((float)terrain.DetailResolution * terrain.DetailResolution);
		var coverage = string.Equals(terrain.DetailScatterMode, "CoverageMode", StringComparison.Ordinal);
		var planeSize = terrain.DetailResolution * terrain.DetailResolution;
		var entries = new List<FoliageBiomeEntry>[terrain.Layers.Count];
		for (var i = 0; i < entries.Length; i++)
		{
			entries[i] = [];
		}

		for (var prototypeIndex = 0; prototypeIndex < terrain.DetailPrototypes.Count; prototypeIndex++)
		{
			var prototype = terrain.DetailPrototypes[prototypeIndex];
			var label = prototype.Prefab ?? prototype.Texture ?? $"(prototype {prototypeIndex})";
			if (prototype.UsePrototypeMesh == false || string.IsNullOrWhiteSpace(prototype.Prefab))
			{
				context.SkippedDetailPrototypes.Add($"{label}: grass billboards are not supported; only mesh detail converts");
				continue;
			}

			if (context.ConvertedPrefabs.TryGetValue(prototype.Prefab, out var converted) == false ||
			    _projectService.TryGetAsset(converted.PrefabId, out var prefab) == false)
			{
				context.SkippedDetailPrototypes.Add($"{label}: the prefab did not convert");
				continue;
			}

			var densityPerValue = GetDetailDensityPerValue(terrain, prototype, cellArea, coverage);
			var fit = UnityDetailConversion.Fit(
				alphamaps,
				terrain.AlphamapResolution,
				terrain.Layers.Count,
				details.AsSpan(prototypeIndex * planeSize, planeSize),
				terrain.DetailResolution,
				densityPerValue,
				terrainArea);
			if (fit.PaintedPlants <= 0.0)
			{
				context.SkippedDetailPrototypes.Add($"{label}: not painted anywhere");
				continue;
			}

			var type = GetOrCreateDetailFoliageType(context, prefab, terrain.DetailObjectDistance);
			var densities = new Dictionary<string, float>(StringComparer.Ordinal);
			for (var layer = 0; layer < terrain.Layers.Count; layer++)
			{
				var density = fit.Densities[layer];
				if (density < MinimumDetailDensity)
				{
					continue;
				}

				densities[terrain.Layers[layer].Name] = density;
				entries[layer].Add(CreateBiomeEntry(prototype, type, density));
			}

			context.DetailFits.Add(new UnityDetailPrototypeReport(
				label,
				densities,
				fit.PaintedPlants,
				fit.FittedPlants,
				fit.RelativeError));
		}

		for (var layer = 0; layer < terrain.Layers.Count; layer++)
		{
			if (entries[layer].Count == 0)
			{
				continue;
			}

			var layerName = SanitizeFileName(string.IsNullOrWhiteSpace(terrain.Layers[layer].Name) ? $"Layer {layer}" : terrain.Layers[layer].Name);
			var biome = new FoliageBiome
			{
				Entries = entries[layer]
			};
			var biomeId = RequireCreated(
				_dataAssetCreator.CreateDataAsset(typeof(FoliageBiome), $"{terrainFolder}/Biomes", $"{terrainName} {layerName}", biome),
				$"the biome of layer '{layerName}'");
			biomes[layer] = new AssetRef<FoliageBiome> { NodeId = biomeId };
		}

		return biomes;
	}

	/// <summary>Below this many plants per square metre a fitted layer density is noise from the fit, and dropped.</summary>
	private const float MinimumDetailDensity = 0.01f;

	/// <summary>
	/// Plants per square metre one unit of a detail map value stands for. In instance count mode a value is a plant count
	/// per cell. In coverage mode it is coverage out of 255, which Unity turns into plants by the prototype's density
	/// relative to its size; that is approximated as density plants per square metre at full coverage.
	/// </summary>
	private static float GetDetailDensityPerValue(
		UnityTerrainExport terrain,
		UnityDetailPrototypeExport prototype,
		float cellArea,
		bool coverage)
	{
		var scale = prototype.UseDensityScaling ? Math.Clamp(terrain.DetailObjectDensity, 0.0f, 1.0f) : 1.0f;
		if (coverage)
		{
			return prototype.Density * scale / 255.0f;
		}

		return cellArea > 0.0f ? scale / cellArea : 0.0f;
	}

	/// <summary>
	/// Unity scales detail by a width and a height picked from their ranges with the same noise, so wide plants are also
	/// tall ones. The entry keeps the height range and the width as a ratio of it.
	/// </summary>
	private static FoliageBiomeEntry CreateBiomeEntry(UnityDetailPrototypeExport prototype, AssetRef<FoliageType> type, float density)
	{
		var minHeight = MathF.Max(prototype.MinHeight, 0.01f);
		var maxHeight = MathF.Max(prototype.MaxHeight, minHeight);
		var minRatio = prototype.MinWidth / minHeight;
		var maxRatio = prototype.MaxWidth / maxHeight;

		return new FoliageBiomeEntry
		{
			Type = type,
			Density = density,
			MinHeightScale = minHeight,
			MaxHeightScale = maxHeight,
			MinWidthRatio = MathF.Max(0.01f, MathF.Min(minRatio, maxRatio)),
			MaxWidthRatio = MathF.Max(0.01f, MathF.Max(minRatio, maxRatio)),
			AlignToNormal = Math.Clamp(prototype.AlignToGround, 0.0f, 1.0f)
		};
	}

	/// <summary>
	/// Creates a foliage type for drawing a prefab as detail: out to Unity's detail distance, shadowed near the camera
	/// only, and never ray traced. Kept apart from a tree type for the same prefab, whose distances differ.
	/// </summary>
	private AssetRef<FoliageType> GetOrCreateDetailFoliageType(ImportContext context, AssetDatabaseEntry prefab, float distance)
	{
		if (context.DetailTypesByPrefab.TryGetValue(prefab.Id, out var known))
		{
			return known;
		}

		var cullDistance = MathF.Max(distance, 1.0f);
		var name = $"{GetPrefabFileName(prefab)} Detail";
		var type = new FoliageType
		{
			Prefab = new AssetRef<Prefab> { NodeId = prefab.Id },
			CullDistance = cullDistance,
			ShadowDistance = MathF.Min(DetailShadowDistance, cullDistance),
			RayTracingDistance = 0.0f,
			DetailFadeDistance = cullDistance * 0.25f
		};
		var typeId = RequireCreated(
			_dataAssetCreator.CreateDataAsset(typeof(FoliageType), $"{context.TargetFolder}/Foliage Types", name, type),
			$"the detail foliage type for '{name}'");
		var created = new AssetRef<FoliageType> { NodeId = typeId };
		context.DetailTypesByPrefab.Add(prefab.Id, created);
		context.CreatedFoliageTypes.Add(name);

		return created;
	}

	/// <summary>How far imported detail casts shadows, in metres: the nearest cascade or two.</summary>
	private const float DetailShadowDistance = 25.0f;

	private static string GetPrefabFileName(AssetDatabaseEntry entry)
	{
		var fileName = Path.GetFileName(entry.RelativeSourcePath);
		const string extension = ".prefab.json";

		return fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? fileName[..^extension.Length] : fileName;
	}

	/// <summary>Uses a project foliage type that already draws the prefab, or creates one with default distances.</summary>
	private AssetRef<FoliageType> GetOrCreateFoliageType(ImportContext context, AssetDatabaseEntry prefab)
	{
		if (context.FoliageTypesByPrefab.TryGetValue(prefab.Id, out var known))
		{
			return known;
		}

		foreach (var mounted in _projectService.CurrentAssetCatalog.Assets)
		{
			if (FoliageAssetTypes.IsFoliageType(mounted.Asset) == false)
			{
				continue;
			}

			var reference = new AssetRef<FoliageType> { NodeId = mounted.Asset.Id };
			if (reference.Asset is { } existing && existing.Prefab.NodeId == prefab.Id)
			{
				context.FoliageTypesByPrefab.Add(prefab.Id, reference);
				return reference;
			}
		}

		var name = GetPrefabFileName(prefab);
		var type = new FoliageType
		{
			Prefab = new AssetRef<Prefab> { NodeId = prefab.Id }
		};
		var typeId = RequireCreated(
			_dataAssetCreator.CreateDataAsset(typeof(FoliageType), $"{context.TargetFolder}/Foliage Types", name, type),
			$"the foliage type for '{name}'");
		var created = new AssetRef<FoliageType> { NodeId = typeId };
		context.FoliageTypesByPrefab.Add(prefab.Id, created);
		context.CreatedFoliageTypes.Add(name);

		return created;
	}

	private static void AddLights(ImportContext context, EditorScene scene)
	{
		foreach (var light in context.Export.Lights)
		{
			if (string.Equals(light.Type, "Directional", StringComparison.Ordinal) == false)
			{
				context.Warnings.Add($"Light '{light.Name}' is a {light.Type} light; only directional lights are imported so far.");
				continue;
			}

			var color = UnitySpace.ToVector3(light.Color.Take(3).ToArray());
			if (light.UseColorTemperature)
			{
				color *= UnitySpace.ColorTemperatureToLinear(light.ColorTemperature);
			}

			var entity = scene.World.CreateEntity(
				string.IsNullOrWhiteSpace(light.Name) ? "Sun" : light.Name,
				UnitySpace.ToVector3(light.Position),
				UnitySpace.ToQuaternion(light.Rotation),
				Vector3.One);
			scene.World.AddComponent(entity, new Light
			{
				Type = LightType.Directional,
				Intensity = light.Intensity * context.Request.LightIntensityScale,
				Range = 25.0f,
				Color = new ColorRGBA(color.X, color.Y, color.Z, 1.0f),
				HorizonFade = true
			});
		}
	}

	private static void AddMainCamera(ImportContext context, EditorScene scene)
	{
		var exported = context.Export.Cameras.FirstOrDefault(camera => camera.IsMain) ?? context.Export.Cameras.FirstOrDefault();
		if (exported is null) return;

		var entity = scene.World.CreateEntity(
			string.IsNullOrWhiteSpace(exported.Name) ? "Main Camera" : exported.Name,
			UnitySpace.ToVector3(exported.Position),
			UnitySpace.ToQuaternion(exported.Rotation),
			Vector3.One);
		var camera = new Camera();
		camera.ApplyDefaultValues(scene.World, entity);
		camera.NearPlane = exported.NearClip;
		camera.FarPlane = exported.FarClip;
		camera.SetPerspective(exported.FieldOfView);
		scene.World.AddComponent(entity, camera);
	}

	private void AddWorldSettings(ImportContext context, EditorScene scene)
	{
		if (string.IsNullOrWhiteSpace(context.Request.RenderConfigPath)) return;

		if (_assetPipelineService.TryGetPrimaryNodeIdForRelativeSourcePath(
			    _projectService.ProjectRootPath!,
			    context.Request.RenderConfigPath,
			    out var renderConfigId) == false)
		{
			context.Warnings.Add($"Render config '{context.Request.RenderConfigPath}' was not found; the scene uses default settings.");
			return;
		}

		var entity = scene.World.CreateEntity("World Settings", Vector3.Zero, Quaternion.Identity, Vector3.One);
		scene.World.AddComponent(entity, new WorldSettings
		{
			RenderConfigAsset = new AssetRef<RenderConfig> { NodeId = renderConfigId }
		});
	}

	private RgbaImage? LoadUnityImage(ImportContext context, string? unityAssetPath)
	{
		if (string.IsNullOrWhiteSpace(unityAssetPath)) return null;

		var path = Path.Combine(context.Request.UnityProjectPath, unityAssetPath);
		if (File.Exists(path) == false)
		{
			context.Warnings.Add($"Texture '{unityAssetPath}' was not found in the Unity project.");
			return null;
		}

		return RgbaImage.Load(path);
	}

	private AssetRef<Texture> WriteTexture(RgbaImage image, string relativePath, TextureSemantic semantic)
	{
		var nodeId = GeneratedTextureWriter.Write(_projectService, _metadataStore, image, relativePath, semantic);
		_projectService.RefreshAssetSource(relativePath);

		return new AssetRef<Texture> { NodeId = nodeId };
	}

	private static Guid RequireCreated(EditorAssetCreationResult result, string what)
	{
		if (result.Success == false || result.AssetId is not { } id)
		{
			throw new InvalidOperationException($"Could not create {what}: {result.ErrorMessage}");
		}

		return id;
	}

	private static string SanitizeFileName(string name)
	{
		var invalid = Path.GetInvalidFileNameChars();
		var cleaned = new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();

		return cleaned.Length == 0 ? "Imported" : cleaned;
	}

	private sealed class ImportContext(UnitySceneImportRequest request, UnitySceneExport export, string targetFolder)
	{
		public UnitySceneImportRequest Request { get; } = request;
		public UnitySceneExport Export { get; } = export;
		public string TargetFolder { get; } = targetFolder;
		public Dictionary<Guid, AssetRef<FoliageType>> FoliageTypesByPrefab { get; } = new();
		public Dictionary<Guid, AssetRef<FoliageType>> DetailTypesByPrefab { get; } = new();
		public List<UnityDetailPrototypeReport> DetailFits { get; } = [];
		public List<string> SkippedDetailPrototypes { get; } = [];
		public Dictionary<string, int> UnresolvedTreePrefabs { get; } = new(StringComparer.Ordinal);
		public List<string> CreatedFoliageTypes { get; } = [];
		public IReadOnlyDictionary<string, UnityConvertedPrefab> ConvertedPrefabs { get; set; } = new Dictionary<string, UnityConvertedPrefab>();
		public Dictionary<string, int> SkippedPrefabInstances { get; } = new(StringComparer.Ordinal);
		public Dictionary<string, int> InstanceOverrides { get; } = new(StringComparer.Ordinal);
		public int PrefabInstancesPlaced { get; set; }
		public List<string> Warnings { get; } = [];
		public int LayerCount { get; set; }
		public int TreesImported { get; set; }
		public float DiscardedLayerWeight { get; set; }
	}
}

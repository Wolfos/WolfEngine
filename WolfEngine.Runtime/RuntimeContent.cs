using WolfEngine.Animation;
using System.Text.Json;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Rendering.UI;
using WolfEngine.Mathematics;
using ImGuiNET;
using WolfEngine.Audio;

namespace WolfEngine.Runtime;

public sealed class RuntimeNullUi : IUiFrameProvider, IImGuiInputSink
{
	public void DisableAutomaticIniPersistence() { }
	public bool TryConsumeLatest(out UiFrameData frame)
	{
		frame = UiFrameData.Empty;
		return false;
	}

	public void NewFrame(float deltaTime, Int2 windowSize, Int2 framebufferSize)
	{
	}

	public void RunGui(Action draw)
	{
	}

	public void SetKey(ImGuiKey key, bool down)
	{
	}

	public void AddChar(char c)
	{
	}

	public void SetMousePosition(System.Numerics.Vector2 position)
	{
	}

	public void SetMouseButton(int button, bool down)
	{
	}

	public void AddMouseScroll(System.Numerics.Vector2 scroll)
	{
	}
}

public interface IRuntimeAssetStore
{
	object? Load(Guid id, Type expectedType);
	T? Load<T>(Guid id) where T : class;
}

public sealed class RuntimeAssetStore : IRuntimeAssetStore, IAssetInstanceRegistry
{
	private readonly WolfPackCatalog _catalog;
	private readonly ITextureFactory _textures;
	private readonly IMaterialFactory _materials;
	private readonly IMaterialTypeRegistry _materialTypes;
	private readonly Dictionary<(Guid, Type), object?> _cache = [];
	private readonly Dictionary<string, Mesh> _meshesByContentHash = new(StringComparer.Ordinal);

	public RuntimeAssetStore(WolfPackCatalog catalog, ITextureFactory textures, IMaterialFactory materials, IMaterialTypeRegistry materialTypes)
	{
		_catalog = catalog;
		_textures = textures;
		_materials = materials;
		_materialTypes = materialTypes;
	}

	public T? Load<T>(Guid id) where T : class => Load(id, typeof(T)) as T;

	public object? Load(Guid id, Type expectedType)
	{
		if (id == Guid.Empty)
			return null;
		if (_cache.TryGetValue((id, expectedType), out var cached))
			return cached;

		var entry = _catalog.GetEntry(id);
		if (entry.Kind == nameof(AssetType.Scene) && expectedType == typeof(global::WolfEngine.Gameplay.SceneAsset))
			return new global::WolfEngine.Gameplay.SceneAsset();
		if (entry.Kind == nameof(AssetType.AudioClip) && expectedType == typeof(AudioClip))
		{
			var clip = new AudioClip(id);
			_cache[(id, expectedType)] = clip;
			return clip;
		}
		// Legacy mesh GUIDs can refer to the same imported geometry. Cooked entries carry a
		// content hash, so their immutable geometry can share the same GPU resource identity too.
		if (entry.Kind == nameof(AssetType.Mesh) && expectedType == typeof(Mesh) &&
		    _meshesByContentHash.TryGetValue(entry.Sha256, out var sharedMesh))
		{
			_cache[(id, expectedType)] = sharedMesh;
			return sharedMesh;
		}
		var bytes = _catalog.Read(id);
		using var stream = new MemoryStream(bytes, false);
		var value = entry.Kind switch
		{
			nameof(AssetType.Texture2D) when expectedType == typeof(Texture) => CreateTexture(id, stream),
			nameof(AssetType.ColorLookupTable) when expectedType == typeof(ColorLookupTable) =>
				ColorLookupTable.Create($"lut:{id:D}", ColorLookupTableArtifactSerializer.Read(stream), _textures),
			nameof(AssetType.ComputeShader) when expectedType == typeof(global::WolfEngine.Rendering.ComputeShader) =>
				new global::WolfEngine.Rendering.ComputeShader(id.ToString("D"),
					global::WolfEngine.Rendering.Shaders.ComputeShaderArtifactSerializer.Read(stream)),
			nameof(AssetType.Mesh) when expectedType == typeof(Mesh) => CreateMesh(id, stream),
			nameof(AssetType.Terrain) when expectedType == typeof(TerrainAsset) =>
				TerrainAssetSerializer.Read(stream, id.ToString("D")),
			nameof(AssetType.Material) when expectedType == typeof(Material) =>
				CreateMaterial(JsonSerializer.Deserialize(bytes, EngineJsonContext.Default.MaterialAsset)!),
			nameof(AssetType.Skeleton) when expectedType == typeof(Skeleton) => SkeletonSerializer.Read(stream).ToSkeleton(),
            nameof(AssetType.AnimationClip) when expectedType == typeof(AnimationClip) => AnimationClipSerializer.Read(stream).ToClip(),
            nameof(AssetType.AnimationGraph) when expectedType == typeof(AnimationGraph) => JsonSerializer.Deserialize(bytes, EngineJsonContext.Default.AnimationGraph),
            nameof(AssetType.AnimationSet) when expectedType == typeof(AnimationSet) => JsonSerializer.Deserialize(bytes, EngineJsonContext.Default.AnimationSet),
            nameof(AssetType.BoneMask) when expectedType == typeof(BoneMask) => JsonSerializer.Deserialize(bytes, EngineJsonContext.Default.BoneMask),
            nameof(AssetType.DataAsset) => CreateDataAsset(bytes, expectedType),
			nameof(AssetType.Prefab) when expectedType == typeof(Prefab) => CreatePrefab(id, bytes),
			_ => throw new InvalidOperationException(
				$"Cooked entry '{id}' of kind '{entry.Kind}' cannot resolve '{expectedType.FullName}'.")
		};
		if (value is not null && !expectedType.IsInstanceOfType(value))
			throw new InvalidOperationException($"Cooked asset '{id}' resolved to the wrong runtime type.");

		if (value is Mesh mesh) _meshesByContentHash[entry.Sha256] = mesh;
		_cache[(id, expectedType)] = value;
		return value;
	}

	/// <summary>
	/// Cooked packs are immutable, so the CPU copy of a texture's mips can go once it is on the GPU and be read
	/// from the pack again if anything asks for it.
	/// </summary>
	private Texture CreateTexture(Guid id, Stream stream)
	{
		var texture = TextureArtifactSerializer.Read(stream, id.ToString("D"));
		texture.MipDataSource = () => ReadTexture(id).MipLevels;

		return _textures.GetTexture(texture);
	}

	private Texture ReadTexture(Guid id)
	{
		using var stream = new MemoryStream(_catalog.Read(id), false);

		return TextureArtifactSerializer.Read(stream, id.ToString("D"));
	}

	/// <summary>
	/// As for textures: the geometry can go once it is on the GPU and be rebuilt from the pack, for a mesh collider
	/// for example.
	/// </summary>
	private Mesh CreateMesh(Guid id, Stream stream)
	{
		var mesh = CreateMesh(ImportedMeshSerializer.Read(stream));
		mesh.CpuGeometrySource = () => ReadMesh(id);

		return mesh;
	}

	private Mesh ReadMesh(Guid id)
	{
		using var stream = new MemoryStream(_catalog.Read(id), false);

		return CreateMesh(ImportedMeshSerializer.Read(stream));
	}

	private static Mesh CreateMesh(ImportedMeshAssetFile mesh) => new(mesh.Vertices, mesh.Indices, mesh.Normals, mesh.UVs, mesh.Tangents,
        mesh.BoneIndices.Length > 0 ? mesh.BoneIndices : null, mesh.BoneWeights.Length > 0 ? mesh.BoneWeights : null,
        mesh.ShadowOpaqueIndices, mesh.ShadowAlphaTestIndices);

	// Cooked prefabs store every entity's full component data (nested prefab instances included, as of the
	// prefab's last save), as cooked scene cells do. The build already failed on any component it could not
	// resolve, so a spawned unit cannot silently miss a gameplay component.
	private static Prefab CreatePrefab(Guid id, byte[] bytes) => Prefab.LoadCooked(id, bytes);

	private static object CreateDataAsset(byte[] bytes, Type expectedType) => CookedDataAsset.Read(bytes, expectedType);

	private Material CreateMaterial(MaterialAsset asset)
	{
		var descriptor = _materialTypes.GetDescriptor(asset.MaterialType);
		var properties = asset.GetActiveProperties();
		Texture? Resolve(AssetRef<Texture> reference) => reference.IsValid ? Load<Texture>(reference.NodeId) : null;
		return _materials.GetMaterial(
			descriptor.ShaderPath,
			properties.BaseColor,
			properties.MetallicFactor,
			properties.RoughnessFactor,
			properties.NormalScale,
			properties.EmissiveFactor,
			properties.EmissiveIntensity,
			Resolve(properties.Textures.Albedo),
			Resolve(properties.Textures.Orm),
			Resolve(properties.Textures.Normal),
			Resolve(properties.Textures.Emissive),
			descriptor.RuntimeAlphaMode,
			asset.AlphaCutoff,
			doubleSided: asset.DoubleSided);
	}

	public object? GetInstance(Guid assetId, Type expectedType) => Load(assetId, expectedType);

	/// <summary>Diagnostic: the CPU-side data the loaded meshes and textures keep, by asset kind.</summary>
	public string DescribeCpuMemory()
	{
		long meshBytes = 0;
		var meshCount = 0;
		var releasedMeshCount = 0;
		long textureBytes = 0;
		var textureCount = 0;
		var releasedTextureCount = 0;
		// Distinct: meshes shared by content hash are cached under every id that refers to them.
		foreach (var value in _cache.Values.Distinct(ReferenceEqualityComparer.Instance).ToArray())
		{
			if (value is Mesh { HasCpuGeometry: false })
			{
				releasedMeshCount++;
			}
			else if (value is Texture { HasCpuMipData: false })
			{
				releasedTextureCount++;
			}
			else if (value is Mesh mesh)
			{
				meshCount++;
				meshBytes += (long)mesh.Vertices.Length * 16 + (long)mesh.Normals.Length * 12 + (long)mesh.UVs.Length * 8 +
				             (long)mesh.Tangents.Length * 16 +
				             ((long)mesh.Indices.Length + mesh.ShadowOpaqueIndices.Length + mesh.ShadowAlphaTestIndices.Length) * 4;
			}
			else if (value is Texture texture)
			{
				textureCount++;
				foreach (var mip in texture.MipLevels ?? [])
				{
					textureBytes += mip.Data?.Length ?? 0;
				}
			}
		}

		return $"Asset CPU data: {meshCount} meshes keep {meshBytes / (1024.0 * 1024.0):F1} MB of arrays " +
		       $"({releasedMeshCount} released after upload), {textureCount} textures keep " +
		       $"{textureBytes / (1024.0 * 1024.0):F1} MB of mip data ({releasedTextureCount} released after upload).";
	}

	public void RefreshProject(string projectRootPath, AssetDatabase database) => throw new NotSupportedException("Runtime assets are immutable cooked packs.");

	public void InvalidateAssets(IEnumerable<Guid> assetIds) => throw new NotSupportedException("Runtime assets are immutable cooked packs.");

	public void ClearCachedInstances()
	{
		_cache.Clear();
		_meshesByContentHash.Clear();
	}

	public void Clear() => ClearCachedInstances();
}

public interface IRuntimeSceneLoader
{
	World Load(Guid sceneId);
}

public sealed class RuntimeSceneLoader : IRuntimeSceneLoader
{
	private readonly WolfPackCatalog _catalog;

	public RuntimeSceneLoader(WolfPackCatalog catalog) => _catalog = catalog;

	public World Load(Guid sceneId)
	{
		if (_catalog.GetEntry(sceneId).Kind != nameof(AssetType.Scene))
			throw new InvalidDataException($"Asset '{sceneId}' is not a scene.");
		var scene = JsonSerializer.Deserialize(_catalog.Read(sceneId), EngineJsonContext.Default.SceneManifestDocument)
			?? throw new InvalidDataException("Cooked scene manifest is invalid.");
		if (scene.Version != SceneManifestDocument.CurrentVersion)
			throw new InvalidDataException($"Unsupported scene version {scene.Version}.");

		var cellIds = new[] { scene.GlobalCellId }
			.Concat(scene.SpatialCells.Select(cell => cell.CellId))
			.Where(id => id != Guid.Empty);
		var cells = new List<CookedSceneCell>();
		foreach (var id in cellIds)
			cells.Add(CookedSceneCell.Read(_catalog.Read(id)));

		var world = new World(WorldTag.Game);
		CookedSceneCell.Instantiate(world, cells);
		return world;
	}
}

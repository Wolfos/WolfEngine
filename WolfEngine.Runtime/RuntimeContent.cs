using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Rendering;
using WolfEngine.Rendering.UI;
using WolfEngine.Mathematics;
using ImGuiNET;
using WolfEngine.Audio;

namespace WolfEngine.Runtime;

public sealed class RuntimeNullUi : IUiFrameProvider, IImGuiInputSink
{
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
		if (entry.Kind == nameof(AssetType.AudioClip) && expectedType == typeof(AudioClip))
		{
			var clip = new AudioClip(id);
			_cache[(id, expectedType)] = clip;
			return clip;
		}
		var bytes = _catalog.Read(id);
		using var stream = new MemoryStream(bytes, false);
		var value = entry.Kind switch
		{
			nameof(AssetType.Texture2D) when expectedType == typeof(Texture) =>
				_textures.GetTexture(TextureArtifactSerializer.Read(stream, id.ToString("D"))),
			nameof(AssetType.Mesh) when expectedType == typeof(Mesh) => CreateMesh(ImportedMeshSerializer.Read(stream)),
			nameof(AssetType.Terrain) when expectedType == typeof(TerrainAsset) =>
				TerrainAssetSerializer.Read(stream, id.ToString("D")),
			nameof(AssetType.Material) when expectedType == typeof(Material) =>
				CreateMaterial(JsonSerializer.Deserialize<MaterialAsset>(bytes, AssetJson.SerializerOptions)!),
			nameof(AssetType.DataAsset) => CreateDataAsset(bytes, expectedType),
			_ => throw new InvalidOperationException(
				$"Cooked entry '{id}' of kind '{entry.Kind}' cannot resolve '{expectedType.FullName}'.")
		};
		if (value is not null && !expectedType.IsInstanceOfType(value))
			throw new InvalidOperationException($"Cooked asset '{id}' resolved to the wrong runtime type.");

		_cache[(id, expectedType)] = value;
		return value;
	}

	private static Mesh CreateMesh(ImportedMeshAssetFile mesh) => new(mesh.Vertices, mesh.Indices, mesh.Normals, mesh.UVs, mesh.Tangents);

	private object CreateDataAsset(byte[] bytes, Type expectedType)
	{
		var file = JsonSerializer.Deserialize<DataAssetFile>(bytes, AssetJson.SerializerOptions)
			?? throw new InvalidDataException("Cooked data asset is invalid.");
		return file.Data.Deserialize(expectedType, AssetJson.GetSerializerOptions(expectedType))
			?? throw new InvalidDataException($"Could not deserialize data asset '{expectedType.FullName}'.");
	}

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
			asset.AlphaCutoff);
	}

	public object? GetInstance(Guid assetId, Type expectedType) => Load(assetId, expectedType);

	public void RefreshProject(string projectRootPath, AssetDatabase database) => throw new NotSupportedException("Runtime assets are immutable cooked packs.");

	public void InvalidateAssets(IEnumerable<Guid> assetIds) => throw new NotSupportedException("Runtime assets are immutable cooked packs.");

	public void ClearCachedInstances() => _cache.Clear();

	public void Clear() => _cache.Clear();
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
		var scene = JsonSerializer.Deserialize<CookedSceneManifest>(_catalog.Read(sceneId), AssetJson.SerializerOptions)
			?? throw new InvalidDataException("Cooked scene manifest is invalid.");
		if (scene.Version != 1)
			throw new InvalidDataException($"Unsupported scene version {scene.Version}.");

		var cells = new List<CookedCell>();
		var cellIds = new[] { scene.GlobalCellId }
			.Concat(scene.SpatialCells.Select(cell => cell.CellId))
			.Where(id => id != Guid.Empty);
		foreach (var id in cellIds)
		{
			var cell = JsonSerializer.Deserialize<CookedCell>(_catalog.Read(id), AssetJson.SerializerOptions)
				?? throw new InvalidDataException($"Scene cell '{id}' is invalid.");
			cells.Add(cell);
		}

		var world = new World(WorldTag.Game);
		var entities = new Dictionary<Guid, Entity>();
		foreach (var saved in cells.SelectMany(cell => cell.Entities))
		{
			var entity = saved.HasName ? world.CreateEntity(saved.Name) : world.CreateEntity();
			if (saved.LocalTransform is { } transform)
				world.AddTransform(entity, transform);

			world.SetEnabled(entity, saved.Enabled);
			entities.Add(saved.EntityId, entity);
		}

		foreach (var saved in cells.SelectMany(cell => cell.Entities))
		{
			var entity = entities[saved.EntityId];
			foreach (var component in saved.Components)
				ApplyComponent(world, entity, component, entities);
			if (saved.ParentEntityId is { } parent && entities.TryGetValue(parent, out var parentEntity))
				world.SetParent(entity, parentEntity);
		}

		return world;
	}

	private static void ApplyComponent(World world, Entity entity, CookedComponent component, IReadOnlyDictionary<Guid, Entity> entities)
	{
		var type = ResolveType(component.TypeId, component.Type);
		if (type is null || !type.IsValueType || !typeof(IEntityComponent).IsAssignableFrom(type))
			throw new InvalidDataException($"Runtime component type '{component.TypeId}' is unavailable.");

		var options = new JsonSerializerOptions(AssetJson.GetSerializerOptions(type));
		options.Converters.Insert(0, new EntityReferenceConverter(entities));
		var value = component.Data.Deserialize(type, options) ?? Activator.CreateInstance(type)!;
		typeof(RuntimeSceneLoader)
			.GetMethod(nameof(AddComponent), BindingFlags.Static | BindingFlags.NonPublic)!
			.MakeGenericMethod(type)
			.Invoke(null, [world, entity, value]);
	}

	private static void AddComponent<T>(World world, Entity entity, object value) where T : struct, IEntityComponent => world.AddComponent(entity, (T)value);

	private static Type? ResolveType(string stableId, string typeName)
	{
		var gameplayName = stableId.StartsWith("gameplay:", StringComparison.Ordinal) ? stableId[9..] : null;
		foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
		{
			if (gameplayName is not null && assembly.GetType(gameplayName, false) is { } gameplayType)
				return gameplayType;
			if (Type.GetType(typeName, false) is { } type)
				return type;
		}

		return null;
	}

	private sealed class EntityReferenceConverter(IReadOnlyDictionary<Guid, Entity> entities) : JsonConverter<Entity>
	{
		public override Entity Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
		{
			using var document = JsonDocument.ParseValue(ref reader);
			if (document.RootElement.ValueKind == JsonValueKind.Object
			    && document.RootElement.TryGetProperty("__entityRefId", out var value)
			    && Guid.TryParse(value.GetString(), out var id)
			    && entities.TryGetValue(id, out var entity))
				return entity;

			return default;
		}

		public override void Write(Utf8JsonWriter writer, Entity value, JsonSerializerOptions options) => throw new NotSupportedException();
	}
}

public sealed class CookedSceneManifest
{
	public int Version { get; set; }
	public Guid GlobalCellId { get; set; }
	public List<CookedSpatialCell> SpatialCells { get; set; } = [];
}

public sealed class CookedSpatialCell
{
	public Guid CellId { get; set; }
}

public sealed class CookedCell
{
	public int Version { get; set; }
	public List<CookedEntity> Entities { get; set; } = [];
}

public sealed class CookedEntity
{
	public Guid EntityId { get; set; }
	public Guid? ParentEntityId { get; set; }
	public bool HasName { get; set; }
	public string Name { get; set; } = string.Empty;
	public bool Enabled { get; set; } = true;
	public System.Numerics.Matrix4x4? LocalTransform { get; set; }
	public List<CookedComponent> Components { get; set; } = [];
}

public sealed class CookedComponent
{
	public string Type { get; set; } = string.Empty;
	public string TypeId { get; set; } = string.Empty;
	public JsonElement Data { get; set; }
}

public sealed class GameplayAssemblyLoader
{
	public (Assembly Assembly, global::WolfEngine.Gameplay.IGameplayModule Module) Load(byte[] bytes, byte[]? symbols = null)
	{
		using var stream = new MemoryStream(bytes, false);
		using var symbolStream = symbols is null ? null : new MemoryStream(symbols, false);
		var assembly = symbolStream is null
			? AssemblyLoadContext.Default.LoadFromStream(stream)
			: AssemblyLoadContext.Default.LoadFromStream(stream, symbolStream);
		var method = assembly.GetTypes()
			.Select(type => type.GetMethod("CreateModule", BindingFlags.Public | BindingFlags.Static))
			.SingleOrDefault(candidate => candidate is not null)
			?? throw new InvalidDataException("Gameplay assembly has no unique public CreateModule entrypoint.");
		var module = (global::WolfEngine.Gameplay.IGameplayModule?)method.Invoke(null, null)
			?? throw new InvalidDataException("Gameplay CreateModule returned null.");
		return (assembly, module);
	}
}

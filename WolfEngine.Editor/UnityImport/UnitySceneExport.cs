using System.Numerics;
using System.Text.Json;

namespace WolfEngine.Editor.UnityImport;

/// <summary>
/// The scene a Unity project exported with the WolfEngine exporter (WolfEngine.UnityExporter): <c>scene.json</c> and the
/// raw terrain maps beside it. Everything is in Unity's space and refers to Unity assets by their path in the Unity
/// project; see <see cref="UnitySpace"/> for the conversion.
/// </summary>
public sealed class UnitySceneExport
{
	public const int SupportedFormatVersion = 1;
	public const string SceneFileName = "scene.json";

	private static readonly JsonSerializerOptions SerializerOptions = new()
	{
		PropertyNameCaseInsensitive = true
	};

	public int FormatVersion { get; set; }
	public string UnityVersion { get; set; } = string.Empty;
	public string ScenePath { get; set; } = string.Empty;
	public List<UnityTerrainExport> Terrains { get; set; } = [];
	public List<UnityPrefabInstanceExport> PrefabInstances { get; set; } = [];
	public List<UnityLightExport> Lights { get; set; } = [];
	public List<UnityCameraExport> Cameras { get; set; } = [];
	public List<UnityPrefabExport> Prefabs { get; set; } = [];
	public List<UnityMaterialExport> Materials { get; set; } = [];
	public List<UnityMeshExport> Meshes { get; set; } = [];

	/// <summary>The export folder, which the terrain map files are read from.</summary>
	public string FolderPath { get; private set; } = string.Empty;

	public static UnitySceneExport Load(string folderPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

		var scenePath = Path.Combine(folderPath, SceneFileName);
		if (File.Exists(scenePath) == false)
		{
			throw new FileNotFoundException($"'{folderPath}' is not a Unity export: it has no {SceneFileName}.", scenePath);
		}

		var export = JsonSerializer.Deserialize<UnitySceneExport>(File.ReadAllText(scenePath), SerializerOptions)
			?? throw new InvalidDataException($"'{scenePath}' is empty.");
		if (export.FormatVersion != SupportedFormatVersion)
		{
			throw new InvalidDataException(
				$"'{scenePath}' has export format {export.FormatVersion}; this editor reads format {SupportedFormatVersion}.");
		}

		export.FolderPath = Path.GetFullPath(folderPath);

		return export;
	}

	/// <summary>Reads a terrain's heights: <see cref="UnityTerrainExport.HeightmapResolution"/> squared 16-bit values.</summary>
	public byte[] ReadHeights(UnityTerrainExport terrain)
	{
		var bytes = File.ReadAllBytes(Path.Combine(FolderPath, terrain.HeightsFile));
		var expected = terrain.HeightmapResolution * terrain.HeightmapResolution * 2;
		if (bytes.Length != expected)
		{
			throw new InvalidDataException($"'{terrain.HeightsFile}' holds {bytes.Length} bytes; a {terrain.HeightmapResolution}² heightmap needs {expected}.");
		}

		return bytes;
	}

	/// <summary>Reads a terrain's layer weights: one plane of alphamap resolution squared bytes per layer.</summary>
	public byte[] ReadAlphamaps(UnityTerrainExport terrain)
	{
		var bytes = File.ReadAllBytes(Path.Combine(FolderPath, terrain.AlphamapFile));
		var expected = terrain.AlphamapResolution * terrain.AlphamapResolution * terrain.Layers.Count;
		if (bytes.Length != expected)
		{
			throw new InvalidDataException(
				$"'{terrain.AlphamapFile}' holds {bytes.Length} bytes; {terrain.Layers.Count} layers at {terrain.AlphamapResolution}² need {expected}.");
		}

		return bytes;
	}

	/// <summary>
	/// Reads a terrain's detail maps: one plane per detail prototype of detail resolution squared 16-bit values, rows
	/// along Z. Empty for an export written before detail was exported.
	/// </summary>
	public ushort[] ReadDetails(UnityTerrainExport terrain)
	{
		if (string.IsNullOrEmpty(terrain.DetailsFile) || terrain.DetailPrototypes.Count == 0)
		{
			return [];
		}

		var bytes = File.ReadAllBytes(Path.Combine(FolderPath, terrain.DetailsFile));
		var expected = terrain.DetailResolution * terrain.DetailResolution * terrain.DetailPrototypes.Count * 2;
		if (bytes.Length != expected)
		{
			throw new InvalidDataException(
				$"'{terrain.DetailsFile}' holds {bytes.Length} bytes; {terrain.DetailPrototypes.Count} detail maps at {terrain.DetailResolution}² need {expected}.");
		}

		var values = new ushort[bytes.Length / 2];
		for (var i = 0; i < values.Length; i++)
		{
			values[i] = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i * 2, 2));
		}

		return values;
	}
}

public sealed class UnityTerrainExport
{
	public string Name { get; set; } = string.Empty;
	public string? TerrainDataPath { get; set; }
	public float[] Position { get; set; } = [0.0f, 0.0f, 0.0f];

	/// <summary>Width, height range and length, in metres. The terrain spans from its position along +X and +Z.</summary>
	public float[] Size { get; set; } = [0.0f, 0.0f, 0.0f];
	public int HeightmapResolution { get; set; }
	public string HeightsFile { get; set; } = string.Empty;
	public int AlphamapResolution { get; set; }
	public string AlphamapFile { get; set; } = string.Empty;
	public List<UnityTerrainLayerExport> Layers { get; set; } = [];
	public List<UnityTreePrototypeExport> TreePrototypes { get; set; } = [];
	public List<UnityTreeExport> Trees { get; set; } = [];

	/// <summary>Cells along each side of the detail maps.</summary>
	public int DetailResolution { get; set; }

	/// <summary>
	/// <c>InstanceCountMode</c>, where a detail map value is a plant count per cell, or <c>CoverageMode</c>, where it is
	/// coverage from 0 to 255 scaled by each prototype's density.
	/// </summary>
	public string? DetailScatterMode { get; set; }
	public string? DetailsFile { get; set; }

	/// <summary>How far from the camera Unity draws detail, in metres.</summary>
	public float DetailObjectDistance { get; set; } = 80.0f;

	/// <summary>The terrain's detail density setting, 0 to 1, for prototypes that use density scaling.</summary>
	public float DetailObjectDensity { get; set; } = 1.0f;
	public List<UnityDetailPrototypeExport> DetailPrototypes { get; set; } = [];

	public Vector3 GetPosition() => UnitySpace.ToVector3(Position);

	public Vector3 GetSize() => UnitySpace.ToVector3(Size);
}

public sealed class UnityTerrainLayerExport
{
	public string Name { get; set; } = string.Empty;
	public bool Missing { get; set; }
	public string? DiffuseTexture { get; set; }
	public string? NormalTexture { get; set; }
	public string? MaskTexture { get; set; }
	public float[] TileSize { get; set; } = [1.0f, 1.0f];
	public float NormalScale { get; set; } = 1.0f;

	/// <summary>Multiplies the diffuse texture, per channel, after <see cref="DiffuseRemapMin"/> is added.</summary>
	public float[] DiffuseRemapMax { get; set; } = [1.0f, 1.0f, 1.0f, 1.0f];
	public float[] DiffuseRemapMin { get; set; } = [0.0f, 0.0f, 0.0f, 0.0f];

	/// <summary>Remaps the mask texture's channels (metallic, occlusion, height, smoothness) into this range.</summary>
	public float[] MaskMapRemapMin { get; set; } = [0.0f, 0.0f, 0.0f, 0.0f];
	public float[] MaskMapRemapMax { get; set; } = [1.0f, 1.0f, 1.0f, 1.0f];
}

public sealed class UnityTreePrototypeExport
{
	public string? Prefab { get; set; }
}

/// <summary>A Unity detail prototype: a mesh prefab, or a texture drawn as grass billboards.</summary>
public sealed class UnityDetailPrototypeExport
{
	public bool UsePrototypeMesh { get; set; }
	public string? Prefab { get; set; }
	public string? Texture { get; set; }
	public string? RenderMode { get; set; }
	public float MinWidth { get; set; } = 1.0f;
	public float MaxWidth { get; set; } = 1.0f;
	public float MinHeight { get; set; } = 1.0f;
	public float MaxHeight { get; set; } = 1.0f;
	public float NoiseSpread { get; set; }

	/// <summary>Density relative to the prototype's size, for coverage mode only.</summary>
	public float Density { get; set; } = 1.0f;
	public float AlignToGround { get; set; }
	public float PositionJitter { get; set; } = 1.0f;
	public bool UseDensityScaling { get; set; } = true;
}

public sealed class UnityTreeExport
{
	public int Prototype { get; set; }

	/// <summary>Position within the terrain, 0 to 1 along each axis of its size, from its corner.</summary>
	public float[] Position { get; set; } = [0.0f, 0.0f, 0.0f];

	/// <summary>Rotation about +Y, in radians.</summary>
	public float Rotation { get; set; }
	public float WidthScale { get; set; } = 1.0f;
	public float HeightScale { get; set; } = 1.0f;
}

public sealed class UnityPrefabInstanceExport
{
	public string Name { get; set; } = string.Empty;
	public string PrefabPath { get; set; } = string.Empty;
	public float[] Position { get; set; } = [0.0f, 0.0f, 0.0f];
	public float[] Rotation { get; set; } = [0.0f, 0.0f, 0.0f, 1.0f];
	public float[] Scale { get; set; } = [1.0f, 1.0f, 1.0f];
	public int Overrides { get; set; }
	public List<string> OverrideKinds { get; set; } = [];
}

public sealed class UnityLightExport
{
	public string Name { get; set; } = string.Empty;
	public string Type { get; set; } = string.Empty;
	public float[] Position { get; set; } = [0.0f, 0.0f, 0.0f];
	public float[] Rotation { get; set; } = [0.0f, 0.0f, 0.0f, 1.0f];
	public float[] Color { get; set; } = [1.0f, 1.0f, 1.0f, 1.0f];
	public float Intensity { get; set; } = 1.0f;
	public bool UseColorTemperature { get; set; }
	public float ColorTemperature { get; set; } = 6500.0f;
	public float Range { get; set; } = 10.0f;
}

public sealed class UnityCameraExport
{
	public string Name { get; set; } = string.Empty;
	public bool IsMain { get; set; }
	public float[] Position { get; set; } = [0.0f, 0.0f, 0.0f];
	public float[] Rotation { get; set; } = [0.0f, 0.0f, 0.0f, 1.0f];
	public float FieldOfView { get; set; } = 60.0f;
	public float NearClip { get; set; } = 0.3f;
	public float FarClip { get; set; } = 1000.0f;
}

/// <summary>A prefab's hierarchy as Unity resolves it, nested prefabs included. Node 0 is the root.</summary>
public sealed class UnityPrefabExport
{
	public string Path { get; set; } = string.Empty;
	public string Guid { get; set; } = string.Empty;
	public List<UnityPrefabNodeExport> Nodes { get; set; } = [];
	public UnityLodGroupExport? LodGroup { get; set; }
}

public sealed class UnityPrefabNodeExport
{
	public string Name { get; set; } = string.Empty;

	/// <summary>Index of the parent node, or -1 for the root.</summary>
	public int Parent { get; set; } = -1;
	public bool Active { get; set; } = true;
	public float[] Position { get; set; } = [0.0f, 0.0f, 0.0f];
	public float[] Rotation { get; set; } = [0.0f, 0.0f, 0.0f, 1.0f];
	public float[] Scale { get; set; } = [1.0f, 1.0f, 1.0f];
	public UnityRendererExport? Renderer { get; set; }
	public List<string> OtherComponents { get; set; } = [];
}

public sealed class UnityRendererExport
{
	public bool Enabled { get; set; } = true;

	/// <summary>Key of the mesh in <see cref="UnitySceneExport.Meshes"/>.</summary>
	public string Mesh { get; set; } = string.Empty;

	/// <summary>Material keys, one per submesh; null where Unity has none.</summary>
	public List<string?> Materials { get; set; } = [];
	public bool CastShadows { get; set; } = true;
}

public sealed class UnityLodGroupExport
{
	public int Node { get; set; }
	public float[] LocalReferencePoint { get; set; } = [0.0f, 0.0f, 0.0f];

	/// <summary>The largest axis of the group's bounds, which Unity measures screen height on.</summary>
	public float Size { get; set; }
	public List<UnityLodExport> Lods { get; set; } = [];
}

public sealed class UnityLodExport
{
	/// <summary>Below this share of the screen height the next level draws; below the last one Unity culls.</summary>
	public float ScreenRelativeTransitionHeight { get; set; }
	public List<int> RendererNodes { get; set; } = [];
}

/// <summary>A material with every property its shader declares, read through Unity's material API.</summary>
public sealed class UnityMaterialExport
{
	public string Key { get; set; } = string.Empty;
	public string Name { get; set; } = string.Empty;
	public string? Shader { get; set; }
	public List<string> Keywords { get; set; } = [];
	public Dictionary<string, float> Floats { get; set; } = new();

	/// <summary>Colours as Unity stores them: sRGB, unless the property is HDR.</summary>
	public Dictionary<string, float[]> Colors { get; set; } = new();
	public Dictionary<string, UnityMaterialTextureExport> Textures { get; set; } = new();
}

public sealed class UnityMaterialTextureExport
{
	public string Path { get; set; } = string.Empty;
	public float[] Scale { get; set; } = [1.0f, 1.0f];
	public float[] Offset { get; set; } = [0.0f, 0.0f];
	public bool IsNormalMap { get; set; }
}

/// <summary>
/// A mesh written as glTF beside <c>scene.json</c>, one glTF mesh per submesh named <c>sub0</c>, <c>sub1</c> and so on,
/// in Unity's space once imported (see the exporter's GltfMeshWriter).
/// </summary>
public sealed class UnityMeshExport
{
	public string Key { get; set; } = string.Empty;
	public string Name { get; set; } = string.Empty;
	public string? AssetPath { get; set; }
	public string File { get; set; } = string.Empty;
	public int SubMeshCount { get; set; }
	public int VertexCount { get; set; }
}

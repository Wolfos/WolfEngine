#nullable enable

using System.Numerics;
using System.Text.Json;
using WolfEngine.Importing;
using WolfEngine.Rendering;

namespace WolfEngine.AssetPipeline;

public enum AssetType
{
	Texture2D,
	Material,
	DataAsset,
	Terrain,
	Mesh,
	Model3D,
	Scene,
	Prefab
}

public enum MaterialAssetType
{
	Opaque,
	AlphaTest,
	AlphaBlend
}

public sealed class AssetDatabase
{
	private static IAssetInstanceRegistry? _instanceRegistry;

	public List<AssetDatabaseEntry> Assets { get; set; } = new();

	public static void SetInstanceRegistry(IAssetInstanceRegistry? instanceRegistry)
	{
		_instanceRegistry = instanceRegistry;
	}

	public static void ClearInstanceRegistry()
	{
		_instanceRegistry = null;
	}

	public static T? GetInstance<T>(Guid nodeId)
	{
		if (nodeId == Guid.Empty)
		{
			return default;
		}

		var instanceRegistry = _instanceRegistry
		                       ?? throw new InvalidOperationException(
			                       "No asset instance registry has been configured.");
		var instance = instanceRegistry.GetInstance(nodeId, typeof(T));
		if (instance is null)
		{
			return default;
		}

		if (instance is T typedInstance)
		{
			return typedInstance;
		}

		throw new InvalidOperationException(
			$"Asset node '{nodeId}' resolved to '{instance.GetType().FullName}', which cannot be assigned to '{typeof(T).FullName}'.");
	}
}

public sealed class AssetDatabaseEntry
{
	public Guid Id { get; set; }
	public Guid SourceId { get; set; }
	public AssetType Type { get; set; }
	public string Name { get; set; } = string.Empty;
	public string NodeKey { get; set; } = string.Empty;
	public bool IsGenerated { get; set; }
	public string RelativeSourcePath { get; set; } = string.Empty;
	public string RelativeAssetPath { get; set; } = string.Empty;
	public string RelativeStatePath { get; set; } = string.Empty;
	public string RelativeMetaPath { get; set; } = string.Empty;
	public List<AssetArtifactRecord> Artifacts { get; set; } = new();
	public string SummaryJson { get; set; } = "{}";

	public string GetEffectiveRelativeStatePath()
	{
		return string.IsNullOrWhiteSpace(RelativeStatePath) ? RelativeMetaPath : RelativeStatePath;
	}

	public bool TryGetSummary<T>(out T summary)
	{
		if (string.IsNullOrWhiteSpace(SummaryJson) || string.Equals(SummaryJson, "{}", StringComparison.Ordinal))
		{
			summary = default!;
			return false;
		}

		try
		{
			summary = AssetPipelineSerialization.Deserialize<T>(SummaryJson);
			return summary is not null;
		}
		catch (JsonException)
		{
			summary = default!;
			return false;
		}
	}

	public T GetRequiredSummary<T>()
	{
		if (TryGetSummary<T>(out var summary))
		{
			return summary;
		}

		throw new InvalidOperationException($"Asset node '{Id}' is missing a '{typeof(T).FullName}' summary.");
	}

	public void SetSummary<T>(T summary)
	{
		ArgumentNullException.ThrowIfNull(summary);
		SummaryJson = AssetPipelineSerialization.Serialize(summary);
	}
}

public sealed class TextureAssetSummary
{
	public string RelativeSourceAssetPath { get; set; } = string.Empty;
	public string RelativeImportedPath { get; set; } = string.Empty;
	public string RelativeRuntimeArtifactPath { get; set; } = string.Empty;
	public int Width { get; set; }
	public int Height { get; set; }
	public int Channels { get; set; }
	public TextureSemantic Semantic { get; set; }
	public string SourceExtension { get; set; } = string.Empty;
}

public sealed class MaterialAssetSummary
{
	public MaterialAssetType MaterialType { get; set; }
}

public sealed class DataAssetSummary
{
	public string DataAssetType { get; set; } = string.Empty;
	public string DataAssetTypeId { get; set; } = string.Empty;
	public string DisplayName { get; set; } = string.Empty;
}

public sealed class TerrainAssetSummary
{
	public int HeightmapWidth { get; set; }
	public int HeightmapHeight { get; set; }
	public int LayerMapWidth { get; set; }
	public int LayerMapHeight { get; set; }
	public int LayerMipCount { get; set; }
}

public sealed class MeshAssetSummary
{
	public string RelativeImportedMeshPath { get; set; } = string.Empty;
	public int VertexCount { get; set; }
	public int IndexCount { get; set; }
}

public sealed class Model3DAssetSummary
{
	public string RelativeImportedModelPath { get; set; } = string.Empty;
	public int RootNodeCount { get; set; }
}

public sealed class SceneAssetSummary
{
	public string GlobalCellPath { get; set; } = string.Empty;
	public int SpatialCellCount { get; set; }
}

public sealed class PrefabAssetSummary
{
	public Guid RootEntityId { get; set; }
	public int EntityCount { get; set; }
}

public sealed class TextureAsset
{
	public TextureImportSettings ImportSettings { get; set; } = new();
}

public sealed class TextureImportSettings
{
	public TextureSemantic TextureSemantic { get; set; } = TextureSemantic.BaseColor;
	public int MaxResolution { get; set; } = 8192;
}

public sealed class MaterialAsset
{
	public const int CurrentVersion = 1;
	public const string FileExtension = ".mat.json";

	public int Version { get; set; } = CurrentVersion;
	public AssetType AssetType { get; set; } = AssetType.Material;
	public MaterialAssetType MaterialType { get; set; } = MaterialAssetType.Opaque;
	public OpaqueMaterialProperties Opaque { get; set; } = new();
	public AlphaTestMaterialProperties AlphaTest { get; set; } = new();
	public AlphaBlendMaterialProperties AlphaBlend { get; set; } = new();

	public MaterialSurfaceProperties GetActiveProperties()
	{
		return MaterialType switch
		{
			MaterialAssetType.Opaque => Opaque,
			MaterialAssetType.AlphaTest => AlphaTest,
			MaterialAssetType.AlphaBlend => AlphaBlend,
			_ => Opaque
		};
	}
}

public sealed class DataAssetFile
{
	public const int CurrentVersion = 1;
	public const string FileExtension = ".data.json";

	public int Version { get; set; } = CurrentVersion;
	public AssetType AssetType { get; set; } = AssetType.DataAsset;
	public string DataAssetType { get; set; } = string.Empty;
	public string DataAssetTypeId { get; set; } = string.Empty;
	public System.Text.Json.JsonElement Data { get; set; }
}

public sealed class MaterialTextureAssignments
{
	public AssetRef<Texture> Albedo { get; set; }
	public AssetRef<Texture> Orm { get; set; }
	public AssetRef<Texture> Normal { get; set; }
	public AssetRef<Texture> Emissive { get; set; }
}

public abstract class MaterialSurfaceProperties
{
	public ColorRGBA BaseColor { get; set; } = ColorRGBA.White;
	public float MetallicFactor { get; set; } = 1.0f;
	public float RoughnessFactor { get; set; } = 1.0f;
	public Vector3 EmissiveFactor { get; set; } = Vector3.Zero;
	public float EmissiveIntensity { get; set; } = 1.0f;
	public MaterialTextureAssignments Textures { get; set; } = new();
}

public sealed class OpaqueMaterialProperties : MaterialSurfaceProperties
{
}

public sealed class AlphaTestMaterialProperties : MaterialSurfaceProperties
{
	public float AlphaCutoff { get; set; } = 0.5f;
}

public sealed class AlphaBlendMaterialProperties : MaterialSurfaceProperties
{
	public float AlphaCutoff { get; set; } = 0.5f;
}

public sealed class ImportedMeshAssetFile
{
	public const int CurrentVersion = 1;

	public int Version { get; set; } = CurrentVersion;
	public Vector4[] Vertices { get; set; } = [];
	public uint[] Indices { get; set; } = [];
	public Vector3[] Normals { get; set; } = [];
	public Vector4[] Tangents { get; set; } = [];
	public Vector2[] UVs { get; set; } = [];
}

public sealed class ImportedModelAssetFile
{
	public const int CurrentVersion = 1;

	public int Version { get; set; } = CurrentVersion;
	public string Name { get; set; } = string.Empty;
	public List<ImportedModelAssetNode> RootNodes { get; set; } = new();
}

public sealed class ImportedModelAssetNode
{
	public string Name { get; set; } = string.Empty;
	public Matrix4x4 LocalTransform { get; set; } = Matrix4x4.Identity;
	public List<ImportedModelAssetMeshInstance> Meshes { get; set; } = new();
	public List<ImportedModelAssetNode> Children { get; set; } = new();
}

public sealed class ImportedModelAssetMeshInstance
{
	public string Name { get; set; } = string.Empty;
	public Guid MeshNodeId { get; set; }
	public Guid MaterialNodeId { get; set; }
}
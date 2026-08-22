#nullable enable

using System.Numerics;
using System.Text.Json;
using WolfEngine.Animation;
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
	SceneCell,
	Prefab,
	AudioClip,
	// Persisted as integers in the SQLite index, so new members are appended rather than inserted.
	Skeleton,
	AnimationClip
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
	public bool IsSkinned { get; set; }
}

public sealed class SkeletonAssetSummary
{
	public string RelativeImportedSkeletonPath { get; set; } = string.Empty;
	public int BoneCount { get; set; }
	public string RootBoneName { get; set; } = string.Empty;
}

public sealed class AnimationClipAssetSummary
{
	public string RelativeImportedClipPath { get; set; } = string.Empty;
	public float Duration { get; set; }
	public float FramesPerSecond { get; set; }
	public int TransformTrackCount { get; set; }
	public int PropertyTrackCount { get; set; }
}

public sealed class Model3DAssetSummary
{
	public string RelativeImportedModelPath { get; set; } = string.Empty;
	public int RootNodeCount { get; set; }
	public int SkeletonCount { get; set; }
	public int AnimationCount { get; set; }
}

public sealed class SceneAssetSummary
{
	public Guid GlobalCellId { get; set; }
	public int SpatialCellCount { get; set; }
}

public sealed class SceneCellAssetSummary
{
	public string RelativeCellPath { get; set; } = string.Empty;
	public bool IsGlobal { get; set; }
	public int X { get; set; }
	public int Y { get; set; }
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

public sealed class ModelImportSettings
{
	/// <summary>
	/// Uniform scale applied while the source file is parsed. Assimp's global-scale step bakes it
	/// into mesh vertices, bone offset matrices, animation position keys and node translations, so
	/// no scale is left on the imported hierarchy for the runtime to carry.
	/// </summary>
	public float ScaleFactor { get; set; } = 1.0f;

	/// <summary>
	/// Meta files are hand-editable, and a zero, negative or NaN factor would collapse a model to a
	/// point rather than fail loudly, so the import falls back to the identity scale instead.
	/// </summary>
	public float GetEffectiveScaleFactor()
	{
		return float.IsFinite(ScaleFactor) && ScaleFactor > 0.0f ? ScaleFactor : 1.0f;
	}
}

public sealed class MaterialAsset : MaterialSurfaceProperties
{
	public const int CurrentVersion = 2;
	public const string FileExtension = ".mat.json";

	public int Version { get; set; } = CurrentVersion;
	public AssetType AssetType { get; set; } = AssetType.Material;
	public MaterialAssetType MaterialType { get; set; } = MaterialAssetType.Opaque;
	public float AlphaCutoff { get; set; } = 0.5f;

	// Retained as a compatibility convenience for callers that operate on surface properties.
	public MaterialSurfaceProperties GetActiveProperties() => this;
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
	public float NormalScale { get; set; } = 1.0f;
	public Vector3 EmissiveFactor { get; set; } = Vector3.Zero;
	public float EmissiveIntensity { get; set; } = 1.0f;
	public MaterialTextureAssignments Textures { get; set; } = new();
}

public sealed class ImportedMeshAssetFile
{
	public const int CurrentVersion = 2;

	public int Version { get; set; } = CurrentVersion;
	public Vector4[] Vertices { get; set; } = [];
	public uint[] Indices { get; set; } = [];
	public Vector3[] Normals { get; set; } = [];
	public Vector4[] Tangents { get; set; } = [];
	public Vector2[] UVs { get; set; } = [];

	/// <summary>
	/// Four bone influences per vertex, flattened. Empty for unskinned meshes, which is every mesh
	/// written before artifact version 2.
	/// </summary>
	public uint[] BoneIndices { get; set; } = [];

	public float[] BoneWeights { get; set; } = [];
}

public sealed class ImportedSkeletonAssetFile
{
	public const int CurrentVersion = 1;

	public int Version { get; set; } = CurrentVersion;
	public string Name { get; set; } = string.Empty;
	public string[] BoneNames { get; set; } = [];
	public int[] ParentIndices { get; set; } = [];
	public BoneTransform[] BindPoseLocal { get; set; } = [];
	public Matrix4x4[] InverseBindMatrices { get; set; } = [];

	public Skeleton ToSkeleton() =>
		new(Name, BoneNames, ParentIndices, BindPoseLocal, InverseBindMatrices);
}

public sealed class ImportedAnimationClipAssetFile
{
	public const int CurrentVersion = 1;

	public int Version { get; set; } = CurrentVersion;
	public string Name { get; set; } = string.Empty;
	public float Duration { get; set; }
	public float FramesPerSecond { get; set; } = 30.0f;
	public bool Loop { get; set; } = true;
	public TransformTrack[] TransformTracks { get; set; } = [];
	public PropertyTrack[] PropertyTracks { get; set; } = [];
	public string SourceSkeletonName { get; set; } = string.Empty;
	public BoneTransform[] SourceBindPoseLocal { get; set; } = [];

	public AnimationClip ToClip() =>
		new(Name, Duration, FramesPerSecond, Loop, TransformTracks, PropertyTracks, SourceSkeletonName, SourceBindPoseLocal);
}

public sealed class ImportedModelAssetFile
{
	public const int CurrentVersion = 3;

	public int Version { get; set; } = CurrentVersion;
	public string Name { get; set; } = string.Empty;
	public List<ImportedModelAssetNode> Nodes { get; set; } = new();

	/// <summary>Skeleton sub-assets produced by this source, in the order the importer discovered them.</summary>
	public List<Guid> SkeletonNodeIds { get; set; } = new();

	/// <summary>Animation clip sub-assets produced by this source.</summary>
	public List<Guid> AnimationNodeIds { get; set; } = new();
}

public sealed class ImportedModelAssetNode
{
	public string Name { get; set; } = string.Empty;
	public Matrix4x4 LocalTransform { get; set; } = Matrix4x4.Identity;
	public List<ImportedModelAssetMeshInstance> Meshes { get; set; } = new();
	public int ParentIndex { get; set; } = -1;
}

public sealed class ImportedModelAssetMeshInstance
{
	public string Name { get; set; } = string.Empty;
	public Guid MeshNodeId { get; set; }
	public Guid MaterialNodeId { get; set; }

	/// <summary>Skeleton this mesh is skinned to, or <see cref="Guid.Empty"/> for static geometry.</summary>
	public Guid SkeletonNodeId { get; set; }
}

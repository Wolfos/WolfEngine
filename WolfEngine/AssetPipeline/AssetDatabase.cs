#nullable enable

using System.Numerics;
using WolfEngine.Rendering;

namespace WolfEngine.AssetPipeline;

public enum AssetType
{
	Texture2D,
	Material,
	DataAsset
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

	public const int CurrentVersion = 4;
	public const string FileName = "AssetDatabase.json";

	public int Version { get; set; } = CurrentVersion;
	public List<AssetDatabaseEntry> Assets { get; set; } = new();

	public static void SetInstanceRegistry(IAssetInstanceRegistry? instanceRegistry)
	{
		_instanceRegistry = instanceRegistry;
	}

	public static void ClearInstanceRegistry()
	{
		_instanceRegistry = null;
	}

	public static T? GetInstance<T>(Guid id)
	{
		if (id == Guid.Empty)
		{
			return default;
		}

		var instanceRegistry = _instanceRegistry
			?? throw new InvalidOperationException("No asset instance registry has been configured.");
		var instance = instanceRegistry.GetInstance(id, typeof(T));
		if (instance is null)
		{
			return default;
		}

		if (instance is T typedInstance)
		{
			return typedInstance;
		}

		throw new InvalidOperationException(
			$"Asset '{id}' resolved to '{instance.GetType().FullName}', which cannot be assigned to '{typeof(T).FullName}'.");
	}
}

public sealed class AssetDatabaseEntry
{
	public Guid Id { get; set; }
	public AssetType Type { get; set; }
	public string Name { get; set; } = string.Empty;
	public string RelativeAssetPath { get; set; } = string.Empty;
	public string RelativeStatePath { get; set; } = string.Empty;
	public string RelativeMetaPath { get; set; } = string.Empty;
	public TextureAssetSummary? TextureSummary { get; set; }
	public MaterialAssetSummary? MaterialSummary { get; set; }
	public DataAssetSummary? DataAssetSummary { get; set; }

	public string GetEffectiveRelativeStatePath()
	{
		return string.IsNullOrWhiteSpace(RelativeStatePath) == false
			? RelativeStatePath
			: RelativeMetaPath;
	}
}

public sealed class TextureAssetSummary
{
	public string RelativeSourceAssetPath { get; set; } = string.Empty;
	public string RelativeRawImagePath { get; set; } = string.Empty;
	public int Width { get; set; }
	public int Height { get; set; }
	public int Channels { get; set; }
	public bool IsSrgb { get; set; }
	public string SourceExtension { get; set; } = string.Empty;
}

public sealed class MaterialAssetSummary
{
	public MaterialAssetType MaterialType { get; set; }
}

public sealed class DataAssetSummary
{
	public string DataAssetType { get; set; } = string.Empty;
	public string DisplayName { get; set; } = string.Empty;
}

public sealed class AssetArtifactInfo
{
	public string Kind { get; set; } = string.Empty;
	public string Target { get; set; } = string.Empty;
	public string RelativePath { get; set; } = string.Empty;
	public string ContentHash { get; set; } = string.Empty;
	public int Version { get; set; } = 1;
}

public sealed class TextureAsset
{
	public const int CurrentVersion = 1;
	public const string FileExtension = ".tex.json";

	public int Version { get; set; } = CurrentVersion;
	public AssetType AssetType { get; set; } = AssetType.Texture2D;
	public string RelativeSourceAssetPath { get; set; } = string.Empty;
	public TextureImportSettings ImportSettings { get; set; } = new();
}

public sealed class TextureAssetMetaFile
{
	public const int CurrentVersion = 2;

	public int Version { get; set; } = CurrentVersion;
	public Guid AssetId { get; set; }
	public AssetType AssetType { get; set; } = AssetType.Texture2D;
	public string SourceFileName { get; set; } = string.Empty;
	public TextureImportSettings ImportSettings { get; set; } = new();
	public TextureImportArtifacts Artifacts { get; set; } = new();
	public TextureAssetSummary Summary { get; set; } = new();
}

public sealed class TextureAssetStateFile
{
	public const int CurrentVersion = 1;

	public int Version { get; set; } = CurrentVersion;
	public Guid AssetId { get; set; }
	public AssetType AssetType { get; set; } = AssetType.Texture2D;
	public TextureAssetSummary Summary { get; set; } = new();
	public List<AssetArtifactInfo> Artifacts { get; set; } = new();
}

public sealed class TextureImportSettings
{
	public bool IsSrgb { get; set; }
	public int MaxResolution { get; set; } = 8192;
}

public sealed class TextureImportArtifacts
{
	public string RelativeRawImagePath { get; set; } = string.Empty;
}

public sealed class MaterialMetaFile
{
	public const int CurrentVersion = 1;

	public int Version { get; set; } = CurrentVersion;
	public Guid AssetId { get; set; }
	public AssetType AssetType { get; set; } = AssetType.Material;
	public MaterialAssetType MaterialType { get; set; } = MaterialAssetType.Opaque;
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

public sealed class MaterialAssetStateFile
{
	public const int CurrentVersion = 1;

	public int Version { get; set; } = CurrentVersion;
	public Guid AssetId { get; set; }
	public AssetType AssetType { get; set; } = AssetType.Material;
	public MaterialAssetSummary Summary { get; set; } = new();
	public List<AssetArtifactInfo> Artifacts { get; set; } = new();
}

public sealed class DataAssetMetaFile
{
	public const int CurrentVersion = 1;

	public int Version { get; set; } = CurrentVersion;
	public Guid AssetId { get; set; }
	public AssetType AssetType { get; set; } = AssetType.DataAsset;
	public string DataAssetType { get; set; } = string.Empty;
}

public sealed class DataAssetFile
{
	public const int CurrentVersion = 1;
	public const string FileExtension = ".data.json";

	public int Version { get; set; } = CurrentVersion;
	public AssetType AssetType { get; set; } = AssetType.DataAsset;
	public string DataAssetType { get; set; } = string.Empty;
	public System.Text.Json.JsonElement Data { get; set; }
}

public sealed class MaterialTextureAssignments
{
	public AssetLink<Texture> Albedo { get; set; }
	public AssetLink<Texture> MetallicRoughness { get; set; }
	public AssetLink<Texture> Normal { get; set; }
	public AssetLink<Texture> Emissive { get; set; }
	public AssetLink<Texture> Occlusion { get; set; }
}

public abstract class MaterialSurfaceProperties
{
	public ColorRGBA BaseColor { get; set; } = ColorRGBA.White;
	public float MetallicFactor { get; set; } = 1.0f;
	public float RoughnessFactor { get; set; } = 1.0f;
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

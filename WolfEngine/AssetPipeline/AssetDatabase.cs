using System.Numerics;

namespace WolfEngine.AssetPipeline;

public enum AssetType
{
	Texture2D,
	Material
}

public enum MaterialAssetType
{
	Opaque,
	AlphaTest,
	AlphaBlend
}

public sealed class AssetDatabase
{
	public const int CurrentVersion = 2;
	public const string FileName = "AssetDatabase.json";

	public int Version { get; set; } = CurrentVersion;
	public List<AssetDatabaseEntry> Assets { get; set; } = new();
}

public sealed class AssetDatabaseEntry
{
	public Guid Id { get; set; }
	public AssetType Type { get; set; }
	public string Name { get; set; } = string.Empty;
	public string RelativeAssetPath { get; set; } = string.Empty;
	public string RelativeMetaPath { get; set; } = string.Empty;
	public TextureAssetSummary? TextureSummary { get; set; }
	public MaterialAssetSummary? MaterialSummary { get; set; }
}

public sealed class TextureAssetSummary
{
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

public sealed class MaterialAssetFile
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

public sealed class MaterialTextureAssignments
{
	public Guid? Albedo { get; set; }
	public Guid? MetallicRoughness { get; set; }
	public Guid? Normal { get; set; }
	public Guid? Emissive { get; set; }
	public Guid? Occlusion { get; set; }
}

public sealed class ColorRgba
{
	public float R { get; set; } = 1.0f;
	public float G { get; set; } = 1.0f;
	public float B { get; set; } = 1.0f;
	public float A { get; set; } = 1.0f;

	public Vector4 ToVector4() => new(R, G, B, A);

	public static ColorRgba FromVector4(Vector4 value)
	{
		return new ColorRgba
		{
			R = value.X,
			G = value.Y,
			B = value.Z,
			A = value.W
		};
	}
}

public abstract class MaterialSurfaceProperties
{
	public ColorRgba BaseColor { get; set; } = new();
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

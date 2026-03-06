using System.Text.Json.Serialization;

namespace WolfEngine.AssetPipeline;

public enum AssetType
{
	Texture2D
}

public sealed class AssetDatabase
{
	public const int CurrentVersion = 1;
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
	public string RelativeRawImagePath { get; set; } = string.Empty;
	public int Width { get; set; }
	public int Height { get; set; }
	public int Channels { get; set; }
	public bool IsSrgb { get; set; }
	public string SourceExtension { get; set; } = string.Empty;
}

public sealed class TextureAssetMetaFile
{
	public const int CurrentVersion = 1;

	public int Version { get; set; } = CurrentVersion;
	public Guid AssetId { get; set; }
	public AssetType AssetType { get; set; }
	public string SourceFileName { get; set; } = string.Empty;
	public TextureImportSettings ImportSettings { get; set; } = new();
	public TextureImportResultMetadata ImportResult { get; set; } = new();
}

public sealed class TextureImportSettings
{
	public bool IsSrgb { get; set; }
}

public sealed class TextureImportResultMetadata
{
	public int Width { get; set; }
	public int Height { get; set; }
	public int Channels { get; set; }
	public string RelativeRawImagePath { get; set; } = string.Empty;
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace WolfEngine.AssetPipeline;

public static class AssetPipelinePaths
{
	public const string AssetsFolderName = "Assets";
	public const string LibraryFolderName = "Library";
	public const string ImportedFolderName = "Imported";
	public const string ArtifactsFolderName = "Artifacts";
	public const string SqliteFileName = "AssetPipeline.sqlite";

	public static string GetAssetsPath(string projectRootPath) => Path.Combine(projectRootPath, AssetsFolderName);
	public static string GetLibraryPath(string projectRootPath) => Path.Combine(projectRootPath, LibraryFolderName);
	public static string GetImportedRoot(string projectRootPath) => Path.Combine(GetLibraryPath(projectRootPath), ImportedFolderName);
	public static string GetArtifactsRoot(string projectRootPath) => Path.Combine(GetLibraryPath(projectRootPath), ArtifactsFolderName);
	public static string GetSqlitePath(string projectRootPath) => Path.Combine(GetLibraryPath(projectRootPath), SqliteFileName);
}

public static class AssetImporterIds
{
	public const string Texture = "texture";
	public const string Material = "material";
	public const string DataAsset = "data";
	public const string ThreeDScene = "three-d-scene";
	public const string EditorScene = "editor-scene";
}

public sealed class AssetSourceMetaFile
{
	public const int CurrentVersion = 1;

	public int Version { get; set; } = CurrentVersion;
	public Guid SourceId { get; set; }
	public string ImporterId { get; set; } = string.Empty;
	public int ImporterVersion { get; set; } = 1;
	public TextureImportSettings? TextureImportSettings { get; set; }
	public string SourceContentHash { get; set; } = string.Empty;
	public long SourceFileSize { get; set; }
	public long SourceLastWriteTimeUtcTicks { get; set; }
	public List<AssetSubAssetManifestEntry> SubAssets { get; set; } = new();
}

public sealed class AssetSubAssetManifestEntry
{
	public string Key { get; set; } = string.Empty;
	public Guid NodeId { get; set; }
	public AssetType Type { get; set; }
	public string Name { get; set; } = string.Empty;
}

public sealed class AssetSourceRecord
{
	public Guid SourceId { get; set; }
	public string RelativeSourcePath { get; set; } = string.Empty;
	public string RelativeMetaPath { get; set; } = string.Empty;
	public string ImporterId { get; set; } = string.Empty;
	public int ImporterVersion { get; set; }
	public string SourceContentHash { get; set; } = string.Empty;
	public long SourceFileSize { get; set; }
	public long SourceLastWriteTimeUtcTicks { get; set; }
	public string ImportSettingsJson { get; set; } = "{}";
}

public sealed class AssetNodeRecord
{
	public Guid NodeId { get; set; }
	public Guid SourceId { get; set; }
	public AssetType Type { get; set; }
	public string NodeKey { get; set; } = string.Empty;
	public string Name { get; set; } = string.Empty;
	public bool IsGenerated { get; set; }
	public string RelativeSourcePath { get; set; } = string.Empty;
	public string RelativeAssetPath { get; set; } = string.Empty;
	public string RelativeMetaPath { get; set; } = string.Empty;
	public string SummaryJson { get; set; } = "{}";
}

public sealed class AssetArtifactRecord
{
	public Guid NodeId { get; set; }
	public string ArtifactKey { get; set; } = string.Empty;
	public string Kind { get; set; } = string.Empty;
	public string Target { get; set; } = string.Empty;
	public string RelativePath { get; set; } = string.Empty;
	public string ContentHash { get; set; } = string.Empty;
	public long ByteSize { get; set; }
	public int ChunkIndex { get; set; }
	public int ChunkCount { get; set; }
	public string StreamGroup { get; set; } = string.Empty;
	public string MetadataJson { get; set; } = "{}";
}

public sealed class AssetDependencyRecord
{
	public Guid FromNodeId { get; set; }
	public Guid ToNodeId { get; set; }
	public string Kind { get; set; } = string.Empty;
	public bool IsHard { get; set; } = true;
}

public sealed class AssetImportResult
{
	public Guid? PrimaryNodeId { get; init; }
	public Guid? PrimarySourceId { get; init; }
}

public static class AssetPipelineSerialization
{
	public static string Serialize<T>(T value)
	{
		return JsonSerializer.Serialize(value, AssetJson.SerializerOptions);
	}

	public static T Deserialize<T>(string json)
	{
		return JsonSerializer.Deserialize<T>(json, AssetJson.SerializerOptions)
		       ?? throw new InvalidOperationException($"Failed to deserialize '{typeof(T).FullName}'.");
	}
}

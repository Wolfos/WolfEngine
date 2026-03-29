using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace WolfEngine.AssetPipeline;

public static class AssetFileExtensions
{
	public static string GetMetaPath(string absoluteSourcePath) => absoluteSourcePath + ".meta";

	public static string GetRelativeMetaPath(string relativeSourcePath) => relativeSourcePath + ".meta";
}

public interface IAssetMetadataStore
{
	AssetSourceMetaFile Load(string absoluteMetaPath);
	void Save(string absoluteMetaPath, AssetSourceMetaFile metadata);
}

public sealed class AssetMetadataStore : IAssetMetadataStore
{
	public AssetSourceMetaFile Load(string absoluteMetaPath)
	{
		if (string.IsNullOrWhiteSpace(absoluteMetaPath))
		{
			throw new ArgumentException("Meta path cannot be null or empty.", nameof(absoluteMetaPath));
		}

		var json = File.ReadAllText(absoluteMetaPath);
		var metadata = JsonSerializer.Deserialize<AssetSourceMetaFile>(json, AssetJson.SerializerOptions)
		               ?? throw new InvalidOperationException($"Failed to deserialize asset metadata '{absoluteMetaPath}'.");
		if (metadata.Version != AssetSourceMetaFile.CurrentVersion)
		{
			throw new InvalidOperationException(
				$"Unsupported asset metadata version {metadata.Version}. Expected {AssetSourceMetaFile.CurrentVersion}.");
		}

		metadata.SubAssets ??= new List<AssetSubAssetManifestEntry>();
		return metadata;
	}

	public void Save(string absoluteMetaPath, AssetSourceMetaFile metadata)
	{
		if (string.IsNullOrWhiteSpace(absoluteMetaPath))
		{
			throw new ArgumentException("Meta path cannot be null or empty.", nameof(absoluteMetaPath));
		}

		ArgumentNullException.ThrowIfNull(metadata);
		metadata.Version = AssetSourceMetaFile.CurrentVersion;
		metadata.SubAssets ??= new List<AssetSubAssetManifestEntry>();
		WriteJsonAtomically(absoluteMetaPath, metadata);
	}

	private static void WriteJsonAtomically<T>(string path, T value)
	{
		var directory = Path.GetDirectoryName(path);
		if (string.IsNullOrWhiteSpace(directory) == false)
		{
			Directory.CreateDirectory(directory);
		}

		var tempPath = path + ".tmp";
		var json = JsonSerializer.Serialize(value, AssetJson.SerializerOptions);
		File.WriteAllText(tempPath, json);
		File.Move(tempPath, path, true);
	}
}

public static class AssetHashing
{
	public static string ComputeFileHash(string absolutePath)
	{
		using var stream = File.OpenRead(absolutePath);
		using var sha256 = SHA256.Create();
		var hash = sha256.ComputeHash(stream);
		return Convert.ToHexString(hash);
	}
}

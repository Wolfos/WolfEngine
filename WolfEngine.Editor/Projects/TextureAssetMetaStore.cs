using System.Text.Json;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.Projects;

public interface ITextureAssetMetaStore
{
	TextureAssetMetaFile Load(string metaFilePath);
	void Save(string metaFilePath, TextureAssetMetaFile metaFile);
}

public sealed class TextureAssetMetaStore : ITextureAssetMetaStore
{
	public TextureAssetMetaFile Load(string metaFilePath)
	{
		if (string.IsNullOrWhiteSpace(metaFilePath))
		{
			throw new ArgumentException("Texture meta path cannot be null or empty.", nameof(metaFilePath));
		}

		var json = File.ReadAllText(metaFilePath);
		var metaFile = JsonSerializer.Deserialize<TextureAssetMetaFile>(json, AssetJson.SerializerOptions)
			?? throw new InvalidOperationException($"Failed to deserialize texture metadata '{metaFilePath}'.");

		if (metaFile.Version != TextureAssetMetaFile.CurrentVersion)
		{
			throw new InvalidOperationException(
				$"Unsupported texture metadata version {metaFile.Version}. Expected {TextureAssetMetaFile.CurrentVersion}.");
		}

		metaFile.ImportSettings ??= new TextureImportSettings();
		metaFile.Artifacts ??= new TextureImportArtifacts();
		metaFile.Summary ??= new TextureAssetSummary();
		return metaFile;
	}

	public void Save(string metaFilePath, TextureAssetMetaFile metaFile)
	{
		if (string.IsNullOrWhiteSpace(metaFilePath))
		{
			throw new ArgumentException("Texture meta path cannot be null or empty.", nameof(metaFilePath));
		}

		ArgumentNullException.ThrowIfNull(metaFile);
		metaFile.Version = TextureAssetMetaFile.CurrentVersion;
		metaFile.AssetType = AssetType.Texture2D;
		metaFile.ImportSettings ??= new TextureImportSettings();
		metaFile.Artifacts ??= new TextureImportArtifacts();
		metaFile.Summary ??= new TextureAssetSummary();
		WriteJsonAtomically(metaFilePath, metaFile);
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

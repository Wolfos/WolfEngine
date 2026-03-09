using System.Text.Json;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.Projects;

public interface ITextureAssetStore
{
	TextureAsset Create(string relativeSourceAssetPath, TextureImportSettings? importSettings = null);
	TextureAsset LoadAsset(string assetFilePath);
	TextureAssetStateFile CreateState(Guid assetId, TextureAssetSummary summary, IEnumerable<AssetArtifactInfo>? artifacts = null);
	TextureAssetStateFile LoadState(string stateFilePath);
	void SaveAsset(string assetFilePath, TextureAsset assetFile);
	void SaveState(string stateFilePath, TextureAssetStateFile stateFile);
	string GetAssetRelativePath(string assetName);
	string GetSourceRelativePath(string assetName, string sourceExtension);
	string GetStateRelativePath(Guid assetId);
	string GetRuntimeArtifactRelativePath(Guid assetId, string target = "");
	IReadOnlyList<AssetArtifactInfo> CreateDefaultRuntimeArtifacts(Guid assetId);
}

public sealed class TextureAssetStore : ITextureAssetStore
{
	public TextureAsset Create(string relativeSourceAssetPath, TextureImportSettings? importSettings = null)
	{
		return new TextureAsset
		{
			RelativeSourceAssetPath = relativeSourceAssetPath ?? throw new ArgumentNullException(nameof(relativeSourceAssetPath)),
			ImportSettings = importSettings ?? new TextureImportSettings()
		};
	}

	public TextureAsset LoadAsset(string assetFilePath)
	{
		if (string.IsNullOrWhiteSpace(assetFilePath))
		{
			throw new ArgumentException("Texture asset path cannot be null or empty.", nameof(assetFilePath));
		}

		var json = File.ReadAllText(assetFilePath);
		var assetFile = JsonSerializer.Deserialize<TextureAsset>(json, AssetJson.SerializerOptions)
			?? throw new InvalidOperationException($"Failed to deserialize texture asset '{assetFilePath}'.");
		if (assetFile.Version != TextureAsset.CurrentVersion)
		{
			throw new InvalidOperationException(
				$"Unsupported texture asset version {assetFile.Version}. Expected {TextureAsset.CurrentVersion}.");
		}

		assetFile.ImportSettings ??= new TextureImportSettings();
		return assetFile;
	}

	public TextureAssetStateFile CreateState(Guid assetId, TextureAssetSummary summary, IEnumerable<AssetArtifactInfo>? artifacts = null)
	{
		return new TextureAssetStateFile
		{
			AssetId = assetId,
			Summary = summary ?? throw new ArgumentNullException(nameof(summary)),
			Artifacts = artifacts?.ToList() ?? []
		};
	}

	public TextureAssetStateFile LoadState(string stateFilePath)
	{
		if (string.IsNullOrWhiteSpace(stateFilePath))
		{
			throw new ArgumentException("Texture state path cannot be null or empty.", nameof(stateFilePath));
		}

		var json = File.ReadAllText(stateFilePath);
		var stateFile = JsonSerializer.Deserialize<TextureAssetStateFile>(json, AssetJson.SerializerOptions)
			?? throw new InvalidOperationException($"Failed to deserialize texture state '{stateFilePath}'.");
		if (stateFile.Version != TextureAssetStateFile.CurrentVersion)
		{
			throw new InvalidOperationException(
				$"Unsupported texture state version {stateFile.Version}. Expected {TextureAssetStateFile.CurrentVersion}.");
		}

		stateFile.Summary ??= new TextureAssetSummary();
		stateFile.Artifacts ??= [];
		return stateFile;
	}

	public void SaveAsset(string assetFilePath, TextureAsset assetFile)
	{
		if (string.IsNullOrWhiteSpace(assetFilePath))
		{
			throw new ArgumentException("Texture asset path cannot be null or empty.", nameof(assetFilePath));
		}

		ArgumentNullException.ThrowIfNull(assetFile);
		assetFile.Version = TextureAsset.CurrentVersion;
		assetFile.AssetType = AssetType.Texture2D;
		assetFile.ImportSettings ??= new TextureImportSettings();
		WriteJsonAtomically(assetFilePath, assetFile);
	}

	public void SaveState(string stateFilePath, TextureAssetStateFile stateFile)
	{
		if (string.IsNullOrWhiteSpace(stateFilePath))
		{
			throw new ArgumentException("Texture state path cannot be null or empty.", nameof(stateFilePath));
		}

		ArgumentNullException.ThrowIfNull(stateFile);
		stateFile.Version = TextureAssetStateFile.CurrentVersion;
		stateFile.AssetType = AssetType.Texture2D;
		stateFile.Summary ??= new TextureAssetSummary();
		stateFile.Artifacts ??= [];
		WriteJsonAtomically(stateFilePath, stateFile);
	}

	public string GetAssetRelativePath(string assetName)
	{
		return $"Assets/{assetName}{TextureAsset.FileExtension}";
	}

	public string GetSourceRelativePath(string assetName, string sourceExtension)
	{
		var normalizedExtension = string.IsNullOrWhiteSpace(sourceExtension)
			? string.Empty
			: sourceExtension.StartsWith('.') ? sourceExtension : $".{sourceExtension}";
		return $"Assets/{assetName}.source{normalizedExtension}";
	}

	public string GetStateRelativePath(Guid assetId)
	{
		return $"Database/{assetId:D}.assetstate.json";
	}

	public string GetRuntimeArtifactRelativePath(Guid assetId, string target = "")
	{
		var suffix = string.IsNullOrWhiteSpace(target) ? string.Empty : $".{target}";
		return $"Database/{assetId:D}/runtimetexture{suffix}.bin";
	}

	public IReadOnlyList<AssetArtifactInfo> CreateDefaultRuntimeArtifacts(Guid assetId)
	{
		var sharedPath = GetRuntimeArtifactRelativePath(assetId);
		return
		[
			new AssetArtifactInfo
			{
				Kind = "RuntimeTexture",
				RelativePath = sharedPath,
				Version = TextureRawImageSerializer.CurrentVersion
			},
			new AssetArtifactInfo
			{
				Kind = "RuntimeTexture",
				Target = "metal",
				RelativePath = sharedPath,
				Version = TextureRawImageSerializer.CurrentVersion
			},
			new AssetArtifactInfo
			{
				Kind = "RuntimeTexture",
				Target = "d3d12",
				RelativePath = sharedPath,
				Version = TextureRawImageSerializer.CurrentVersion
			}
		];
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

using System.Text.Json;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.Projects;

public interface IMaterialAssetStore
{
	MaterialAssetFile CreateDefault(MaterialAssetType materialType = MaterialAssetType.Opaque);
	MaterialMetaFile CreateMeta(Guid assetId, MaterialAssetType materialType);
	MaterialAssetFile LoadAsset(string assetFilePath);
	MaterialMetaFile LoadMeta(string metaFilePath);
	void SaveAsset(string assetFilePath, MaterialAssetFile assetFile);
	void SaveMeta(string metaFilePath, MaterialMetaFile metaFile);
}

public sealed class MaterialAssetStore : IMaterialAssetStore
{
	public MaterialAssetFile CreateDefault(MaterialAssetType materialType = MaterialAssetType.Opaque)
	{
		return new MaterialAssetFile
		{
			MaterialType = materialType,
			Opaque = new OpaqueMaterialProperties(),
			AlphaTest = new AlphaTestMaterialProperties(),
			AlphaBlend = new AlphaBlendMaterialProperties()
		};
	}

	public MaterialMetaFile CreateMeta(Guid assetId, MaterialAssetType materialType)
	{
		return new MaterialMetaFile
		{
			AssetId = assetId,
			MaterialType = materialType
		};
	}

	public MaterialAssetFile LoadAsset(string assetFilePath)
	{
		if (string.IsNullOrWhiteSpace(assetFilePath))
		{
			throw new ArgumentException("Material asset path cannot be null or empty.", nameof(assetFilePath));
		}

		var json = File.ReadAllText(assetFilePath);
		var assetFile = JsonSerializer.Deserialize<MaterialAssetFile>(json, AssetJson.SerializerOptions)
			?? throw new InvalidOperationException($"Failed to deserialize material asset '{assetFilePath}'.");
		if (assetFile.Version != MaterialAssetFile.CurrentVersion)
		{
			throw new InvalidOperationException(
				$"Unsupported material asset version {assetFile.Version}. Expected {MaterialAssetFile.CurrentVersion}.");
		}

		assetFile.Opaque ??= new OpaqueMaterialProperties();
		assetFile.AlphaTest ??= new AlphaTestMaterialProperties();
		assetFile.AlphaBlend ??= new AlphaBlendMaterialProperties();
		return assetFile;
	}

	public MaterialMetaFile LoadMeta(string metaFilePath)
	{
		if (string.IsNullOrWhiteSpace(metaFilePath))
		{
			throw new ArgumentException("Material meta path cannot be null or empty.", nameof(metaFilePath));
		}

		var json = File.ReadAllText(metaFilePath);
		var metaFile = JsonSerializer.Deserialize<MaterialMetaFile>(json, AssetJson.SerializerOptions)
			?? throw new InvalidOperationException($"Failed to deserialize material metadata '{metaFilePath}'.");
		if (metaFile.Version != MaterialMetaFile.CurrentVersion)
		{
			throw new InvalidOperationException(
				$"Unsupported material metadata version {metaFile.Version}. Expected {MaterialMetaFile.CurrentVersion}.");
		}

		return metaFile;
	}

	public void SaveAsset(string assetFilePath, MaterialAssetFile assetFile)
	{
		if (string.IsNullOrWhiteSpace(assetFilePath))
		{
			throw new ArgumentException("Material asset path cannot be null or empty.", nameof(assetFilePath));
		}

		ArgumentNullException.ThrowIfNull(assetFile);
		assetFile.Version = MaterialAssetFile.CurrentVersion;
		assetFile.AssetType = AssetType.Material;
		assetFile.Opaque ??= new OpaqueMaterialProperties();
		assetFile.AlphaTest ??= new AlphaTestMaterialProperties();
		assetFile.AlphaBlend ??= new AlphaBlendMaterialProperties();
		WriteJsonAtomically(assetFilePath, assetFile);
	}

	public void SaveMeta(string metaFilePath, MaterialMetaFile metaFile)
	{
		if (string.IsNullOrWhiteSpace(metaFilePath))
		{
			throw new ArgumentException("Material meta path cannot be null or empty.", nameof(metaFilePath));
		}

		ArgumentNullException.ThrowIfNull(metaFile);
		metaFile.Version = MaterialMetaFile.CurrentVersion;
		metaFile.AssetType = AssetType.Material;
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

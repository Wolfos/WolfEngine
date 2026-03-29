using System;
using System.IO;
using System.Text.Json;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.Projects;

public interface IMaterialAssetStore
{
	MaterialAsset CreateDefault(MaterialAssetType materialType = MaterialAssetType.Opaque);
	MaterialAsset LoadAsset(string assetFilePath);
	void SaveAsset(string assetFilePath, MaterialAsset assetFile);
}

public sealed class MaterialAssetStore : IMaterialAssetStore
{
	public MaterialAsset CreateDefault(MaterialAssetType materialType = MaterialAssetType.Opaque)
	{
		return new MaterialAsset
		{
			MaterialType = materialType,
			Opaque = new OpaqueMaterialProperties(),
			AlphaTest = new AlphaTestMaterialProperties(),
			AlphaBlend = new AlphaBlendMaterialProperties()
		};
	}

	public MaterialAsset LoadAsset(string assetFilePath)
	{
		if (string.IsNullOrWhiteSpace(assetFilePath))
		{
			throw new ArgumentException("Material asset path cannot be null or empty.", nameof(assetFilePath));
		}

		var json = File.ReadAllText(assetFilePath);
		var assetFile = JsonSerializer.Deserialize<MaterialAsset>(json, AssetJson.SerializerOptions)
			?? throw new InvalidOperationException($"Failed to deserialize material asset '{assetFilePath}'.");
		if (assetFile.Version != MaterialAsset.CurrentVersion)
		{
			throw new InvalidOperationException(
				$"Unsupported material asset version {assetFile.Version}. Expected {MaterialAsset.CurrentVersion}.");
		}

		assetFile.Opaque ??= new OpaqueMaterialProperties();
		assetFile.AlphaTest ??= new AlphaTestMaterialProperties();
		assetFile.AlphaBlend ??= new AlphaBlendMaterialProperties();
		assetFile.Opaque.Textures ??= new MaterialTextureAssignments();
		assetFile.AlphaTest.Textures ??= new MaterialTextureAssignments();
		assetFile.AlphaBlend.Textures ??= new MaterialTextureAssignments();
		return assetFile;
	}

	public void SaveAsset(string assetFilePath, MaterialAsset assetFile)
	{
		if (string.IsNullOrWhiteSpace(assetFilePath))
		{
			throw new ArgumentException("Material asset path cannot be null or empty.", nameof(assetFilePath));
		}

		ArgumentNullException.ThrowIfNull(assetFile);
		assetFile.Version = MaterialAsset.CurrentVersion;
		assetFile.AssetType = AssetType.Material;
		assetFile.Opaque ??= new OpaqueMaterialProperties();
		assetFile.AlphaTest ??= new AlphaTestMaterialProperties();
		assetFile.AlphaBlend ??= new AlphaBlendMaterialProperties();
		assetFile.Opaque.Textures ??= new MaterialTextureAssignments();
		assetFile.AlphaTest.Textures ??= new MaterialTextureAssignments();
		assetFile.AlphaBlend.Textures ??= new MaterialTextureAssignments();
		WriteJsonAtomically(assetFilePath, assetFile);
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

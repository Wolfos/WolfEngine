using System.Text.Json;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.Projects;

public interface IMaterialAssetStore
{
	MaterialAsset CreateDefault(MaterialAssetType materialType = MaterialAssetType.Opaque);
	MaterialAssetStateFile CreateState(Guid assetId, MaterialAssetType materialType);
	MaterialAsset LoadAsset(string assetFilePath);
	MaterialAssetStateFile LoadState(string stateFilePath);
	void SaveAsset(string assetFilePath, MaterialAsset assetFile);
	void SaveState(string stateFilePath, MaterialAssetStateFile stateFile);
	string GetStateRelativePath(Guid assetId);
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

	public MaterialAssetStateFile CreateState(Guid assetId, MaterialAssetType materialType)
	{
		return new MaterialAssetStateFile
		{
			AssetId = assetId,
			Summary = new MaterialAssetSummary
			{
				MaterialType = materialType
			}
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

	public MaterialAssetStateFile LoadState(string stateFilePath)
	{
		if (string.IsNullOrWhiteSpace(stateFilePath))
		{
			throw new ArgumentException("Material state path cannot be null or empty.", nameof(stateFilePath));
		}

		var json = File.ReadAllText(stateFilePath);
		var stateFile = JsonSerializer.Deserialize<MaterialAssetStateFile>(json, AssetJson.SerializerOptions)
			?? throw new InvalidOperationException($"Failed to deserialize material state '{stateFilePath}'.");
		if (stateFile.Version != MaterialAssetStateFile.CurrentVersion)
		{
			throw new InvalidOperationException(
				$"Unsupported material state version {stateFile.Version}. Expected {MaterialAssetStateFile.CurrentVersion}.");
		}

		stateFile.Summary ??= new MaterialAssetSummary();
		stateFile.Artifacts ??= new List<AssetArtifactInfo>();
		return stateFile;
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

	public void SaveState(string stateFilePath, MaterialAssetStateFile stateFile)
	{
		if (string.IsNullOrWhiteSpace(stateFilePath))
		{
			throw new ArgumentException("Material state path cannot be null or empty.", nameof(stateFilePath));
		}

		ArgumentNullException.ThrowIfNull(stateFile);
		stateFile.Version = MaterialAssetStateFile.CurrentVersion;
		stateFile.AssetType = AssetType.Material;
		stateFile.Summary ??= new MaterialAssetSummary();
		stateFile.Artifacts ??= new List<AssetArtifactInfo>();
		WriteJsonAtomically(stateFilePath, stateFile);
	}

	public string GetStateRelativePath(Guid assetId)
	{
		return $"Database/{assetId:D}.assetstate.json";
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

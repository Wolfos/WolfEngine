using System.Text.Json;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.Projects;

public sealed class DataAssetLoadResult
{
	public required DataAssetFile File { get; init; }
	public required Type DataAssetType { get; init; }
	public required IDataAsset Asset { get; init; }
}

public interface IDataAssetStore
{
	IDataAsset CreateDefault(Type dataAssetType);
	DataAssetMetaFile CreateMeta(Guid assetId, Type dataAssetType);
	DataAssetLoadResult LoadAsset(string assetFilePath);
	DataAssetMetaFile LoadMeta(string metaFilePath);
	void SaveAsset(string assetFilePath, Type dataAssetType, IDataAsset asset);
	void SaveMeta(string metaFilePath, DataAssetMetaFile metaFile);
}

public sealed class DataAssetStore : IDataAssetStore
{
	public IDataAsset CreateDefault(Type dataAssetType)
	{
		ArgumentNullException.ThrowIfNull(dataAssetType);
		ValidateDataAssetType(dataAssetType);

		return (IDataAsset)(Activator.CreateInstance(dataAssetType)
			?? throw new InvalidOperationException($"Failed to create data asset instance for '{dataAssetType.FullName}'."));
	}

	public DataAssetMetaFile CreateMeta(Guid assetId, Type dataAssetType)
	{
		ArgumentNullException.ThrowIfNull(dataAssetType);
		ValidateDataAssetType(dataAssetType);

		return new DataAssetMetaFile
		{
			AssetId = assetId,
			DataAssetType = GetTypeName(dataAssetType)
		};
	}

	public DataAssetLoadResult LoadAsset(string assetFilePath)
	{
		if (string.IsNullOrWhiteSpace(assetFilePath))
		{
			throw new ArgumentException("Data asset path cannot be null or empty.", nameof(assetFilePath));
		}

		var json = File.ReadAllText(assetFilePath);
		var assetFile = JsonSerializer.Deserialize<DataAssetFile>(json, AssetJson.SerializerOptions)
			?? throw new InvalidOperationException($"Failed to deserialize data asset '{assetFilePath}'.");
		if (assetFile.Version != DataAssetFile.CurrentVersion)
		{
			throw new InvalidOperationException(
				$"Unsupported data asset version {assetFile.Version}. Expected {DataAssetFile.CurrentVersion}.");
		}

		var dataAssetType = ResolveDataAssetType(assetFile.DataAssetType);
		var asset = (IDataAsset?)assetFile.Data.Deserialize(dataAssetType, AssetJson.SerializerOptions)
			?? throw new InvalidOperationException($"Failed to deserialize data payload from '{assetFilePath}'.");

		return new DataAssetLoadResult
		{
			File = assetFile,
			DataAssetType = dataAssetType,
			Asset = asset
		};
	}

	public DataAssetMetaFile LoadMeta(string metaFilePath)
	{
		if (string.IsNullOrWhiteSpace(metaFilePath))
		{
			throw new ArgumentException("Data asset meta path cannot be null or empty.", nameof(metaFilePath));
		}

		var json = File.ReadAllText(metaFilePath);
		var metaFile = JsonSerializer.Deserialize<DataAssetMetaFile>(json, AssetJson.SerializerOptions)
			?? throw new InvalidOperationException($"Failed to deserialize data asset metadata '{metaFilePath}'.");
		if (metaFile.Version != DataAssetMetaFile.CurrentVersion)
		{
			throw new InvalidOperationException(
				$"Unsupported data asset metadata version {metaFile.Version}. Expected {DataAssetMetaFile.CurrentVersion}.");
		}

		return metaFile;
	}

	public void SaveAsset(string assetFilePath, Type dataAssetType, IDataAsset asset)
	{
		if (string.IsNullOrWhiteSpace(assetFilePath))
		{
			throw new ArgumentException("Data asset path cannot be null or empty.", nameof(assetFilePath));
		}

		ArgumentNullException.ThrowIfNull(dataAssetType);
		ArgumentNullException.ThrowIfNull(asset);
		ValidateDataAssetType(dataAssetType);

		var assetFile = new DataAssetFile
		{
			Version = DataAssetFile.CurrentVersion,
			AssetType = AssetType.DataAsset,
			DataAssetType = GetTypeName(dataAssetType),
			Data = JsonSerializer.SerializeToElement(asset, dataAssetType, AssetJson.SerializerOptions)
		};

		WriteJsonAtomically(assetFilePath, assetFile);
	}

	public void SaveMeta(string metaFilePath, DataAssetMetaFile metaFile)
	{
		if (string.IsNullOrWhiteSpace(metaFilePath))
		{
			throw new ArgumentException("Data asset meta path cannot be null or empty.", nameof(metaFilePath));
		}

		ArgumentNullException.ThrowIfNull(metaFile);
		metaFile.Version = DataAssetMetaFile.CurrentVersion;
		metaFile.AssetType = AssetType.DataAsset;
		WriteJsonAtomically(metaFilePath, metaFile);
	}

	private static void ValidateDataAssetType(Type dataAssetType)
	{
		if (typeof(IDataAsset).IsAssignableFrom(dataAssetType) == false ||
		    dataAssetType.IsClass == false ||
		    dataAssetType.IsAbstract ||
		    dataAssetType.IsGenericTypeDefinition ||
		    dataAssetType.ContainsGenericParameters ||
		    dataAssetType.GetConstructor(Type.EmptyTypes) is null)
		{
			throw new InvalidOperationException($"'{dataAssetType.FullName}' is not a valid data asset type.");
		}
	}

	private static Type ResolveDataAssetType(string typeName)
	{
		var type = Type.GetType(typeName, throwOnError: false);
		if (type is null)
		{
			throw new InvalidOperationException($"Failed to resolve data asset type '{typeName}'.");
		}

		ValidateDataAssetType(type);
		return type;
	}

	private static string GetTypeName(Type dataAssetType)
	{
		return dataAssetType.AssemblyQualifiedName
		       ?? throw new InvalidOperationException($"Type '{dataAssetType.FullName}' does not have an assembly-qualified name.");
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

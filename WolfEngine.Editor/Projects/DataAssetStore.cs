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
	DataAssetLoadResult LoadAsset(string assetFilePath);
	void SaveAsset(string assetFilePath, Type dataAssetType, IDataAsset asset);
}

public sealed class DataAssetStore : IDataAssetStore
{
	private readonly IProjectTypeResolver? _typeResolver;

	public DataAssetStore(IProjectTypeResolver? typeResolver = null)
	{
		_typeResolver = typeResolver;
	}

	public IDataAsset CreateDefault(Type dataAssetType)
	{
		ArgumentNullException.ThrowIfNull(dataAssetType);
		ValidateDataAssetType(dataAssetType);

		return (IDataAsset)(Activator.CreateInstance(dataAssetType)
			?? throw new InvalidOperationException($"Failed to create data asset instance for '{dataAssetType.FullName}'."));
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

	private Type ResolveDataAssetType(string typeName)
	{
		if (_typeResolver?.TryResolveType(typeName, out var type) != true &&
		    ProjectTypeResolverUtility.TryResolveFromLoadedAssemblies(typeName, out type) == false)
		{
			throw new InvalidOperationException($"Failed to resolve data asset type '{typeName}'.");
		}

		ValidateDataAssetType(type);
		return type;
	}

	private string GetTypeName(Type dataAssetType)
	{
		return _typeResolver?.GetTypeName(dataAssetType) ?? ProjectTypeResolverUtility.GetTypeName(dataAssetType);
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

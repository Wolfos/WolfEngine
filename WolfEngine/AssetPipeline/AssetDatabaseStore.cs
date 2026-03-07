using System.Text.Json;

namespace WolfEngine.AssetPipeline;

public interface IAssetDatabaseStore
{
	AssetDatabase CreateEmpty();
	AssetDatabase Load(string databaseFilePath);
	void Save(string databaseFilePath, AssetDatabase database);
}

public sealed class AssetDatabaseStore : IAssetDatabaseStore
{
	public AssetDatabase CreateEmpty()
	{
		return new AssetDatabase();
	}

	public AssetDatabase Load(string databaseFilePath)
	{
		if (string.IsNullOrWhiteSpace(databaseFilePath))
		{
			throw new ArgumentException("Database path cannot be null or empty.", nameof(databaseFilePath));
		}

		var json = File.ReadAllText(databaseFilePath);
		var database = JsonSerializer.Deserialize<AssetDatabase>(json, AssetJson.SerializerOptions)
			?? throw new InvalidOperationException($"Failed to deserialize asset database '{databaseFilePath}'.");

		if (database.Version != AssetDatabase.CurrentVersion)
		{
			throw new InvalidOperationException(
				$"Unsupported asset database version {database.Version}. Expected {AssetDatabase.CurrentVersion}.");
		}

		database.Assets ??= new List<AssetDatabaseEntry>();
		return database;
	}

	public void Save(string databaseFilePath, AssetDatabase database)
	{
		if (string.IsNullOrWhiteSpace(databaseFilePath))
		{
			throw new ArgumentException("Database path cannot be null or empty.", nameof(databaseFilePath));
		}

		ArgumentNullException.ThrowIfNull(database);
		database.Version = AssetDatabase.CurrentVersion;
		database.Assets ??= new List<AssetDatabaseEntry>();

		var directory = Path.GetDirectoryName(databaseFilePath);
		if (string.IsNullOrWhiteSpace(directory) == false)
		{
			Directory.CreateDirectory(directory);
		}

		var json = JsonSerializer.Serialize(database, AssetJson.SerializerOptions);
		WriteTextAtomically(databaseFilePath, json);
	}

	private static void WriteTextAtomically(string path, string content)
	{
		var tempPath = path + ".tmp";
		File.WriteAllText(tempPath, content);
		File.Move(tempPath, path, true);
	}
}

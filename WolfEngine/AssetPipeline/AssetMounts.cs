using Microsoft.Data.Sqlite;

namespace WolfEngine.AssetPipeline;

public interface IAssetMount
{
	string Id { get; }
	string DisplayName { get; }
	bool IsReadOnly { get; }
	string RootPath { get; }
	AssetDatabase Database { get; }
	IReadOnlyList<AssetDependencyRecord> Dependencies { get; }
	string GetAbsolutePath(string relativePath);
}

public sealed class DirectoryAssetMount : IAssetMount
{
	public DirectoryAssetMount(
		string id,
		string displayName,
		string rootPath,
		bool isReadOnly,
		AssetDatabase database,
		IReadOnlyList<AssetDependencyRecord>? dependencies = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(id);
		ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
		ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
		Id = id;
		DisplayName = displayName;
		RootPath = Path.GetFullPath(rootPath);
		IsReadOnly = isReadOnly;
		Database = database ?? throw new ArgumentNullException(nameof(database));
		Dependencies = dependencies ?? [];
	}

	public string Id { get; }
	public string DisplayName { get; }
	public bool IsReadOnly { get; }
	public string RootPath { get; }
	public AssetDatabase Database { get; }
	public IReadOnlyList<AssetDependencyRecord> Dependencies { get; }

	public string GetAbsolutePath(string relativePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
		var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
		var absolute = Path.GetFullPath(Path.Combine(RootPath, normalized));
		var relative = Path.GetRelativePath(RootPath, absolute);
		if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
			throw new InvalidOperationException($"Asset path '{relativePath}' escapes mount '{Id}'.");
		return absolute;
	}
}

public readonly record struct MountedAsset(IAssetMount Mount, AssetDatabaseEntry Asset)
{
	public string GetAbsolutePath(string relativePath) => Mount.GetAbsolutePath(relativePath);
}

public interface IAssetCatalog
{
	IReadOnlyList<IAssetMount> Mounts { get; }
	IReadOnlyCollection<MountedAsset> Assets { get; }
	bool TryGetAsset(Guid assetId, out MountedAsset asset);
	MountedAsset GetAsset(Guid assetId);
}

public sealed class AssetCatalog : IAssetCatalog
{
	private readonly Dictionary<Guid, MountedAsset> _assets;

	public AssetCatalog(IEnumerable<IAssetMount> mounts)
	{
		ArgumentNullException.ThrowIfNull(mounts);
		Mounts = mounts.ToArray();
		var duplicateMount = Mounts.GroupBy(mount => mount.Id, StringComparer.Ordinal)
			.FirstOrDefault(group => group.Count() > 1);
		if (duplicateMount is not null)
			throw new InvalidOperationException($"Asset mount ID '{duplicateMount.Key}' is registered more than once.");

		_assets = new Dictionary<Guid, MountedAsset>();
		foreach (var mount in Mounts)
		{
			foreach (var entry in mount.Database.Assets)
			{
				if (_assets.TryGetValue(entry.Id, out var existing))
				{
					throw new InvalidOperationException(
						$"Asset ID '{entry.Id}' exists in mount '{existing.Mount.Id}' at '{existing.Asset.RelativeSourcePath}' " +
						$"and mount '{mount.Id}' at '{entry.RelativeSourcePath}'. Asset mounts cannot override one another.");
				}
				_assets.Add(entry.Id, new MountedAsset(mount, entry));
			}
		}
		foreach (var mount in Mounts.Where(candidate => candidate.IsReadOnly))
		{
			foreach (var dependency in mount.Dependencies)
			{
				if (!_assets.TryGetValue(dependency.FromNodeId, out var owner) || !ReferenceEquals(owner.Mount, mount))
					throw new InvalidOperationException(
						$"Read-only mount '{mount.Id}' declares a dependency from asset '{dependency.FromNodeId}' that it does not own.");
				if (!_assets.TryGetValue(dependency.ToNodeId, out var target))
					throw new InvalidOperationException(
						$"Read-only mount '{mount.Id}' depends on missing asset '{dependency.ToNodeId}'.");
				if (!target.Mount.IsReadOnly)
					throw new InvalidOperationException(
						$"Read-only mount '{mount.Id}' depends on writable mount '{target.Mount.Id}' through asset '{dependency.ToNodeId}'.");
			}
		}
		Assets = _assets.Values.ToArray();
	}

	public IReadOnlyList<IAssetMount> Mounts { get; }
	public IReadOnlyCollection<MountedAsset> Assets { get; }
	public bool TryGetAsset(Guid assetId, out MountedAsset asset) => _assets.TryGetValue(assetId, out asset);
	public MountedAsset GetAsset(Guid assetId) => _assets.TryGetValue(assetId, out var asset)
		? asset
		: throw new KeyNotFoundException($"Asset '{assetId}' was not found in any mounted asset database.");
}

public static class ReadOnlyAssetMountLoader
{
	public const int CurrentContentVersion = 1;
	public const string ManifestFileName = "mount.json";

	public static IAssetMount Load(string rootPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
		var root = Path.GetFullPath(rootPath);
		var manifestPath = Path.Combine(root, ManifestFileName);
		if (!File.Exists(manifestPath))
			throw new FileNotFoundException($"Asset mount manifest '{manifestPath}' is missing.", manifestPath);
		var manifest = System.Text.Json.JsonSerializer.Deserialize<AssetMountManifest>(
			File.ReadAllBytes(manifestPath), AssetJson.SerializerOptions)
			?? throw new InvalidDataException($"Asset mount manifest '{manifestPath}' is invalid.");
		if (manifest.Version != CurrentContentVersion)
			throw new InvalidDataException(
				$"Asset mount '{manifest.Id}' uses content version {manifest.Version}; this engine requires {CurrentContentVersion}.");

		var databasePath = Path.Combine(root, manifest.DatabasePath.Replace('/', Path.DirectorySeparatorChar));
		if (!File.Exists(databasePath))
			throw new FileNotFoundException($"Asset mount database '{databasePath}' is missing.", databasePath);

		var builder = new SqliteConnectionStringBuilder
		{
			DataSource = databasePath,
			Mode = SqliteOpenMode.ReadOnly,
			Cache = SqliteCacheMode.Shared
		};
		using var connection = new SqliteConnection(builder.ToString());
		connection.Open();
		var assets = ReadAssets(connection);
		var dependencies = ReadDependencies(connection);
		var assetIds = assets.Select(asset => asset.Id).ToHashSet();
		var invalidDependency = dependencies.FirstOrDefault(dependency => !assetIds.Contains(dependency.FromNodeId));
		if (invalidDependency is not null)
			throw new InvalidDataException(
				$"Read-only mount '{manifest.Id}' declares a dependency from asset '{invalidDependency.FromNodeId}' that it does not own.");
		return new DirectoryAssetMount(manifest.Id, manifest.DisplayName, root, true,
			new AssetDatabase { Assets = assets }, dependencies);
	}

	private static List<AssetDatabaseEntry> ReadAssets(SqliteConnection connection)
	{
		using var command = connection.CreateCommand();
		command.CommandText = """
			SELECT n.node_id, n.source_id, n.asset_type, n.node_key, n.name, n.is_generated,
			       n.relative_source_path, n.relative_asset_path, n.relative_meta_path, n.summary_json,
			       a.artifact_key, a.kind, a.target, a.relative_path, a.content_hash, a.byte_size,
			       a.chunk_index, a.chunk_count, a.stream_group, a.metadata_json
			FROM asset_nodes n LEFT JOIN artifacts a ON a.node_id = n.node_id
			ORDER BY n.node_id, a.artifact_key;
			""";
		using var reader = command.ExecuteReader();
		var byId = new Dictionary<Guid, AssetDatabaseEntry>();
		while (reader.Read())
		{
			var id = Guid.Parse(reader.GetString(0));
			if (!byId.TryGetValue(id, out var entry))
			{
				entry = new AssetDatabaseEntry
				{
					Id = id,
					SourceId = Guid.Parse(reader.GetString(1)),
					Type = (AssetType)reader.GetInt32(2),
					NodeKey = reader.GetString(3),
					Name = reader.GetString(4),
					IsGenerated = reader.GetInt32(5) != 0,
					RelativeSourcePath = reader.GetString(6),
					RelativeAssetPath = reader.GetString(7),
					RelativeStatePath = reader.GetString(8),
					RelativeMetaPath = reader.GetString(8),
					SummaryJson = reader.GetString(9)
				};
				byId.Add(id, entry);
			}
			if (!reader.IsDBNull(10))
			{
				entry.Artifacts.Add(new AssetArtifactRecord
				{
					NodeId = id,
					ArtifactKey = reader.GetString(10),
					Kind = reader.GetString(11),
					Target = reader.GetString(12),
					RelativePath = reader.GetString(13),
					ContentHash = reader.GetString(14),
					ByteSize = reader.GetInt64(15),
					ChunkIndex = reader.GetInt32(16),
					ChunkCount = reader.GetInt32(17),
					StreamGroup = reader.GetString(18),
					MetadataJson = reader.GetString(19)
				});
			}
		}
		return byId.Values.ToList();
	}

	private static List<AssetDependencyRecord> ReadDependencies(SqliteConnection connection)
	{
		using var command = connection.CreateCommand();
		command.CommandText = "SELECT from_node_id, to_node_id, kind, is_hard FROM dependencies;";
		using var reader = command.ExecuteReader();
		var result = new List<AssetDependencyRecord>();
		while (reader.Read())
		{
			result.Add(new AssetDependencyRecord
			{
				FromNodeId = Guid.Parse(reader.GetString(0)),
				ToNodeId = Guid.Parse(reader.GetString(1)),
				Kind = reader.GetString(2),
				IsHard = reader.GetInt32(3) != 0
			});
		}
		return result;
	}
}

public sealed class AssetMountManifest
{
	public int Version { get; set; } = ReadOnlyAssetMountLoader.CurrentContentVersion;
	public string Id { get; set; } = string.Empty;
	public string DisplayName { get; set; } = string.Empty;
	public string DatabasePath { get; set; } = "Library/AssetPipeline.sqlite";
}

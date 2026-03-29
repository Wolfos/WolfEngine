using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace WolfEngine.AssetPipeline;

public interface IAssetPipelineIndex
{
	void Initialize(string projectRootPath);
	void DeleteSource(string projectRootPath, Guid sourceId);
	void UpdateSource(
		string projectRootPath,
		Guid sourceId,
		string relativeMetaPath,
		string importerId,
		int importerVersion,
		string sourceContentHash,
		long sourceFileSize,
		long sourceLastWriteTimeUtcTicks,
		string importSettingsJson);
	void UpsertSourceGraph(
		string projectRootPath,
		AssetSourceRecord source,
		IReadOnlyList<AssetNodeRecord> nodes,
		IReadOnlyList<AssetArtifactRecord> artifacts,
		IReadOnlyList<AssetDependencyRecord> dependencies);
	IReadOnlyList<AssetSourceRecord> GetSources(string projectRootPath);
	IReadOnlyList<AssetNodeRecord> GetNodes(string projectRootPath);
	IReadOnlyList<AssetArtifactRecord> GetArtifacts(string projectRootPath);
	IReadOnlyList<AssetDependencyRecord> GetDependencies(string projectRootPath);
	bool TryGetNode(string projectRootPath, Guid nodeId, out AssetNodeRecord node);
	bool TryGetSourceByRelativePath(string projectRootPath, string relativeSourcePath, out AssetSourceRecord source);
	IReadOnlyList<AssetArtifactRecord> GetArtifactsForNode(string projectRootPath, Guid nodeId);
	IReadOnlyList<AssetDependencyRecord> GetDependenciesFrom(string projectRootPath, Guid nodeId);
}

public sealed class AssetPipelineIndex : IAssetPipelineIndex
{
	public void Initialize(string projectRootPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRootPath);
		Directory.CreateDirectory(AssetPipelinePaths.GetLibraryPath(projectRootPath));
		Directory.CreateDirectory(AssetPipelinePaths.GetImportedRoot(projectRootPath));
		Directory.CreateDirectory(AssetPipelinePaths.GetArtifactsRoot(projectRootPath));

		using var connection = OpenConnection(projectRootPath);
		using var command = connection.CreateCommand();
		command.CommandText = """
			PRAGMA foreign_keys = ON;

			CREATE TABLE IF NOT EXISTS sources (
				source_id TEXT PRIMARY KEY,
				relative_source_path TEXT NOT NULL UNIQUE,
				relative_meta_path TEXT NOT NULL,
				importer_id TEXT NOT NULL,
				importer_version INTEGER NOT NULL,
				source_content_hash TEXT NOT NULL,
				source_file_size INTEGER NOT NULL,
				source_last_write_time_utc_ticks INTEGER NOT NULL,
				import_settings_json TEXT NOT NULL
			);

			CREATE TABLE IF NOT EXISTS asset_nodes (
				node_id TEXT PRIMARY KEY,
				source_id TEXT NOT NULL,
				asset_type INTEGER NOT NULL,
				node_key TEXT NOT NULL,
				name TEXT NOT NULL,
				is_generated INTEGER NOT NULL,
				relative_source_path TEXT NOT NULL,
				relative_asset_path TEXT NOT NULL,
				relative_meta_path TEXT NOT NULL,
				summary_json TEXT NOT NULL,
				UNIQUE(source_id, node_key),
				FOREIGN KEY(source_id) REFERENCES sources(source_id) ON DELETE CASCADE
			);

			CREATE TABLE IF NOT EXISTS artifacts (
				node_id TEXT NOT NULL,
				artifact_key TEXT NOT NULL,
				kind TEXT NOT NULL,
				target TEXT NOT NULL,
				relative_path TEXT NOT NULL,
				content_hash TEXT NOT NULL,
				byte_size INTEGER NOT NULL,
				chunk_index INTEGER NOT NULL,
				chunk_count INTEGER NOT NULL,
				stream_group TEXT NOT NULL,
				metadata_json TEXT NOT NULL,
				PRIMARY KEY(node_id, artifact_key),
				FOREIGN KEY(node_id) REFERENCES asset_nodes(node_id) ON DELETE CASCADE
			);

			CREATE TABLE IF NOT EXISTS dependencies (
				from_node_id TEXT NOT NULL,
				to_node_id TEXT NOT NULL,
				kind TEXT NOT NULL,
				is_hard INTEGER NOT NULL,
				PRIMARY KEY(from_node_id, to_node_id, kind),
				FOREIGN KEY(from_node_id) REFERENCES asset_nodes(node_id) ON DELETE CASCADE
			);

			CREATE TABLE IF NOT EXISTS source_scan_state (
				relative_source_path TEXT PRIMARY KEY,
				last_seen_utc_ticks INTEGER NOT NULL
			);

			CREATE INDEX IF NOT EXISTS ix_asset_nodes_source_id ON asset_nodes(source_id);
			CREATE INDEX IF NOT EXISTS ix_asset_nodes_type ON asset_nodes(asset_type);
			CREATE INDEX IF NOT EXISTS ix_artifacts_node_id ON artifacts(node_id);
			CREATE INDEX IF NOT EXISTS ix_dependencies_from_node_id ON dependencies(from_node_id);
			CREATE INDEX IF NOT EXISTS ix_dependencies_to_node_id ON dependencies(to_node_id);
			""";
		command.ExecuteNonQuery();
	}

	public void DeleteSource(string projectRootPath, Guid sourceId)
	{
		using var connection = OpenConnection(projectRootPath);
		using var command = connection.CreateCommand();
		command.CommandText = "DELETE FROM sources WHERE source_id = $sourceId;";
		command.Parameters.AddWithValue("$sourceId", sourceId.ToString("D"));
		command.ExecuteNonQuery();
	}

	public void UpdateSource(
		string projectRootPath,
		Guid sourceId,
		string relativeMetaPath,
		string importerId,
		int importerVersion,
		string sourceContentHash,
		long sourceFileSize,
		long sourceLastWriteTimeUtcTicks,
		string importSettingsJson)
	{
		using var connection = OpenConnection(projectRootPath);
		using var command = connection.CreateCommand();
		command.CommandText = """
			UPDATE sources
			SET
				relative_meta_path = $relativeMetaPath,
				importer_id = $importerId,
				importer_version = $importerVersion,
				source_content_hash = $sourceContentHash,
				source_file_size = $sourceFileSize,
				source_last_write_time_utc_ticks = $sourceLastWriteTimeUtcTicks,
				import_settings_json = $importSettingsJson
			WHERE source_id = $sourceId;
			""";
		command.Parameters.AddWithValue("$sourceId", sourceId.ToString("D"));
		command.Parameters.AddWithValue("$relativeMetaPath", relativeMetaPath);
		command.Parameters.AddWithValue("$importerId", importerId);
		command.Parameters.AddWithValue("$importerVersion", importerVersion);
		command.Parameters.AddWithValue("$sourceContentHash", sourceContentHash);
		command.Parameters.AddWithValue("$sourceFileSize", sourceFileSize);
		command.Parameters.AddWithValue("$sourceLastWriteTimeUtcTicks", sourceLastWriteTimeUtcTicks);
		command.Parameters.AddWithValue("$importSettingsJson", importSettingsJson);
		command.ExecuteNonQuery();
	}

	public void UpsertSourceGraph(
		string projectRootPath,
		AssetSourceRecord source,
		IReadOnlyList<AssetNodeRecord> nodes,
		IReadOnlyList<AssetArtifactRecord> artifacts,
		IReadOnlyList<AssetDependencyRecord> dependencies)
	{
		ArgumentNullException.ThrowIfNull(source);
		ArgumentNullException.ThrowIfNull(nodes);
		ArgumentNullException.ThrowIfNull(artifacts);
		ArgumentNullException.ThrowIfNull(dependencies);

		using var connection = OpenConnection(projectRootPath);
		using var transaction = connection.BeginTransaction();

		using (var deleteCommand = connection.CreateCommand())
		{
			deleteCommand.Transaction = transaction;
			deleteCommand.CommandText = "DELETE FROM sources WHERE source_id = $sourceId;";
			deleteCommand.Parameters.AddWithValue("$sourceId", source.SourceId.ToString("D"));
			deleteCommand.ExecuteNonQuery();
		}

		using (var insertSource = connection.CreateCommand())
		{
			insertSource.Transaction = transaction;
			insertSource.CommandText = """
				INSERT INTO sources (
					source_id,
					relative_source_path,
					relative_meta_path,
					importer_id,
					importer_version,
					source_content_hash,
					source_file_size,
					source_last_write_time_utc_ticks,
					import_settings_json)
				VALUES (
					$sourceId,
					$relativeSourcePath,
					$relativeMetaPath,
					$importerId,
					$importerVersion,
					$sourceContentHash,
					$sourceFileSize,
					$sourceLastWriteTimeUtcTicks,
					$importSettingsJson);
				""";
			insertSource.Parameters.AddWithValue("$sourceId", source.SourceId.ToString("D"));
			insertSource.Parameters.AddWithValue("$relativeSourcePath", source.RelativeSourcePath);
			insertSource.Parameters.AddWithValue("$relativeMetaPath", source.RelativeMetaPath);
			insertSource.Parameters.AddWithValue("$importerId", source.ImporterId);
			insertSource.Parameters.AddWithValue("$importerVersion", source.ImporterVersion);
			insertSource.Parameters.AddWithValue("$sourceContentHash", source.SourceContentHash);
			insertSource.Parameters.AddWithValue("$sourceFileSize", source.SourceFileSize);
			insertSource.Parameters.AddWithValue("$sourceLastWriteTimeUtcTicks", source.SourceLastWriteTimeUtcTicks);
			insertSource.Parameters.AddWithValue("$importSettingsJson", source.ImportSettingsJson);
			insertSource.ExecuteNonQuery();
		}

		foreach (var node in nodes)
		{
			using var insertNode = connection.CreateCommand();
			insertNode.Transaction = transaction;
			insertNode.CommandText = """
				INSERT INTO asset_nodes (
					node_id,
					source_id,
					asset_type,
					node_key,
					name,
					is_generated,
					relative_source_path,
					relative_asset_path,
					relative_meta_path,
					summary_json)
				VALUES (
					$nodeId,
					$sourceId,
					$assetType,
					$nodeKey,
					$name,
					$isGenerated,
					$relativeSourcePath,
					$relativeAssetPath,
					$relativeMetaPath,
					$summaryJson);
				""";
			insertNode.Parameters.AddWithValue("$nodeId", node.NodeId.ToString("D"));
			insertNode.Parameters.AddWithValue("$sourceId", node.SourceId.ToString("D"));
			insertNode.Parameters.AddWithValue("$assetType", (int)node.Type);
			insertNode.Parameters.AddWithValue("$nodeKey", node.NodeKey);
			insertNode.Parameters.AddWithValue("$name", node.Name);
			insertNode.Parameters.AddWithValue("$isGenerated", node.IsGenerated ? 1 : 0);
			insertNode.Parameters.AddWithValue("$relativeSourcePath", node.RelativeSourcePath);
			insertNode.Parameters.AddWithValue("$relativeAssetPath", node.RelativeAssetPath);
			insertNode.Parameters.AddWithValue("$relativeMetaPath", node.RelativeMetaPath);
			insertNode.Parameters.AddWithValue("$summaryJson", node.SummaryJson);
			insertNode.ExecuteNonQuery();
		}

		foreach (var artifact in artifacts)
		{
			using var insertArtifact = connection.CreateCommand();
			insertArtifact.Transaction = transaction;
			insertArtifact.CommandText = """
				INSERT INTO artifacts (
					node_id,
					artifact_key,
					kind,
					target,
					relative_path,
					content_hash,
					byte_size,
					chunk_index,
					chunk_count,
					stream_group,
					metadata_json)
				VALUES (
					$nodeId,
					$artifactKey,
					$kind,
					$target,
					$relativePath,
					$contentHash,
					$byteSize,
					$chunkIndex,
					$chunkCount,
					$streamGroup,
					$metadataJson);
				""";
			insertArtifact.Parameters.AddWithValue("$nodeId", artifact.NodeId.ToString("D"));
			insertArtifact.Parameters.AddWithValue("$artifactKey", artifact.ArtifactKey);
			insertArtifact.Parameters.AddWithValue("$kind", artifact.Kind);
			insertArtifact.Parameters.AddWithValue("$target", artifact.Target);
			insertArtifact.Parameters.AddWithValue("$relativePath", artifact.RelativePath);
			insertArtifact.Parameters.AddWithValue("$contentHash", artifact.ContentHash);
			insertArtifact.Parameters.AddWithValue("$byteSize", artifact.ByteSize);
			insertArtifact.Parameters.AddWithValue("$chunkIndex", artifact.ChunkIndex);
			insertArtifact.Parameters.AddWithValue("$chunkCount", artifact.ChunkCount);
			insertArtifact.Parameters.AddWithValue("$streamGroup", artifact.StreamGroup);
			insertArtifact.Parameters.AddWithValue("$metadataJson", artifact.MetadataJson);
			insertArtifact.ExecuteNonQuery();
		}

		var emittedDependencies = new HashSet<(Guid FromNodeId, Guid ToNodeId, string Kind)>();
		foreach (var dependency in dependencies)
		{
			if (emittedDependencies.Add((dependency.FromNodeId, dependency.ToNodeId, dependency.Kind)) == false)
			{
				continue;
			}

			using var insertDependency = connection.CreateCommand();
			insertDependency.Transaction = transaction;
			insertDependency.CommandText = """
				INSERT INTO dependencies (
					from_node_id,
					to_node_id,
					kind,
					is_hard)
				VALUES (
					$fromNodeId,
					$toNodeId,
					$kind,
					$isHard);
				""";
			insertDependency.Parameters.AddWithValue("$fromNodeId", dependency.FromNodeId.ToString("D"));
			insertDependency.Parameters.AddWithValue("$toNodeId", dependency.ToNodeId.ToString("D"));
			insertDependency.Parameters.AddWithValue("$kind", dependency.Kind);
			insertDependency.Parameters.AddWithValue("$isHard", dependency.IsHard ? 1 : 0);
			insertDependency.ExecuteNonQuery();
		}

		transaction.Commit();
	}

	public IReadOnlyList<AssetSourceRecord> GetSources(string projectRootPath)
	{
		using var connection = OpenConnection(projectRootPath);
		using var command = connection.CreateCommand();
		command.CommandText = """
			SELECT
				source_id,
				relative_source_path,
				relative_meta_path,
				importer_id,
				importer_version,
				source_content_hash,
				source_file_size,
				source_last_write_time_utc_ticks,
				import_settings_json
			FROM sources
			ORDER BY relative_source_path;
			""";
		using var reader = command.ExecuteReader();
		var results = new List<AssetSourceRecord>();
		while (reader.Read())
		{
			results.Add(new AssetSourceRecord
			{
				SourceId = Guid.Parse(reader.GetString(0)),
				RelativeSourcePath = reader.GetString(1),
				RelativeMetaPath = reader.GetString(2),
				ImporterId = reader.GetString(3),
				ImporterVersion = reader.GetInt32(4),
				SourceContentHash = reader.GetString(5),
				SourceFileSize = reader.GetInt64(6),
				SourceLastWriteTimeUtcTicks = reader.GetInt64(7),
				ImportSettingsJson = reader.GetString(8)
			});
		}

		return results;
	}

	public IReadOnlyList<AssetNodeRecord> GetNodes(string projectRootPath)
	{
		using var connection = OpenConnection(projectRootPath);
		using var command = connection.CreateCommand();
		command.CommandText = """
			SELECT
				node_id,
				source_id,
				asset_type,
				node_key,
				name,
				is_generated,
				relative_source_path,
				relative_asset_path,
				relative_meta_path,
				summary_json
			FROM asset_nodes
			ORDER BY name, node_id;
			""";
		return ReadNodes(command);
	}

	public IReadOnlyList<AssetArtifactRecord> GetArtifacts(string projectRootPath)
	{
		using var connection = OpenConnection(projectRootPath);
		using var command = connection.CreateCommand();
		command.CommandText = """
			SELECT
				node_id,
				artifact_key,
				kind,
				target,
				relative_path,
				content_hash,
				byte_size,
				chunk_index,
				chunk_count,
				stream_group,
				metadata_json
			FROM artifacts;
			""";
		return ReadArtifacts(command);
	}

	public IReadOnlyList<AssetDependencyRecord> GetDependencies(string projectRootPath)
	{
		using var connection = OpenConnection(projectRootPath);
		using var command = connection.CreateCommand();
		command.CommandText = """
			SELECT
				from_node_id,
				to_node_id,
				kind,
				is_hard
			FROM dependencies;
			""";
		return ReadDependencies(command);
	}

	public bool TryGetNode(string projectRootPath, Guid nodeId, out AssetNodeRecord node)
	{
		using var connection = OpenConnection(projectRootPath);
		using var command = connection.CreateCommand();
		command.CommandText = """
			SELECT
				node_id,
				source_id,
				asset_type,
				node_key,
				name,
				is_generated,
				relative_source_path,
				relative_asset_path,
				relative_meta_path,
				summary_json
			FROM asset_nodes
			WHERE node_id = $nodeId
			LIMIT 1;
			""";
		command.Parameters.AddWithValue("$nodeId", nodeId.ToString("D"));
		var nodes = ReadNodes(command);
		if (nodes.Count == 0)
		{
			node = null!;
			return false;
		}

		node = nodes[0];
		return true;
	}

	public bool TryGetSourceByRelativePath(string projectRootPath, string relativeSourcePath, out AssetSourceRecord source)
	{
		using var connection = OpenConnection(projectRootPath);
		using var command = connection.CreateCommand();
		command.CommandText = """
			SELECT
				source_id,
				relative_source_path,
				relative_meta_path,
				importer_id,
				importer_version,
				source_content_hash,
				source_file_size,
				source_last_write_time_utc_ticks,
				import_settings_json
			FROM sources
			WHERE relative_source_path = $relativeSourcePath
			LIMIT 1;
			""";
		command.Parameters.AddWithValue("$relativeSourcePath", relativeSourcePath);
		using var reader = command.ExecuteReader();
		if (reader.Read() == false)
		{
			source = null!;
			return false;
		}

		source = new AssetSourceRecord
		{
			SourceId = Guid.Parse(reader.GetString(0)),
			RelativeSourcePath = reader.GetString(1),
			RelativeMetaPath = reader.GetString(2),
			ImporterId = reader.GetString(3),
			ImporterVersion = reader.GetInt32(4),
			SourceContentHash = reader.GetString(5),
			SourceFileSize = reader.GetInt64(6),
			SourceLastWriteTimeUtcTicks = reader.GetInt64(7),
			ImportSettingsJson = reader.GetString(8)
		};
		return true;
	}

	public IReadOnlyList<AssetArtifactRecord> GetArtifactsForNode(string projectRootPath, Guid nodeId)
	{
		using var connection = OpenConnection(projectRootPath);
		using var command = connection.CreateCommand();
		command.CommandText = """
			SELECT
				node_id,
				artifact_key,
				kind,
				target,
				relative_path,
				content_hash,
				byte_size,
				chunk_index,
				chunk_count,
				stream_group,
				metadata_json
			FROM artifacts
			WHERE node_id = $nodeId;
			""";
		command.Parameters.AddWithValue("$nodeId", nodeId.ToString("D"));
		return ReadArtifacts(command);
	}

	public IReadOnlyList<AssetDependencyRecord> GetDependenciesFrom(string projectRootPath, Guid nodeId)
	{
		using var connection = OpenConnection(projectRootPath);
		using var command = connection.CreateCommand();
		command.CommandText = """
			SELECT
				from_node_id,
				to_node_id,
				kind,
				is_hard
			FROM dependencies
			WHERE from_node_id = $fromNodeId;
			""";
		command.Parameters.AddWithValue("$fromNodeId", nodeId.ToString("D"));
		return ReadDependencies(command);
	}

	private static SqliteConnection OpenConnection(string projectRootPath)
	{
		var connection = new SqliteConnection($"Data Source={AssetPipelinePaths.GetSqlitePath(projectRootPath)}");
		connection.Open();
		return connection;
	}

	private static List<AssetNodeRecord> ReadNodes(SqliteCommand command)
	{
		using var reader = command.ExecuteReader();
		var results = new List<AssetNodeRecord>();
		while (reader.Read())
		{
			results.Add(new AssetNodeRecord
			{
				NodeId = Guid.Parse(reader.GetString(0)),
				SourceId = Guid.Parse(reader.GetString(1)),
				Type = (AssetType)reader.GetInt32(2),
				NodeKey = reader.GetString(3),
				Name = reader.GetString(4),
				IsGenerated = reader.GetInt32(5) != 0,
				RelativeSourcePath = reader.GetString(6),
				RelativeAssetPath = reader.GetString(7),
				RelativeMetaPath = reader.GetString(8),
				SummaryJson = reader.GetString(9)
			});
		}

		return results;
	}

	private static List<AssetArtifactRecord> ReadArtifacts(SqliteCommand command)
	{
		using var reader = command.ExecuteReader();
		var results = new List<AssetArtifactRecord>();
		while (reader.Read())
		{
			results.Add(new AssetArtifactRecord
			{
				NodeId = Guid.Parse(reader.GetString(0)),
				ArtifactKey = reader.GetString(1),
				Kind = reader.GetString(2),
				Target = reader.GetString(3),
				RelativePath = reader.GetString(4),
				ContentHash = reader.GetString(5),
				ByteSize = reader.GetInt64(6),
				ChunkIndex = reader.GetInt32(7),
				ChunkCount = reader.GetInt32(8),
				StreamGroup = reader.GetString(9),
				MetadataJson = reader.GetString(10)
			});
		}

		return results;
	}

	private static List<AssetDependencyRecord> ReadDependencies(SqliteCommand command)
	{
		using var reader = command.ExecuteReader();
		var results = new List<AssetDependencyRecord>();
		while (reader.Read())
		{
			results.Add(new AssetDependencyRecord
			{
				FromNodeId = Guid.Parse(reader.GetString(0)),
				ToNodeId = Guid.Parse(reader.GetString(1)),
				Kind = reader.GetString(2),
				IsHard = reader.GetInt32(3) != 0
			});
		}

		return results;
	}
}

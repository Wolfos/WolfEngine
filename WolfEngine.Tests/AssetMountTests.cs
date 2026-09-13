using System.Text.Json;
using NUnit.Framework;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Tests;

public sealed class AssetMountTests
{
	[Test]
	public void CatalogResolvesAssetsFromTheirOwningMount()
	{
		var projectId = Guid.NewGuid();
		var engineId = Guid.NewGuid();
		var project = CreateMount("project", false, projectId);
		var engine = CreateMount("engine", true, engineId);
		var catalog = new AssetCatalog([project, engine]);

		Assert.That(catalog.GetAsset(projectId).Mount, Is.SameAs(project));
		Assert.That(catalog.GetAsset(engineId).Mount, Is.SameAs(engine));
		Assert.That(catalog.GetAsset(engineId).Asset.Id, Is.EqualTo(engineId));
	}

	[Test]
	public void CatalogRejectsDuplicateAssetIdsWithoutMountPrecedence()
	{
		var duplicate = Guid.NewGuid();
		var exception = Assert.Throws<InvalidOperationException>(() =>
			_ = new AssetCatalog([
				CreateMount("project", false, duplicate),
				CreateMount("engine", true, duplicate)
			]));

		Assert.That(exception!.Message, Does.Contain("project"));
		Assert.That(exception.Message, Does.Contain("engine"));
	}

	[Test]
	public void DirectoryMountRejectsPathsOutsideItsRoot()
	{
		var mount = CreateMount("engine", true, Guid.NewGuid());
		Assert.Throws<InvalidOperationException>(() => mount.GetAbsolutePath("../outside.bin"));
	}

	[Test]
	public void ReadOnlyMountCanDependOnAnotherReadOnlyLibrary()
	{
		var engineId = Guid.NewGuid();
		var libraryId = Guid.NewGuid();
		var engine = CreateMount("engine", true, engineId,
			[new AssetDependencyRecord { FromNodeId = engineId, ToNodeId = libraryId, IsHard = true }]);
		var library = CreateMount("engine-library", true, libraryId);

		Assert.DoesNotThrow(() => _ = new AssetCatalog([engine, library]));
	}

	[Test]
	public void ReadOnlyMountCannotDependOnConsumingProject()
	{
		var engineId = Guid.NewGuid();
		var projectId = Guid.NewGuid();
		var engine = CreateMount("engine", true, engineId,
			[new AssetDependencyRecord { FromNodeId = engineId, ToNodeId = projectId, IsHard = true }]);
		var project = CreateMount("project", false, projectId);

		var exception = Assert.Throws<InvalidOperationException>(() => _ = new AssetCatalog([project, engine]));
		Assert.That(exception!.Message, Does.Contain("writable mount 'project'"));
	}

	[Test]
	public void CatalogDoesNotSpecialCaseAssetTypes()
	{
		var entries = Enum.GetValues<AssetType>()
			.Select(type => new AssetDatabaseEntry
			{
				Id = Guid.NewGuid(),
				SourceId = Guid.NewGuid(),
				Type = type
			}).ToArray();
		var mount = new DirectoryAssetMount("engine", "Engine", Path.GetTempPath(), true,
			new AssetDatabase { Assets = [..entries] });
		var catalog = new AssetCatalog([mount]);

		Assert.That(entries.All(entry => catalog.GetAsset(entry.Id).Asset.Type == entry.Type), Is.True);
	}

	[Test]
	public void ReadOnlyLoaderOpensExistingIndexWithoutChangingIt()
	{
		var root = Path.Combine(Path.GetTempPath(), $"wolf-mount-{Guid.NewGuid():N}");
		try
		{
			Directory.CreateDirectory(root);
			new AssetPipelineIndex().Initialize(root);
			var databasePath = AssetPipelinePaths.GetSqlitePath(root);
			var timestamp = File.GetLastWriteTimeUtc(databasePath);
			File.WriteAllBytes(Path.Combine(root, ReadOnlyAssetMountLoader.ManifestFileName),
				JsonSerializer.SerializeToUtf8Bytes(new AssetMountManifest
				{
					Id = "engine",
					DisplayName = "Engine"
				}, AssetJson.SerializerOptions));

			var mount = ReadOnlyAssetMountLoader.Load(root);

			Assert.Multiple(() =>
			{
				Assert.That(mount.IsReadOnly, Is.True);
				Assert.That(mount.Database.Assets, Is.Empty);
				Assert.That(File.GetLastWriteTimeUtc(databasePath), Is.EqualTo(timestamp));
			});
		}
		finally
		{
			if (Directory.Exists(root)) Directory.Delete(root, true);
		}
	}

	private static DirectoryAssetMount CreateMount(
		string id,
		bool readOnly,
		Guid assetId,
		IReadOnlyList<AssetDependencyRecord>? dependencies = null)
	{
		return new DirectoryAssetMount(id, char.ToUpperInvariant(id[0]) + id[1..], Path.GetTempPath(), readOnly,
			new AssetDatabase
			{
				Assets = [new AssetDatabaseEntry { Id = assetId, SourceId = Guid.NewGuid(), Type = AssetType.Mesh }]
			}, dependencies);
	}
}

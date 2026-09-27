using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;

namespace WolfEngine.Editor.Tests;

public sealed class MountedAssetResolutionTests
{
	[Test]
	public void NewlyRegisteredRuntimeAssetTypeResolvesThroughOwningMountWithoutMountChanges()
	{
		var root = Path.Combine(Path.GetTempPath(), $"wolf-mounted-runtime-{Guid.NewGuid():N}");
		Directory.CreateDirectory(root);
		try
		{
			var id = Guid.NewGuid();
			var entry = new AssetDatabaseEntry
			{
				Id = id,
				SourceId = Guid.NewGuid(),
				Type = AssetType.DataAsset,
				RelativeAssetPath = "Assets/test.asset"
			};
			var mount = new DirectoryAssetMount("engine", "Engine", root, true,
				new AssetDatabase { Assets = [entry] });
			var services = new ServiceCollection()
				.AddSingleton<TestMountedAssetResolver>()
				.BuildServiceProvider();
			var registry = new EditorAssetInstanceRegistry(services);
			registry.RefreshCatalog(new AssetCatalog([mount]));

			var resolved = (TestMountedAsset?)registry.GetInstance(id, typeof(TestMountedAsset));

			Assert.That(resolved, Is.Not.Null);
			Assert.That(resolved!.Path, Is.EqualTo(mount.GetAbsolutePath(entry.RelativeAssetPath)));
		}
		finally
		{
			if (Directory.Exists(root)) Directory.Delete(root, true);
		}
	}

	[Test]
	public void CatalogRefreshPreservesOnlyRequestedInstanceAndStillInvalidatesChangedIdentity()
	{
		var preservedId = Guid.NewGuid();
		var otherId = Guid.NewGuid();
		using var services = new ServiceCollection().AddSingleton<TestMountedAssetResolver>().BuildServiceProvider();
		var registry = new EditorAssetInstanceRegistry(services);
		IAssetCatalog Catalog(string summary, string preservedPath = "Assets/preserved.asset") => new AssetCatalog([
			new DirectoryAssetMount("project", "Project", Path.GetTempPath(), false, new AssetDatabase
			{
				Assets = [
					new AssetDatabaseEntry { Id = preservedId, Type = AssetType.DataAsset, RelativeAssetPath = preservedPath, SummaryJson = summary },
					new AssetDatabaseEntry { Id = otherId, Type = AssetType.DataAsset, RelativeAssetPath = "Assets/other.asset", SummaryJson = summary }
				]
			})
		]);
		registry.RefreshCatalog(Catalog("before"));
		var preserved = registry.GetInstance(preservedId, typeof(TestMountedAsset));
		var other = registry.GetInstance(otherId, typeof(TestMountedAsset));
		registry.RefreshCatalog(Catalog("after"), preservedId);
		Assert.That(registry.GetInstance(preservedId, typeof(TestMountedAsset)), Is.SameAs(preserved));
		Assert.That(registry.GetInstance(otherId, typeof(TestMountedAsset)), Is.Not.SameAs(other));

		registry.RefreshCatalog(Catalog("after", "Assets/renamed.asset"), preservedId);
		var renamed = (TestMountedAsset)registry.GetInstance(preservedId, typeof(TestMountedAsset))!;
		Assert.That(renamed, Is.Not.SameAs(preserved));
		Assert.That(renamed.Path, Is.EqualTo(Path.Combine(Path.GetTempPath(), "Assets/renamed.asset")));
		registry.RefreshCatalog(new AssetCatalog([]), preservedId);
		Assert.That(registry.GetInstance(preservedId, typeof(TestMountedAsset)), Is.Null);
	}

	[RuntimeAsset(AssetType.DataAsset, typeof(object), typeof(TestMountedAssetResolver))]
	private sealed class TestMountedAsset
	{
		public required string Path { get; init; }
	}

	private sealed class TestMountedAssetResolver : IRuntimeAssetResolver
	{
		public object Resolve(RuntimeAssetResolveContext context) =>
			new TestMountedAsset { Path = context.GetAbsolutePath(context.Asset.RelativeAssetPath) };
	}
}

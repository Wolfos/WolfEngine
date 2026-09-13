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

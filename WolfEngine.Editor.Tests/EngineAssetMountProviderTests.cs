using NSubstitute;
using NUnit.Framework;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;

namespace WolfEngine.Editor.Tests;

public sealed class EngineAssetMountProviderTests
{
	[Test]
	public void BuildsSourcesIntoTargetSpecificCacheAndReusesPreparedMount()
	{
		var testRoot = Path.Combine(Path.GetTempPath(), $"wolf-engine-mount-{Guid.NewGuid():N}");
		var engineRoot = Path.Combine(testRoot, "engine");
		var sourceRoot = Path.Combine(engineRoot, "BuiltInContent", "Assets");
		var cacheRoot = Path.Combine(testRoot, "cache");
		Directory.CreateDirectory(sourceRoot);
		var sourcePath = Path.Combine(sourceRoot, "BuiltIn.asset");
		File.WriteAllText(sourcePath, "first");

		try
		{
			var pipeline = Substitute.For<IProjectAssetPipelineService>();
			pipeline.RebuildProject(Arg.Any<string>()).Returns(call =>
			{
				var outputRoot = call.Arg<string>();
				new AssetPipelineIndex().Initialize(outputRoot);
				return new AssetDatabase();
			});
			var target = Substitute.For<IRuntimeArtifactTargetProvider>();
			target.CurrentTarget.Returns("test-target");
			var provider = new EngineAssetMountProvider(engineRoot, pipeline, target, cacheRoot);

			var first = provider.GetMounts().Single();
			var second = provider.GetMounts().Single();

			Assert.Multiple(() =>
			{
				Assert.That(first.RootPath, Is.EqualTo(second.RootPath));
				Assert.That(first.IsReadOnly, Is.True);
				Assert.That(first.RootPath, Does.StartWith(cacheRoot));
				Assert.That(File.ReadAllText(Path.Combine(first.RootPath, "Assets", "BuiltIn.asset")), Is.EqualTo("first"));
				Assert.That(File.Exists(Path.Combine(first.RootPath, "Library", "AssetPipeline.sqlite")), Is.True);
				Assert.That(
					Directory.EnumerateDirectories(cacheRoot).Select(Path.GetFileName),
					Is.EqualTo(new[] { Path.GetFileName(first.RootPath) }),
					"Publishing left a staging directory behind.");
			});
			pipeline.Received(1).RebuildProject(Arg.Any<string>());

			File.WriteAllText(sourcePath, "second");
			var changed = provider.GetMounts().Single();

			Assert.That(changed.RootPath, Is.Not.EqualTo(first.RootPath));
			Assert.That(File.ReadAllText(Path.Combine(changed.RootPath, "Assets", "BuiltIn.asset")), Is.EqualTo("second"));
			pipeline.Received(2).RebuildProject(Arg.Any<string>());

			Assert.DoesNotThrow(() => Directory.Delete(cacheRoot, recursive: true));
		}
		finally
		{
			if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
		}
	}
}

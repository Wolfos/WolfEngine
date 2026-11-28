using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class BuiltInEngineAssetsTests
{
	[Test]
	public void BuiltInEngineAssets_MatchCommittedMetadata()
	{
		var assetsRoot = Path.Combine(AppContext.BaseDirectory, "BuiltInContent", "Assets");

		AssertNodeId(assetsRoot, "Materials/Default.mat.json", "main", BuiltInEngineAssets.DefaultMaterial.NodeId);
		AssertNodeId(assetsRoot, "Primitives/Cube.obj", "mesh:root-0-Cube:0:Cube", BuiltInEngineAssets.CubeMesh.NodeId);
		AssertNodeId(assetsRoot, "Primitives/Sphere.obj", "mesh:root-0-Sphere:0:Sphere", BuiltInEngineAssets.SphereMesh.NodeId);
	}

	private static void AssertNodeId(string assetsRoot, string relativeSourcePath, string subAssetKey, Guid expectedNodeId)
	{
		var metadata = new AssetMetadataStore().Load(AssetFileExtensions.GetMetaPath(Path.Combine(assetsRoot, relativeSourcePath)));
		var subAsset = metadata.SubAssets.SingleOrDefault(entry => entry.Key == subAssetKey);
		Assert.That(subAsset, Is.Not.Null, $"'{relativeSourcePath}' has no sub-asset '{subAssetKey}'.");
		Assert.That(subAsset!.NodeId, Is.EqualTo(expectedNodeId), relativeSourcePath);
	}
}

using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.Tests;

public sealed class TerrainLayerSetPersistenceTests
{
	[Test]
	public void DataAssetStore_RoundTripsTerrainLayerSetTextureRefs()
	{
		var store = new DataAssetStore();
		var assetPath = Path.Combine(Path.GetTempPath(), "WolfEngineTerrainTests", Guid.NewGuid().ToString("N"), $"TerrainLayerSet{DataAssetFile.FileExtension}");
		var albedoId = Guid.NewGuid();
		var normalId = Guid.NewGuid();
		var heightId = Guid.NewGuid();
		var layerSet = new TerrainLayerSet
		{
			ActiveLayerCount = 2,
			HeightBlendSharpness = 6.5f,
			Layer0 = new TerrainLayerDefinition
			{
				Scale = 12.0f,
				Albedo = new AssetRef<Texture> { NodeId = albedoId },
				Normal = new AssetRef<Texture> { NodeId = normalId }
			},
			Layer1 = new TerrainLayerDefinition
			{
				Scale = 24.0f,
				Height = new AssetRef<Texture> { NodeId = heightId }
			}
		};

		store.SaveAsset(assetPath, typeof(TerrainLayerSet), layerSet);
		var loadResult = store.LoadAsset(assetPath);
		var loaded = (TerrainLayerSet)loadResult.Asset;

		Assert.That(loadResult.DataAssetType, Is.EqualTo(typeof(TerrainLayerSet)));
		Assert.That(loaded.ActiveLayerCount, Is.EqualTo(2));
		Assert.That(loaded.HeightBlendSharpness, Is.EqualTo(6.5f).Within(0.0001f));
		Assert.That(loaded.Layer0.Scale, Is.EqualTo(12.0f).Within(0.0001f));
		Assert.That(loaded.Layer1.Scale, Is.EqualTo(24.0f).Within(0.0001f));
		Assert.That(loaded.Layer0.Albedo.NodeId, Is.EqualTo(albedoId));
		Assert.That(loaded.Layer0.Normal.NodeId, Is.EqualTo(normalId));
		Assert.That(loaded.Layer1.Height.NodeId, Is.EqualTo(heightId));
	}
}

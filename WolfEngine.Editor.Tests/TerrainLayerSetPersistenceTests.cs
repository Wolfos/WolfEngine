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
			ActiveLayerCount = 5,
			HeightBlendSharpness = 6.5f,
			Layer0 = new TerrainLayerDefinition
			{
				Name = "Grass",
				Scale = 12.0f,
				AutoMaterial = true,
				Albedo = new AssetRef<Texture> { NodeId = albedoId },
				Normal = new AssetRef<Texture> { NodeId = normalId }
			},
			Layer1 = new TerrainLayerDefinition
			{
				Scale = 24.0f,
				AutoMaterial = true,
				UseMinimumSlope = true,
				MinimumSlopeDegrees = 42.0f,
				Height = new AssetRef<Texture> { NodeId = heightId }
			}
		};
		layerSet.EnsureLayerCapacity(5);
		layerSet.Layers[4] = new TerrainLayerDefinition
		{
			Name = "Snow",
			AutoMaterial = true,
			Albedo = new AssetRef<Texture> { NodeId = Guid.NewGuid() }
		};

		store.SaveAsset(assetPath, typeof(TerrainLayerSet), layerSet);
		var loadResult = store.LoadAsset(assetPath);
		var loaded = (TerrainLayerSet)loadResult.Asset;

		Assert.That(loadResult.DataAssetType, Is.EqualTo(typeof(TerrainLayerSet)));
		Assert.That(loaded.ActiveLayerCount, Is.EqualTo(5));
		Assert.That(loaded.HeightBlendSharpness, Is.EqualTo(6.5f).Within(0.0001f));
		Assert.That(loaded.Layer0.Scale, Is.EqualTo(12.0f).Within(0.0001f));
		Assert.That(loaded.Layer0.Name, Is.EqualTo("Grass"));
		Assert.That(loaded.Layer1.Scale, Is.EqualTo(24.0f).Within(0.0001f));
		Assert.That(loaded.Layer0.AutoMaterial, Is.True);
		Assert.That(loaded.Layer1.AutoMaterial, Is.True);
		Assert.That(loaded.Layer1.UseMinimumSlope, Is.True);
		Assert.That(loaded.Layer1.MinimumSlopeDegrees, Is.EqualTo(42.0f).Within(0.0001f));
		Assert.That(loaded.Layer0.Albedo.NodeId, Is.EqualTo(albedoId));
		Assert.That(loaded.Layer0.Normal.NodeId, Is.EqualTo(normalId));
		Assert.That(loaded.Layer1.Height.NodeId, Is.EqualTo(heightId));
		Assert.That(loaded.GetLayer(4).Name, Is.EqualTo("Snow"));
		Assert.That(loaded.GetLayer(4).AutoMaterial, Is.True);
		Assert.That(loaded.GetLayer(4).Albedo.NodeId, Is.EqualTo(layerSet.Layers[4].Albedo.NodeId));
	}
}

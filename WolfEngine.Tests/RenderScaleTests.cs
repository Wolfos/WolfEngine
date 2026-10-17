using System.Text.Json;
using WolfEngine.AssetPipeline;
using WolfEngine.Mathematics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class RenderScaleTests
{
	[Test]
	public void RenderScaleDefaultsToFullResolutionAndStaysInRange()
	{
		var config = new RenderConfig();

		Assert.That(config.RenderScale, Is.EqualTo(1.0f));

		config.RenderScale = 2.0f;
		Assert.That(config.RenderScale, Is.EqualTo(1.0f));

		config.RenderScale = 0.0f;
		Assert.That(config.RenderScale, Is.EqualTo(RenderConfig.MinRenderScale));

		config.RenderScale = float.NaN;
		Assert.That(config.RenderScale, Is.EqualTo(1.0f));
	}

	[Test]
	public void RenderScaleRoundTripsThroughAssetJsonAndCookedData()
	{
		var config = new RenderConfig
		{
			RenderScale = 0.67f
		};

		var json = JsonSerializer.Serialize(config, AssetJson.SerializerOptions);
		var fromJson = JsonSerializer.Deserialize<RenderConfig>(json, AssetJson.SerializerOptions)!;
		var fromCooked = (RenderConfig)CookedDataAsset.Read(CookedDataAsset.Write(config), typeof(RenderConfig));

		Assert.That(fromJson.RenderScale, Is.EqualTo(0.67f));
		Assert.That(fromCooked.RenderScale, Is.EqualTo(0.67f));
	}

	[Test]
	public void RenderSizeScalesEachAxisAndNeverReachesZero()
	{
		Assert.That(RenderGraph.ScaleRenderSize(new Int2(2622, 1206), 1.0f), Is.EqualTo(new Int2(2622, 1206)));
		Assert.That(RenderGraph.ScaleRenderSize(new Int2(2622, 1206), 0.5f), Is.EqualTo(new Int2(1311, 603)));
		Assert.That(RenderGraph.ScaleRenderSize(new Int2(1, 1), RenderConfig.MinRenderScale), Is.EqualTo(new Int2(1, 1)));
	}
}

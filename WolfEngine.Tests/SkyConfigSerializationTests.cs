using System.Text.Json;
using WolfEngine.AssetPipeline;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class SkyConfigSerializationTests
{
	private static readonly Guid HdriNodeId = new("4f3e1c2a-9b8d-4e7f-a6c5-0123456789ab");

	[Test]
	public void EditorJson_RoundTripsEverySkyMode()
	{
		var options = AssetJson.GetSerializerOptions(typeof(RenderConfig));
		var json = JsonSerializer.Serialize(CreateConfig(), options);
		var copy = JsonSerializer.Deserialize<RenderConfig>(json, options)!;

		AssertSkyMatches(copy.Sky);
	}

	[Test]
	public void CookedData_RoundTripsEverySkyMode()
	{
		var bytes = CookedDataAsset.Write(CreateConfig());
		var copy = (RenderConfig)CookedDataAsset.Read(bytes, typeof(RenderConfig));

		AssertSkyMatches(copy.Sky);
	}

	[Test]
	public void EditorJson_WithoutSky_LoadsTheDefaultProceduralSky()
	{
		var options = AssetJson.GetSerializerOptions(typeof(RenderConfig));
		var copy = JsonSerializer.Deserialize<RenderConfig>("{}", options)!;

		Assert.Multiple(() =>
		{
			Assert.That(copy.Sky.Mode, Is.EqualTo(SkyConfigMode.Procedural));
			Assert.That(copy.Sky.Procedural.GroundRadiusKm, Is.EqualTo(new ProceduralSkyConfig().GroundRadiusKm));
			Assert.That(copy.Sky.Hdri.Texture.IsValid, Is.False);
		});
	}

	private static RenderConfig CreateConfig()
	{
		return new RenderConfig
		{
			Sky = new SkyConfig
			{
				Mode = SkyConfigMode.Hdri,
				LightingQuality = SkyLightingQuality.High,
				Procedural = new ProceduralSkyConfig
				{
					MieAnisotropy = 0.7f,
					OzoneTipAltitudeKm = 22.0f
				},
				Hdri = new HdriSkyConfig
				{
					Texture = new AssetRef<Texture> { NodeId = HdriNodeId },
					RotationDegrees = 135.0f,
					Intensity = 0.5f
				},
				Gradient = new GradientSkyConfig
				{
					ZenithColor = new ColorRGBA(0.1f, 0.2f, 0.3f, 1.0f),
					SkyExponent = 2.0f,
					GroundExponent = 0.75f,
					Intensity = 3.0f
				}
			}
		};
	}

	private static void AssertSkyMatches(SkyConfig sky)
	{
		Assert.Multiple(() =>
		{
			Assert.That(sky.Mode, Is.EqualTo(SkyConfigMode.Hdri));
			Assert.That(sky.LightingQuality, Is.EqualTo(SkyLightingQuality.High));
			Assert.That(sky.Procedural.MieAnisotropy, Is.EqualTo(0.7f));
			Assert.That(sky.Procedural.OzoneTipAltitudeKm, Is.EqualTo(22.0f));
			Assert.That(sky.Hdri.Texture.NodeId, Is.EqualTo(HdriNodeId));
			Assert.That(sky.Hdri.RotationDegrees, Is.EqualTo(135.0f));
			Assert.That(sky.Hdri.Intensity, Is.EqualTo(0.5f));
			Assert.That(sky.Gradient.ZenithColor.G, Is.EqualTo(0.2f));
			Assert.That(sky.Gradient.SkyExponent, Is.EqualTo(2.0f));
			Assert.That(sky.Gradient.GroundExponent, Is.EqualTo(0.75f));
			Assert.That(sky.Gradient.Intensity, Is.EqualTo(3.0f));
		});
	}
}

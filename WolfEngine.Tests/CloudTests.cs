using System.Numerics;
using System.Text.Json;
using WolfEngine.AssetPipeline;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Clouds;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class CloudTests
{
	[Test]
	public void ConfigDefaultsAreDisabledCumulusAtMediumQuality()
	{
		var config = new CloudConfig();

		Assert.Multiple(() =>
		{
			Assert.That(config.Enabled, Is.False);
			Assert.That(config.Coverage, Is.EqualTo(0.5f));
			Assert.That(config.CloudBaseAltitude, Is.EqualTo(1500.0f));
			Assert.That(config.CloudThickness, Is.EqualTo(2500.0f));
			Assert.That(config.GetCloudTopAltitude(), Is.EqualTo(4000.0f));
			Assert.That(config.Quality, Is.EqualTo(CloudQuality.Medium));
			Assert.That(config.CastShadows, Is.True);
		});
	}

	[TestCase(-1.0f, 0.0f)]
	[TestCase(0.25f, 0.25f)]
	[TestCase(2.0f, 1.0f)]
	[TestCase(float.NaN, 0.5f)]
	public void CoverageIsClampedToAFraction(float value, float expected)
	{
		var config = new CloudConfig { Coverage = value };

		Assert.That(config.Coverage, Is.EqualTo(expected));
	}

	[TestCase(10.0f, CloudConfig.MinThickness)]
	[TestCase(3000.0f, 3000.0f)]
	[TestCase(20000.0f, CloudConfig.MaxThickness)]
	public void ThicknessIsClamped(float value, float expected)
	{
		var config = new CloudConfig { CloudThickness = value };

		Assert.That(config.CloudThickness, Is.EqualTo(expected));
	}

	[TestCase(-90.0f, 270.0f)]
	[TestCase(45.0f, 45.0f)]
	[TestCase(450.0f, 90.0f)]
	[TestCase(float.PositiveInfinity, 0.0f)]
	public void WindDirectionWrapsToACompassBearing(float value, float expected)
	{
		var config = new CloudConfig { WindDirection = value };

		Assert.That(config.WindDirection, Is.EqualTo(expected).Within(1e-4f));
	}

	[Test]
	public void RenderConfigWithoutCloudsKeyDeserializesToDefaults()
	{
		var options = AssetJson.GetSerializerOptions(typeof(RenderConfig));
		var copy = JsonSerializer.Deserialize<RenderConfig>("{}", options)!;

		Assert.Multiple(() =>
		{
			Assert.That(copy.Clouds.Enabled, Is.False);
			Assert.That(copy.Clouds.Coverage, Is.EqualTo(new CloudConfig().Coverage));
			Assert.That(copy.Clouds.Quality, Is.EqualTo(CloudQuality.Medium));
		});
	}

	[Test]
	public void EditorJson_RoundTripsCloudSettings()
	{
		var options = AssetJson.GetSerializerOptions(typeof(RenderConfig));
		var json = JsonSerializer.Serialize(CreateConfig(), options);
		var copy = JsonSerializer.Deserialize<RenderConfig>(json, options)!;

		AssertCloudsMatch(copy.Clouds);
	}

	[Test]
	public void EditorJson_DoesNotSerializeTheDerivedTopAltitude()
	{
		var options = AssetJson.GetSerializerOptions(typeof(RenderConfig));
		var json = JsonSerializer.Serialize(CreateConfig(), options);

		Assert.That(json, Does.Not.Contain("TopAltitude"));
	}

	[Test]
	public void CookedData_RoundTripsCloudSettings()
	{
		var bytes = CookedDataAsset.Write(CreateConfig());
		var copy = (RenderConfig)CookedDataAsset.Read(bytes, typeof(RenderConfig));

		AssertCloudsMatch(copy.Clouds);
	}

	[Test]
	public void EditorJson_ClampsOutOfRangeSerializedValues()
	{
		var options = AssetJson.GetSerializerOptions(typeof(RenderConfig));
		var json = "{\"Clouds\":{\"Coverage\":5,\"CloudThickness\":1,\"WindDirection\":-30}}";
		var copy = JsonSerializer.Deserialize<RenderConfig>(json, options)!;

		Assert.Multiple(() =>
		{
			Assert.That(copy.Clouds.Coverage, Is.EqualTo(1.0f));
			Assert.That(copy.Clouds.CloudThickness, Is.EqualTo(CloudConfig.MinThickness));
			Assert.That(copy.Clouds.WindDirection, Is.EqualTo(330.0f).Within(1e-4f));
		});
	}

	[Test]
	public void HigherQualityNeverCostsLess()
	{
		var low = CloudQualitySettings.Get(CloudQuality.Low);
		var medium = CloudQualitySettings.Get(CloudQuality.Medium);
		var high = CloudQualitySettings.Get(CloudQuality.High);

		Assert.Multiple(() =>
		{
			Assert.That(low.TraceDivisor, Is.GreaterThanOrEqualTo(medium.TraceDivisor));
			Assert.That(medium.TraceDivisor, Is.GreaterThanOrEqualTo(high.TraceDivisor));
			Assert.That(low.PrimarySamples, Is.LessThan(medium.PrimarySamples));
			Assert.That(medium.PrimarySamples, Is.LessThan(high.PrimarySamples));
			Assert.That(low.LightSamples, Is.LessThan(medium.LightSamples));
			Assert.That(medium.LightSamples, Is.LessThan(high.LightSamples));
		});
	}

	[TestCase(SkyConfigMode.Procedural, true, true)]
	[TestCase(SkyConfigMode.Procedural, false, false)]
	[TestCase(SkyConfigMode.Hdri, true, false)]
	[TestCase(SkyConfigMode.Gradient, true, false)]
	public void CloudsOnlyRenderWithTheProceduralSky(SkyConfigMode mode, bool enabled, bool expected)
	{
		var config = new RenderConfig
		{
			Sky = new SkyConfig { Mode = mode },
			Clouds = new CloudConfig { Enabled = enabled }
		};

		Assert.That(RenderGraphFrameBuilder.UsesClouds(config), Is.EqualTo(expected));
	}

	[Test]
	public void TraceSizeRoundsUp()
	{
		Assert.That(CloudViewResources.GetTraceSize(new Mathematics.Int2(1921, 1080), 2), Is.EqualTo(new Mathematics.Int2(961, 540)));
	}

	[Test]
	public void NoiseOffsetKeepsPrecisionFarFromTheOrigin()
	{
		const float period = 6000.0f;
		var offset = CloudFrame.WrapOffset(1.0e9 + 1234.5, -1.0e9 - 0.25, period);

		Assert.Multiple(() =>
		{
			Assert.That(offset.X, Is.EqualTo(1234.5f + (float)(1.0e9 % period)).Within(1e-2f));
			Assert.That(offset.X, Is.InRange(0.0f, period));
			Assert.That(offset.Y, Is.InRange(0.0f, period));
		});
	}

	[Test]
	public void WindAdvancesAlongItsDirectionAndClampsHitches()
	{
		var view = new ViewClouds();
		var settings = new CloudConfig { WindDirection = 90.0f, WindSpeed = 10.0f };

		view.Advance(settings, 0.1f);
		view.Advance(settings, 10.0f);

		Assert.Multiple(() =>
		{
			Assert.That(view.WindDisplacementX, Is.EqualTo(10.0 * (0.1 + ViewClouds.MaxFrameSeconds)).Within(1e-4));
			Assert.That(view.WindDisplacementZ, Is.EqualTo(0.0).Within(1e-4));
			Assert.That(view.LastWindDelta.X, Is.EqualTo(10.0f * ViewClouds.MaxFrameSeconds).Within(1e-4f));
		});
	}

	[Test]
	public void EnvironmentRedrawsWhenCloudsDriftOrSettingsChange()
	{
		var view = new ViewClouds();
		var settings = new CloudConfig { WindSpeed = 0.0f };

		Assert.That(view.EnvironmentChanged(settings, Vector3.Zero), Is.True, "Never drawn.");

		view.MarkEnvironmentDrawn(settings, Vector3.Zero);
		Assert.That(view.EnvironmentChanged(settings, new Vector3(100.0f, 0.0f, 0.0f)), Is.False, "A short move keeps the environment.");
		Assert.That(view.EnvironmentChanged(settings, new Vector3(1000.0f, 0.0f, 0.0f)), Is.True, "A long move redraws it.");
		Assert.That(view.EnvironmentChanged(settings with { }, Vector3.Zero), Is.False);

		var changed = settings;
		changed.Coverage = 0.9f;
		Assert.That(view.EnvironmentChanged(changed, Vector3.Zero), Is.True, "New settings redraw it.");
	}

	private static RenderConfig CreateConfig()
	{
		return new RenderConfig
		{
			Clouds = new CloudConfig
			{
				Enabled = true,
				Coverage = 0.7f,
				Density = 1.5f,
				CloudBaseAltitude = 2000.0f,
				CloudThickness = 3000.0f,
				ShapeScale = 2.0f,
				DetailStrength = 0.6f,
				WindDirection = 200.0f,
				WindSpeed = 30.0f,
				AmbientStrength = 0.8f,
				MultipleScattering = 0.5f,
				Quality = CloudQuality.High,
				CastShadows = false
			}
		};
	}

	private static void AssertCloudsMatch(CloudConfig clouds)
	{
		Assert.Multiple(() =>
		{
			Assert.That(clouds.Enabled, Is.True);
			Assert.That(clouds.Coverage, Is.EqualTo(0.7f));
			Assert.That(clouds.Density, Is.EqualTo(1.5f));
			Assert.That(clouds.CloudBaseAltitude, Is.EqualTo(2000.0f));
			Assert.That(clouds.CloudThickness, Is.EqualTo(3000.0f));
			Assert.That(clouds.ShapeScale, Is.EqualTo(2.0f));
			Assert.That(clouds.DetailStrength, Is.EqualTo(0.6f));
			Assert.That(clouds.WindDirection, Is.EqualTo(200.0f));
			Assert.That(clouds.WindSpeed, Is.EqualTo(30.0f));
			Assert.That(clouds.AmbientStrength, Is.EqualTo(0.8f));
			Assert.That(clouds.MultipleScattering, Is.EqualTo(0.5f));
			Assert.That(clouds.Quality, Is.EqualTo(CloudQuality.High));
			Assert.That(clouds.CastShadows, Is.False);
		});
	}
}

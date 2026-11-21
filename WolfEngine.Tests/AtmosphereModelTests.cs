using System.Numerics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Atmosphere;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class AtmosphereModelTests
{
	[Test]
	public void ZenithTransmittanceAtSeaLevel_MatchesEarthOpticalDepth()
	{
		var model = new AtmosphereModel(new ProceduralSkyConfig());

		var transmittance = model.GetTransmittance(AtmosphereModel.MinCameraAltitudeKm, Vector3.UnitY);

		// Optical depth straight up is about 0.06 (red), 0.14 (green) and 0.27 (blue): Rayleigh scale height times
		// its coefficient, plus a little Mie and the ozone layer. The reference's 40-step march reads a few percent
		// less depth than that, and the CPU matches the reference, so the bounds allow for it.
		Assert.Multiple(() =>
		{
			Assert.That(transmittance.X, Is.InRange(0.92f, 0.97f));
			Assert.That(transmittance.Y, Is.InRange(0.85f, 0.91f));
			Assert.That(transmittance.Z, Is.InRange(0.74f, 0.81f));
		});
	}

	[Test]
	public void TransmittanceTowardTheHorizon_IsRedderAndDimmerThanTowardTheZenith()
	{
		var model = new AtmosphereModel(new ProceduralSkyConfig());
		var lowSun = Vector3.Normalize(new Vector3(1.0f, MathF.Tan(5.0f * MathF.PI / 180.0f), 0.0f));

		var zenith = model.GetTransmittance(0.1f, Vector3.UnitY);
		var low = model.GetTransmittance(0.1f, lowSun);

		Assert.Multiple(() =>
		{
			Assert.That(low.Z, Is.LessThan(zenith.Z));
			Assert.That(low.X / low.Z, Is.GreaterThan(zenith.X / zenith.Z));
		});
	}

	[Test]
	public void SunBelowTheHorizon_IsInThePlanetsShadow()
	{
		var model = new AtmosphereModel(new ProceduralSkyConfig());
		var belowHorizon = Vector3.Normalize(new Vector3(1.0f, -0.1f, 0.0f));

		Assert.That(model.GetTransmittance(1.0f, belowHorizon), Is.EqualTo(Vector3.Zero));
	}

	[Test]
	public void CameraAltitude_IsMeasuredFromTheGroundHeightAndKeptInsideTheAtmosphere()
	{
		var model = new AtmosphereModel(new ProceduralSkyConfig { GroundHeight = -100.0f });

		Assert.Multiple(() =>
		{
			Assert.That(model.GetCameraAltitudeKm(900.0f), Is.EqualTo(1.0f).Within(1e-5f));
			Assert.That(model.GetCameraAltitudeKm(-500.0f), Is.EqualTo(AtmosphereModel.MinCameraAltitudeKm));
			Assert.That(model.GetCameraAltitudeKm(1.0e9f), Is.LessThan(model.TopRadius - model.BottomRadius));
		});
	}

	[Test]
	public void LutHash_ChangesWithTheMediumButNotWithTheSunDiscOrGroundHeight()
	{
		var baseline = new AtmosphereModel(new ProceduralSkyConfig()).GetLutHash();

		Assert.Multiple(() =>
		{
			Assert.That(new AtmosphereModel(new ProceduralSkyConfig { MieAnisotropy = 0.7f }).GetLutHash(), Is.Not.EqualTo(baseline));
			Assert.That(new AtmosphereModel(new ProceduralSkyConfig { GroundAlbedo = new ColorRGBA(0.1f, 0.1f, 0.1f, 1.0f) }).GetLutHash(), Is.Not.EqualTo(baseline));
			Assert.That(new AtmosphereModel(new ProceduralSkyConfig { SunDiscAngularDiameter = 2.0f }).GetLutHash(), Is.EqualTo(baseline));
			Assert.That(new AtmosphereModel(new ProceduralSkyConfig { GroundHeight = 50.0f }).GetLutHash(), Is.EqualTo(baseline));
		});
	}

	[Test]
	public void SunLightAttenuation_TintsInProceduralModeAndFadesAtTheHorizonOtherwise()
	{
		var light = new Light { Type = LightType.Directional, Intensity = 3.0f, HorizonFade = true };
		var lowSunForward = -Vector3.Normalize(new Vector3(1.0f, 0.05f, 0.0f));

		var procedural = SunLightAttenuation.ForSky(new SkyConfig(), 2.0f).GetColorScale(light, lowSunForward);
		var gradient = SunLightAttenuation.ForSky(new SkyConfig { Mode = SkyConfigMode.Gradient }, 2.0f).GetColorScale(light, lowSunForward);

		Assert.Multiple(() =>
		{
			Assert.That(procedural.X, Is.GreaterThan(procedural.Z), "A low sun is reddened by the atmosphere.");
			Assert.That(gradient.X, Is.EqualTo(gradient.Z), "Without an atmosphere the horizon fade is grey.");
			Assert.That(gradient.X, Is.EqualTo(DirectionalLightUtility.GetIntensityScale(light, lowSunForward)));
		});
	}
}

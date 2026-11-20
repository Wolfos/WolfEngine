using System.Numerics;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Rendering.Atmosphere;

/// <summary>
/// <see cref="ProceduralSkyConfig"/> converted to the coefficients of Hillaire's atmosphere model, in kilometres
/// and 1/km, with the planet's center at the origin and +Y up. The GPU passes and the CPU sun transmittance both
/// read the atmosphere through this type, so they always describe the same medium.
/// </summary>
public readonly struct AtmosphereModel
{
	/// <summary>
	/// Lowest camera altitude above the ground, in kilometres. The reference offsets the planet by 10 m when it tests
	/// for the planet's shadow, so a camera closer to the ground than that would see itself shadowed.
	/// </summary>
	public const float MinCameraAltitudeKm = 0.02f;

	/// <summary>Matches PLANET_RADIUS_OFFSET in the reference implementation.</summary>
	public const float PlanetRadiusOffsetKm = 0.01f;

	/// <summary>Matches the transmittance LUT's fixed sample count in the reference implementation.</summary>
	public const int TransmittanceSampleCount = 40;

	private const float MetresPerKilometre = 1000.0f;

	public AtmosphereModel(in ProceduralSkyConfig config)
	{
		BottomRadius = MathF.Max(config.GroundRadiusKm, 1.0f);
		TopRadius = BottomRadius + MathF.Max(config.AtmosphereHeightKm, 0.1f);
		GroundHeightMetres = config.GroundHeight;

		RayleighScattering = ToVector(config.RayleighScattering) * MathF.Max(config.RayleighScatteringScale, 0.0f);
		RayleighDensityExpScale = -1.0f / MathF.Max(config.RayleighExponentialDistributionKm, 0.001f);

		MieScattering = ToVector(config.MieScattering) * MathF.Max(config.MieScatteringScale, 0.0f);
		MieAbsorption = ToVector(config.MieAbsorption) * MathF.Max(config.MieAbsorptionScale, 0.0f);
		MieExtinction = MieScattering + MieAbsorption;
		MieDensityExpScale = -1.0f / MathF.Max(config.MieExponentialDistributionKm, 0.001f);
		MiePhaseG = Math.Clamp(config.MieAnisotropy, 0.0f, 0.999f);

		// The ozone tent of Unreal's SkyAtmosphere, written as the reference's two linear density layers.
		var tipAltitude = MathF.Max(config.OzoneTipAltitudeKm, 0.0f);
		var tipWidth = MathF.Max(config.OzoneTipWidthKm, 0.001f);
		AbsorptionDensity0LayerWidth = tipAltitude;
		AbsorptionDensity0LinearTerm = 1.0f / tipWidth;
		AbsorptionDensity0ConstantTerm = 1.0f - tipAltitude / tipWidth;
		AbsorptionDensity1LinearTerm = -1.0f / tipWidth;
		AbsorptionDensity1ConstantTerm = 1.0f + tipAltitude / tipWidth;
		AbsorptionExtinction = ToVector(config.OzoneAbsorption) * MathF.Max(config.OzoneAbsorptionScale, 0.0f);

		GroundAlbedo = Vector3.Clamp(ToVector(config.GroundAlbedo), Vector3.Zero, Vector3.One);
		SunDiscHalfAngleRadians = Math.Clamp(config.SunDiscAngularDiameter, 0.0f, 20.0f) * 0.5f * (MathF.PI / 180.0f);
	}

	public float BottomRadius { get; }
	public float TopRadius { get; }
	public float GroundHeightMetres { get; }

	public Vector3 RayleighScattering { get; }
	public float RayleighDensityExpScale { get; }

	public Vector3 MieScattering { get; }
	public Vector3 MieAbsorption { get; }
	public Vector3 MieExtinction { get; }
	public float MieDensityExpScale { get; }
	public float MiePhaseG { get; }

	public float AbsorptionDensity0LayerWidth { get; }
	public float AbsorptionDensity0ConstantTerm { get; }
	public float AbsorptionDensity0LinearTerm { get; }
	public float AbsorptionDensity1ConstantTerm { get; }
	public float AbsorptionDensity1LinearTerm { get; }
	public Vector3 AbsorptionExtinction { get; }

	public Vector3 GroundAlbedo { get; }
	public float SunDiscHalfAngleRadians { get; }

	/// <summary>Camera altitude above the ground in kilometres, kept inside the atmosphere.</summary>
	public float GetCameraAltitudeKm(float cameraWorldY)
	{
		var altitude = (cameraWorldY - GroundHeightMetres) / MetresPerKilometre;
		var maxAltitude = TopRadius - BottomRadius - PlanetRadiusOffsetKm;

		return Math.Clamp(altitude, MinCameraAltitudeKm, maxAltitude);
	}

	/// <summary>
	/// Fraction of light that reaches a point <paramref name="altitudeKm"/> above the ground from direction
	/// <paramref name="towardLight"/>, zero when the planet is in the way. This is the reference's transmittance LUT
	/// evaluation, so the sun light on the CPU matches what the sky's LUTs see.
	/// </summary>
	public Vector3 GetTransmittance(float altitudeKm, Vector3 towardLight)
	{
		if (towardLight == Vector3.Zero) return Vector3.Zero;

		var direction = Vector3.Normalize(towardLight);
		var position = new Vector3(0.0f, BottomRadius + altitudeKm, 0.0f);
		var up = Vector3.Normalize(position);

		// The reference's planet shadow test, with the planet lowered by the same offset.
		var shadowCenter = up * PlanetRadiusOffsetKm;
		if (RaySphereIntersectNearest(position, direction, shadowCenter, BottomRadius) >= 0.0f)
		{
			return Vector3.Zero;
		}

		var tTop = RaySphereIntersectNearest(position, direction, Vector3.Zero, TopRadius);
		if (tTop <= 0.0f)
		{
			return Vector3.One;
		}

		const float sampleSegmentT = 0.3f;
		var opticalDepth = Vector3.Zero;
		var t = 0.0f;
		for (var sample = 0; sample < TransmittanceSampleCount; sample++)
		{
			var newT = tTop * (sample + sampleSegmentT) / TransmittanceSampleCount;
			var dt = newT - t;
			t = newT;

			var samplePosition = position + direction * t;
			opticalDepth += GetExtinction(samplePosition.Length() - BottomRadius) * dt;
		}

		return new Vector3(
			MathF.Exp(-opticalDepth.X),
			MathF.Exp(-opticalDepth.Y),
			MathF.Exp(-opticalDepth.Z));
	}

	/// <summary>Extinction of the whole medium at an altitude above the ground, in 1/km. The reference's sampleMediumRGB.</summary>
	public Vector3 GetExtinction(float altitudeKm)
	{
		var densityMie = MathF.Exp(MieDensityExpScale * altitudeKm);
		var densityRayleigh = MathF.Exp(RayleighDensityExpScale * altitudeKm);
		var densityOzone = Math.Clamp(
			altitudeKm < AbsorptionDensity0LayerWidth
				? AbsorptionDensity0LinearTerm * altitudeKm + AbsorptionDensity0ConstantTerm
				: AbsorptionDensity1LinearTerm * altitudeKm + AbsorptionDensity1ConstantTerm,
			0.0f,
			1.0f);

		return densityMie * MieExtinction +
		       densityRayleigh * RayleighScattering +
		       densityOzone * AbsorptionExtinction;
	}

	/// <summary>
	/// Content hash of everything the transmittance and multiple-scattering LUTs depend on, so a view rebuilds them
	/// only when the atmosphere itself changes.
	/// </summary>
	public int GetLutHash()
	{
		var hash = new HashCode();
		hash.Add(BottomRadius);
		hash.Add(TopRadius);
		hash.Add(RayleighScattering);
		hash.Add(RayleighDensityExpScale);
		hash.Add(MieScattering);
		hash.Add(MieAbsorption);
		hash.Add(MieDensityExpScale);
		hash.Add(MiePhaseG);
		hash.Add(AbsorptionDensity0LayerWidth);
		hash.Add(AbsorptionDensity0LinearTerm);
		hash.Add(AbsorptionDensity0ConstantTerm);
		hash.Add(AbsorptionDensity1LinearTerm);
		hash.Add(AbsorptionDensity1ConstantTerm);
		hash.Add(AbsorptionExtinction);
		hash.Add(GroundAlbedo);

		return hash.ToHashCode();
	}

	/// <summary>The reference's raySphereIntersectNearest: distance to the nearest hit ahead, or -1.</summary>
	private static float RaySphereIntersectNearest(Vector3 origin, Vector3 direction, Vector3 center, float radius)
	{
		var a = Vector3.Dot(direction, direction);
		var toOrigin = origin - center;
		var b = 2.0f * Vector3.Dot(direction, toOrigin);
		var c = Vector3.Dot(toOrigin, toOrigin) - radius * radius;
		var delta = b * b - 4.0f * a * c;
		if (delta < 0.0f || a == 0.0f)
		{
			return -1.0f;
		}

		var root = MathF.Sqrt(delta);
		var solution0 = (-b - root) / (2.0f * a);
		var solution1 = (-b + root) / (2.0f * a);
		if (solution0 < 0.0f && solution1 < 0.0f)
		{
			return -1.0f;
		}

		if (solution0 < 0.0f)
		{
			return MathF.Max(0.0f, solution1);
		}

		if (solution1 < 0.0f)
		{
			return MathF.Max(0.0f, solution0);
		}

		return MathF.Max(0.0f, MathF.Min(solution0, solution1));
	}

	private static Vector3 ToVector(in ColorRGBA color)
	{
		return new Vector3(MathF.Max(color.R, 0.0f), MathF.Max(color.G, 0.0f), MathF.Max(color.B, 0.0f));
	}
}

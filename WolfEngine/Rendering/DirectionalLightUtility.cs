using System.Numerics;
using WolfEngine.Rendering.Atmosphere;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Rendering;

public static class DirectionalLightUtility
{
	private const float HorizonFadeStart = -0.05f;
	private const float HorizonFadeEnd = 0.10f;

	public static float GetIntensityScale(in Light light, Vector3 forwardDirection)
	{
		if (light.Type != LightType.Directional || light.HorizonFade == false)
		{
			return 1.0f;
		}

		return ComputeHorizonFadeFactor(forwardDirection);
	}

	public static float ComputeHorizonFadeFactor(Vector3 forwardDirection)
	{
		var lightVector = forwardDirection == Vector3.Zero
			? Vector3.UnitY
			: Vector3.Normalize(-forwardDirection);
		return SmoothStep(HorizonFadeStart, HorizonFadeEnd, lightVector.Y);
	}

	private static float SmoothStep(float edge0, float edge1, float value)
	{
		if (edge0 == edge1)
		{
			return value < edge0 ? 0.0f : 1.0f;
		}

		var t = Math.Clamp((value - edge0) / (edge1 - edge0), 0.0f, 1.0f);
		return t * t * (3.0f - (2.0f * t));
	}
}

/// <summary>
/// How a view's sky dims and tints its directional lights. The Procedural sky's atmosphere tints each light by
/// the transmittance from the camera toward it, which also covers the planet's shadow; other sky modes fade a
/// light out at the horizon when the light asks for it.
/// </summary>
public readonly struct SunLightAttenuation
{
	private readonly AtmosphereModel _atmosphere;
	private readonly float _cameraAltitudeKm;
	private readonly bool _usesAtmosphere;

	private SunLightAttenuation(in AtmosphereModel atmosphere, float cameraAltitudeKm)
	{
		_atmosphere = atmosphere;
		_cameraAltitudeKm = cameraAltitudeKm;
		_usesAtmosphere = true;
	}

	/// <summary>The view's attenuation for its sky settings and camera height.</summary>
	public static SunLightAttenuation ForSky(in SkyConfig sky, float cameraWorldY)
	{
		if (sky.Mode != SkyConfigMode.Procedural)
		{
			return default;
		}

		var atmosphere = new AtmosphereModel(sky.Procedural);

		return new SunLightAttenuation(atmosphere, atmosphere.GetCameraAltitudeKm(cameraWorldY));
	}

	/// <summary>RGB scale for <paramref name="light"/>'s color, given the direction it travels.</summary>
	public Vector3 GetColorScale(in Light light, Vector3 forwardDirection)
	{
		if (light.Type != LightType.Directional)
		{
			return Vector3.One;
		}

		if (_usesAtmosphere == false)
		{
			return new Vector3(DirectionalLightUtility.GetIntensityScale(light, forwardDirection));
		}

		var towardLight = forwardDirection == Vector3.Zero
			? Vector3.UnitY
			: -forwardDirection;

		return _atmosphere.GetTransmittance(_cameraAltitudeKm, towardLight);
	}
}

using System;
using System.Numerics;

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

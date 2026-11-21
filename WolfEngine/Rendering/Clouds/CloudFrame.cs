using System.Numerics;
using WolfEngine.Rendering.Atmosphere;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Rendering.Clouds;

/// <summary>
/// A view's cloud layer for one frame: its settings resolved into the units the shaders use, its position relative
/// to the camera and the atmosphere, and how far the wind has carried it. Written into a shader's
/// <c>CloudConstants clouds</c> field (cloud_common.slang).
/// </summary>
internal readonly struct CloudFrame
{
	/// <summary>World size the weather map spans at a shape scale of one, in metres.</summary>
	public const float WeatherTileMetres = 40000.0f;

	/// <summary>World size the shape noise spans at a shape scale of one, in metres.</summary>
	public const float ShapeTileMetres = 6000.0f;

	/// <summary>World size the detail noise spans at a shape scale of one, in metres.</summary>
	public const float DetailTileMetres = 1200.0f;

	/// <summary>Extinction of the densest cloud at a density of one, in 1/m: a mean free path of 25 m.</summary>
	public const float ExtinctionAtFullDensity = 0.04f;

	/// <summary>How far cloud tops lean downwind of their bases, in metres per unit of wind speed.</summary>
	public const float TopSkewMetresPerWindSpeed = 20.0f;

	public const float ForwardPhaseG = 0.8f;
	public const float BackwardPhaseG = -0.2f;
	public const float ForwardPhaseWeight = 0.6f;

	public CloudFrame(
		in CloudConfig settings,
		in AtmosphereFrame atmosphere,
		Vector3 cameraOrigin,
		ViewClouds view)
	{
		Settings = settings;
		Quality = CloudQualitySettings.Get(settings.Quality);
		CameraAltitudeMetres = atmosphere.CameraAltitudeKm * 1000.0f;
		CameraRadiusMetres = (atmosphere.Model.BottomRadius + atmosphere.CameraAltitudeKm) * 1000.0f;
		SunDirection = atmosphere.SunDirectionWorld;
		SunIlluminance = atmosphere.SunIlluminance;
		WindDirection = GetWindDirection(settings.WindDirection);
		WindDelta = view.LastWindDelta;
		JitterOffset = (view.FrameIndex * 0.618034f) % 1.0f;
		SubpixelJitter = GetSubpixelJitter(view.FrameIndex);

		var scale = settings.ShapeScale;
		WeatherTile = WeatherTileMetres * scale;
		ShapeTile = ShapeTileMetres * scale;
		DetailTile = DetailTileMetres * scale;

		var originX = cameraOrigin.X - view.WindDisplacementX;
		var originZ = cameraOrigin.Z - view.WindDisplacementZ;
		WeatherOffset = WrapOffset(originX, originZ, WeatherTile);
		ShapeOffset = WrapOffset(originX, originZ, ShapeTile);
		DetailOffset = WrapOffset(originX, originZ, DetailTile);
	}

	public CloudConfig Settings { get; }
	public CloudQualitySettings Quality { get; }
	public float CameraAltitudeMetres { get; }
	public float CameraRadiusMetres { get; }

	/// <summary>Direction toward the sun, +Y up.</summary>
	public Vector3 SunDirection { get; }

	/// <summary>The sun light's color times intensity at the top of the atmosphere.</summary>
	public Vector3 SunIlluminance { get; }

	/// <summary>Unit world XZ direction the wind blows toward.</summary>
	public Vector2 WindDirection { get; }

	/// <summary>How far the wind carried the clouds since the view's previous frame, in world XZ metres.</summary>
	public Vector2 WindDelta { get; }

	/// <summary>Offset of this frame's ray-march jitter, in 0..1.</summary>
	public float JitterOffset { get; }

	/// <summary>This frame's ray offset within a trace pixel, in pixels, each axis in -0.5..0.5.</summary>
	public Vector2 SubpixelJitter { get; }

	public float WeatherTile { get; }
	public float ShapeTile { get; }
	public float DetailTile { get; }

	/// <summary>
	/// What each noise texture adds to a camera-relative XZ position, wrapped to the texture's period. Computed in
	/// double precision so the clouds keep their detail far from the world origin and after hours of wind.
	/// </summary>
	public Vector2 WeatherOffset { get; }
	public Vector2 ShapeOffset { get; }
	public Vector2 DetailOffset { get; }

	/// <summary>Height of the cloud base relative to the camera, in metres. Ignores curvature.</summary>
	public float BaseHeightAboveCamera => Settings.CloudBaseAltitude - CameraAltitudeMetres;

	/// <summary>
	/// The R2 low-discrepancy sequence (Roberts 2018), which covers a pixel evenly in any run of consecutive frames.
	/// </summary>
	public static Vector2 GetSubpixelJitter(uint frameIndex)
	{
		var x = (0.5 + frameIndex * 0.7548776662466927) % 1.0;
		var y = (0.5 + frameIndex * 0.5698402909980532) % 1.0;

		return new Vector2((float)x - 0.5f, (float)y - 0.5f);
	}

	public static Vector2 GetWindDirection(float degrees)
	{
		var radians = degrees * (MathF.PI / 180.0f);

		return new Vector2(MathF.Sin(radians), MathF.Cos(radians));
	}

	public void Write(ShaderPropertyWriter writer)
	{
		var settings = Settings;
		writer.SetVector4(
			"clouds.layer",
			new Vector4(
				CameraAltitudeMetres,
				settings.CloudBaseAltitude,
				settings.GetCloudTopAltitude(),
				CameraRadiusMetres));
		writer.SetVector4(
			"clouds.shape",
			new Vector4(
				settings.Coverage,
				settings.Density * ExtinctionAtFullDensity,
				settings.DetailStrength,
				settings.MultipleScattering));
		writer.SetVector4(
			"clouds.noiseScale",
			new Vector4(
				1.0f / WeatherTile,
				1.0f / ShapeTile,
				1.0f / DetailTile,
				settings.WindSpeed * TopSkewMetresPerWindSpeed));
		writer.SetVector4("clouds.noiseOffset0", new Vector4(WeatherOffset, ShapeOffset.X, ShapeOffset.Y));
		writer.SetVector4("clouds.noiseOffset1", new Vector4(DetailOffset, WindDirection.X, WindDirection.Y));
		writer.SetVector4(
			"clouds.lighting",
			new Vector4(
				settings.AmbientStrength,
				ForwardPhaseG,
				BackwardPhaseG,
				ForwardPhaseWeight));
		writer.SetVector4(
			"clouds.sampling",
			new Vector4(
				Quality.PrimarySamples,
				Quality.LightSamples,
				Quality.MaxTraceDistanceMetres,
				0.0f));
		writer.SetVector4("clouds.sunDirection", new Vector4(SunDirection, 0.0f));
		writer.SetVector4("clouds.sunIlluminance", new Vector4(SunIlluminance, 0.0f));
	}

	/// <summary>The same constants with the environment's own sample counts.</summary>
	public void WriteForEnvironment(ShaderPropertyWriter writer)
	{
		Write(writer);
		writer.SetVector4(
			"clouds.sampling",
			new Vector4(
				Quality.EnvironmentSamples,
				2.0f,
				Quality.MaxTraceDistanceMetres,
				0.0f));
	}

	internal static Vector2 WrapOffset(double x, double z, float period)
	{
		return new Vector2(Wrap(x, period), Wrap(z, period));
	}

	private static float Wrap(double value, double period)
	{
		var wrapped = value % period;
		if (wrapped < 0.0)
		{
			wrapped += period;
		}

		return (float)wrapped;
	}
}

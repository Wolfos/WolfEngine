using WolfEngine.ECS;

namespace WolfEngine.Rendering.Passes;

/// <summary>
/// One layer of procedural volumetric cumulus clouds, lit by the Procedural sky's atmosphere. Clouds only render
/// when the sky mode is <see cref="SkyConfigMode.Procedural"/>; HDRI and Gradient skies ignore these settings.
/// Altitudes and lengths are in metres.
/// </summary>
public struct CloudConfig
{
	public const float MinThickness = 100.0f;
	public const float MaxThickness = 8000.0f;
	public const float MaxBaseAltitude = 10000.0f;

	private float _coverage = 0.5f;
	private float _density = 1.0f;
	private float _cloudBaseAltitude = 1500.0f;
	private float _cloudThickness = 2500.0f;
	private float _shapeScale = 1.0f;
	private float _detailStrength = 0.4f;
	private float _windDirection = 45.0f;
	private float _windSpeed = 15.0f;
	private float _ambientStrength = 1.0f;
	private float _multipleScattering = 0.7f;

	public CloudConfig()
	{
	}

	/// <summary>Draws the cloud layer. Only used with the Procedural sky.</summary>
	public bool Enabled { get; set; } = false;

	/// <summary>Fraction of the sky covered by clouds, from clear (0) to overcast (1).</summary>
	[Header("Shape")]
	public float Coverage
	{
		readonly get => _coverage;
		set => _coverage = ClampFinite(value, 0.0f, 1.0f, 0.5f);
	}

	/// <summary>
	/// Multiplier for how much light the cloud material absorbs and scatters. Higher values give darker
	/// undersides and sharper silhouettes; lower values give thin, hazy clouds.
	/// </summary>
	public float Density
	{
		readonly get => _density;
		set => _density = ClampFinite(value, 0.0f, 4.0f, 1.0f);
	}

	/// <summary>Height of the flat cloud bases above the sky's ground height, in metres.</summary>
	public float CloudBaseAltitude
	{
		readonly get => _cloudBaseAltitude;
		set => _cloudBaseAltitude = ClampFinite(value, 0.0f, MaxBaseAltitude, 1500.0f);
	}

	/// <summary>Height of the layer from cloud base to the tallest tops, in metres.</summary>
	public float CloudThickness
	{
		readonly get => _cloudThickness;
		set => _cloudThickness = ClampFinite(value, MinThickness, MaxThickness, 2500.0f);
	}

	/// <summary>
	/// Size of individual clouds. One gives cumulus a few kilometres across; larger values give bigger, fewer
	/// clouds, smaller values give many small puffs.
	/// </summary>
	public float ShapeScale
	{
		readonly get => _shapeScale;
		set => _shapeScale = ClampFinite(value, 0.1f, 10.0f, 1.0f);
	}

	/// <summary>How strongly fine detail noise erodes the cloud edges, from smooth (0) to wispy (1).</summary>
	public float DetailStrength
	{
		readonly get => _detailStrength;
		set => _detailStrength = ClampFinite(value, 0.0f, 1.0f, 0.4f);
	}

	/// <summary>Compass direction the wind blows toward, in degrees. Zero is world +Z, 90 is world +X.</summary>
	[Header("Animation")]
	public float WindDirection
	{
		readonly get => _windDirection;
		set => _windDirection = WrapDegrees(value);
	}

	/// <summary>Speed the clouds drift at, in metres per second.</summary>
	public float WindSpeed
	{
		readonly get => _windSpeed;
		set => _windSpeed = ClampFinite(value, 0.0f, 200.0f, 15.0f);
	}

	/// <summary>Multiplier for skylight on the clouds. Lower values give more contrast between lit and shaded sides.</summary>
	[Header("Lighting")]
	public float AmbientStrength
	{
		readonly get => _ambientStrength;
		set => _ambientStrength = ClampFinite(value, 0.0f, 4.0f, 1.0f);
	}

	/// <summary>
	/// How much light scattered many times inside a cloud brightens its interior, from none (0, dark and
	/// contrasty) to full (1, bright and soft).
	/// </summary>
	public float MultipleScattering
	{
		readonly get => _multipleScattering;
		set => _multipleScattering = ClampFinite(value, 0.0f, 1.0f, 0.7f);
	}

	/// <summary>Rendering resolution and sample counts; see <see cref="CloudQuality"/>.</summary>
	[Header("Quality")]
	public CloudQuality Quality { get; set; } = CloudQuality.Medium;

	/// <summary>Whether clouds shadow sunlight on the ground and in volumetric fog.</summary>
	public bool CastShadows { get; set; } = true;

	/// <summary>The altitude of the cloud tops above the sky's ground height, in metres.</summary>
	public readonly float GetCloudTopAltitude()
	{
		return _cloudBaseAltitude + _cloudThickness;
	}

	private static float ClampFinite(float value, float min, float max, float fallback)
	{
		if (float.IsFinite(value) == false)
		{
			return fallback;
		}

		return Math.Clamp(value, min, max);
	}

	private static float WrapDegrees(float value)
	{
		if (float.IsFinite(value) == false)
		{
			return 0.0f;
		}

		var wrapped = value % 360.0f;
		if (wrapped < 0.0f)
		{
			wrapped += 360.0f;
		}

		return wrapped;
	}
}

public enum CloudQuality
{
	/// <summary>Quarter-resolution clouds with few samples, for low-end hardware.</summary>
	Low,

	/// <summary>Half-resolution clouds; the default balance of cost and detail.</summary>
	Medium,

	/// <summary>Half-resolution clouds with more primary and light samples, for sharper detail and shading.</summary>
	High
}

/// <summary>The internal cost parameters a <see cref="CloudQuality"/> level selects.</summary>
public readonly record struct CloudQualitySettings(
	int TraceDivisor,
	int PrimarySamples,
	int LightSamples,
	float MaxTraceDistanceMetres,
	int EnvironmentWidth,
	int EnvironmentSamples)
{
	public static CloudQualitySettings Get(CloudQuality quality)
	{
		return quality switch
		{
			CloudQuality.Low => new CloudQualitySettings(
				TraceDivisor: 4,
				PrimarySamples: 32,
				LightSamples: 3,
				MaxTraceDistanceMetres: 40000.0f,
				EnvironmentWidth: 256,
				EnvironmentSamples: 24),
			CloudQuality.High => new CloudQualitySettings(
				TraceDivisor: 2,
				PrimarySamples: 80,
				LightSamples: 6,
				MaxTraceDistanceMetres: 80000.0f,
				EnvironmentWidth: 512,
				EnvironmentSamples: 32),
			_ => new CloudQualitySettings(
				TraceDivisor: 2,
				PrimarySamples: 56,
				LightSamples: 4,
				MaxTraceDistanceMetres: 60000.0f,
				EnvironmentWidth: 512,
				EnvironmentSamples: 24)
		};
	}
}

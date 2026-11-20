using System.Numerics;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Rendering.Passes;

/// <summary>
/// The sky a view draws and lights the scene with. Only the settings group that <see cref="Mode"/> selects is
/// used; the others keep their values so switching back restores them.
/// </summary>
public struct SkyConfig
{
	public SkyConfig()
	{
	}

	public SkyConfigMode Mode { get; set; } = SkyConfigMode.Procedural;

	/// <summary>
	/// How much work rebuilding the sky's image-based lighting takes, which happens whenever the sky changes, for
	/// example every frame while the sun moves. Low is the default: for the Procedural sky it lights the scene
	/// indistinguishably at about a tenth of the cost. High is worth it for a detailed HDRI seen in mirror-like
	/// reflections.
	/// </summary>
	public SkyLightingQuality LightingQuality { get; set; } = SkyLightingQuality.Low;
	public ProceduralSkyConfig Procedural { get; set; } = new();
	public HdriSkyConfig Hdri { get; set; } = new();
	public GradientSkyConfig Gradient { get; set; } = new();
}

public enum SkyLightingQuality
{
	/// <summary>Full-resolution sky environment and sample counts.</summary>
	High,

	/// <summary>
	/// A low-resolution Procedural environment, which only lighting reads, and fewer irradiance and prefilter
	/// samples. The Procedural sky is smooth enough that the difference is hard to see.
	/// </summary>
	Low
}

public enum SkyConfigMode
{
	/// <summary>Analytic atmosphere lit by the scene's sun.</summary>
	Procedural,

	/// <summary>An HDR environment texture, equirectangular or cube.</summary>
	Hdri,

	/// <summary>Zenith, horizon and ground colors blended by view direction.</summary>
	Gradient
}

/// <summary>
/// A physically based planetary atmosphere (Hillaire 2020), lit by the scene's first directional light. Lengths
/// inside the atmosphere are in kilometres and coefficients in 1/km, matching Unreal's SkyAtmosphere component.
/// Colors are normalized spectral shapes and each scale sets the coefficient of the strongest channel.
/// </summary>
public struct ProceduralSkyConfig
{
	public ProceduralSkyConfig()
	{
	}

	/// <summary>Distance from the planet's center to its ground, in kilometres.</summary>
	public float GroundRadiusKm { get; set; } = 6360.0f;

	/// <summary>Height of the atmosphere above the ground, in kilometres.</summary>
	public float AtmosphereHeightKm { get; set; } = 100.0f;

	/// <summary>Diffuse reflectance of the planet's ground, seen below the horizon and bounced into the sky.</summary>
	public ColorRGBA GroundAlbedo { get; set; } = new(0.3f, 0.3f, 0.3f, 1.0f);

	/// <summary>World Y, in metres, of the planet's ground. Camera altitude is measured from it.</summary>
	public float GroundHeight { get; set; } = 0.0f;

	/// <summary>Spectral shape of Rayleigh (air molecule) scattering.</summary>
	public ColorRGBA RayleighScattering { get; set; } = new(0.175287f, 0.409607f, 1.0f, 1.0f);

	/// <summary>Rayleigh scattering coefficient of the strongest channel at sea level, in 1/km.</summary>
	public float RayleighScatteringScale { get; set; } = 0.0331f;

	/// <summary>Altitude, in kilometres, over which Rayleigh density falls by a factor of e.</summary>
	public float RayleighExponentialDistributionKm { get; set; } = 8.0f;

	/// <summary>Spectral shape of Mie (aerosol) scattering.</summary>
	public ColorRGBA MieScattering { get; set; } = new(1.0f, 1.0f, 1.0f, 1.0f);

	/// <summary>Mie scattering coefficient of the strongest channel at sea level, in 1/km.</summary>
	public float MieScatteringScale { get; set; } = 0.003996f;

	/// <summary>Spectral shape of Mie absorption.</summary>
	public ColorRGBA MieAbsorption { get; set; } = new(1.0f, 1.0f, 1.0f, 1.0f);

	/// <summary>Mie absorption coefficient of the strongest channel at sea level, in 1/km.</summary>
	public float MieAbsorptionScale { get; set; } = 0.000444f;

	/// <summary>How strongly aerosols scatter forward, 0 to just under 1. Higher values make a tighter glow around the sun.</summary>
	public float MieAnisotropy { get; set; } = 0.8f;

	/// <summary>Altitude, in kilometres, over which Mie density falls by a factor of e.</summary>
	public float MieExponentialDistributionKm { get; set; } = 1.2f;

	/// <summary>Spectral shape of ozone absorption.</summary>
	public ColorRGBA OzoneAbsorption { get; set; } = new(0.345561f, 1.0f, 0.045189f, 1.0f);

	/// <summary>Ozone absorption coefficient of the strongest channel at the layer's tip, in 1/km.</summary>
	public float OzoneAbsorptionScale { get; set; } = 0.001881f;

	/// <summary>Altitude, in kilometres, of the ozone layer's densest point.</summary>
	public float OzoneTipAltitudeKm { get; set; } = 25.0f;

	/// <summary>Distance, in kilometres, above and below the tip at which ozone density falls to zero.</summary>
	public float OzoneTipWidthKm { get; set; } = 15.0f;

	/// <summary>Angular diameter of the sun's disc, in degrees.</summary>
	public float SunDiscAngularDiameter { get; set; } = 0.545f;
}

public struct HdriSkyConfig
{
	public HdriSkyConfig()
	{
	}

	/// <summary>
	/// HDR environment texture: a 2:1 equirectangular panorama imported as a 2D texture, or one imported with the
	/// Cube shape. Unset, or not yet loaded, draws a black sky.
	/// </summary>
	public AssetRef<Texture> Texture { get; set; }

	/// <summary>Turns the environment around the vertical axis, in degrees.</summary>
	public float RotationDegrees { get; set; } = 0.0f;

	/// <summary>Multiplies the texture's radiance.</summary>
	public float Intensity { get; set; } = 1.0f;
}

public struct GradientSkyConfig
{
	public GradientSkyConfig()
	{
	}

	public ColorRGBA ZenithColor { get; set; } = new(0.08f, 0.2f, 0.45f, 1.0f);
	public ColorRGBA HorizonColor { get; set; } = new(0.3f, 0.4f, 0.55f, 1.0f);
	public ColorRGBA GroundColor { get; set; } = new(0.12f, 0.1f, 0.09f, 1.0f);

	/// <summary>
	/// Shapes the blend from the horizon up to the zenith. Below one the zenith color reaches down toward the
	/// horizon; above one the horizon color reaches higher.
	/// </summary>
	public float SkyExponent { get; set; } = 0.5f;

	/// <summary>Shapes the blend from the horizon down to the ground color, as <see cref="SkyExponent"/> does above it.</summary>
	public float GroundExponent { get; set; } = 0.3f;

	/// <summary>Multiplies the gradient's colors.</summary>
	public float Intensity { get; set; } = 1.0f;
}

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
	public ProceduralSkyConfig Procedural { get; set; } = new();
	public HdriSkyConfig Hdri { get; set; } = new();
	public GradientSkyConfig Gradient { get; set; } = new();
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

public struct ProceduralSkyConfig
{
	public ProceduralSkyConfig()
	{
	}

	public float Intensity { get; set; } = 25;
	public Vector3 SunColor { get; set; } = Vector3.One;
	public float SunSharpness { get; set; } = 256;

	/// <summary>Tint toward the zenith. Alpha is the falloff exponent of the tint away from the zenith.</summary>
	public ColorRGBA TopColor { get; set; } = new(0.2f, 0.45f, 0.85f, 1.4f);

	/// <summary>Tint at the horizon. Alpha strengthens the blend into <see cref="GroundColor"/> below the horizon.</summary>
	public ColorRGBA HorizonColor { get; set; } = new(0.65f, 0.75f, 0.9f, 1.0f);

	public ColorRGBA GroundColor { get; set; } = new(0.15f, 0.1f, 0.07f, 0.0f);
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

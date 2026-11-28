using System.Numerics;

namespace WolfEngine.Rendering.Passes;

/// <summary>
/// The scene's wind, which moves every material with a <see cref="WindMode"/>. See docs/foliage.md, Wind.
/// </summary>
public struct WindConfig
{
	private float _strength = 1.0f;
	private float _gustStrength = 0.6f;
	private float _gustFrequency = 0.25f;
	private float _gustScale = 40.0f;

	public WindConfig()
	{
	}

	/// <summary>Off holds every material at rest, as if each had no wind mode.</summary>
	public bool Enabled { get; set; } = true;

	/// <summary>The compass direction the wind blows towards, in degrees from +Z towards +X.</summary>
	public float DirectionDegrees { get; set; } = 45.0f;

	/// <summary>How hard the wind pushes: one bends a 20 m tree about a metre at its crown.</summary>
	public float Strength
	{
		get => _strength;
		set => _strength = float.IsFinite(value) ? Math.Max(0.0f, value) : 1.0f;
	}

	/// <summary>How much gusts add on top of <see cref="Strength"/>, as a fraction of it.</summary>
	public float GustStrength
	{
		get => _gustStrength;
		set => _gustStrength = float.IsFinite(value) ? Math.Max(0.0f, value) : 0.6f;
	}

	/// <summary>How fast the gust field scrolls downwind, in gust cells per second.</summary>
	public float GustFrequency
	{
		get => _gustFrequency;
		set => _gustFrequency = float.IsFinite(value) ? Math.Max(0.0f, value) : 0.25f;
	}

	/// <summary>The size of one gust cell in metres: how far apart neighbouring gusts are.</summary>
	public float GustScale
	{
		get => _gustScale;
		set => _gustScale = float.IsFinite(value) && value > 0.01f ? value : 40.0f;
	}
}

/// <summary>
/// One frame's wind as the shaders read it: two float4 values per frame, mirrored by MakeWindFrame in wind.slang.
/// </summary>
public readonly record struct WindFrameState(Vector4 DirectionStrength, Vector4 GustParams)
{
	/// <summary>No wind: every material stays at rest.</summary>
	public static WindFrameState Calm => new(new Vector4(0.0f, 1.0f, 0.0f, 0.0f), new Vector4(0.0f, 0.0f, 0.0f, 0.0f));

	public static WindFrameState FromConfig(in WindConfig config, float timeSeconds)
	{
		if (config.Enabled == false || config.Strength <= 0.0f)
		{
			return Calm;
		}

		var radians = config.DirectionDegrees * (MathF.PI / 180.0f);
		var direction = new Vector2(MathF.Sin(radians), MathF.Cos(radians));

		return new WindFrameState(
			new Vector4(direction.X, direction.Y, config.Strength, config.GustStrength),
			new Vector4(config.GustFrequency, 1.0f / config.GustScale, timeSeconds, 0.0f));
	}

	/// <summary>Writes the current and, when the shader has them, previous frame's fields.</summary>
	internal static void Write(ShaderPropertyWriter writer, WindFrameState current, WindFrameState previous, bool includePrevious)
	{
		writer.SetVector4("windDirectionStrength", current.DirectionStrength);
		writer.SetVector4("windGustParams", current.GustParams);
		if (includePrevious == false)
		{
			return;
		}

		writer.SetVector4("previousWindDirectionStrength", previous.DirectionStrength);
		writer.SetVector4("previousWindGustParams", previous.GustParams);
	}
}

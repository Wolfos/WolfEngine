using System.Numerics;

namespace WolfEngine.Editor.UnityImport;

/// <summary>
/// Converts Unity scene data into WolfEngine's space, in one place.
/// </summary>
/// <remarks>
/// Both engines are left-handed with +Y up and +Z forward, in metres, so positions, rotations and scales carry over
/// unchanged. That includes models: the exporter writes meshes so that WolfEngine's importer reads back Unity's own
/// vertices, rather than converting the source FBX files, which the two engines mirror along different axes.
/// </remarks>
public static class UnitySpace
{
	public static Vector3 ToVector3(float[] values)
	{
		RequireLength(values, 3);

		return new Vector3(values[0], values[1], values[2]);
	}

	public static Quaternion ToQuaternion(float[] values)
	{
		RequireLength(values, 4);

		var rotation = new Quaternion(values[0], values[1], values[2], values[3]);

		return rotation.LengthSquared() > 0.0f ? Quaternion.Normalize(rotation) : Quaternion.Identity;
	}

	/// <summary>The rotation of a Unity terrain tree, which Unity turns about +Y by <paramref name="yawRadians"/>.</summary>
	public static Quaternion ToTreeRotation(float yawRadians)
	{
		return Quaternion.CreateFromAxisAngle(Vector3.UnitY, yawRadians);
	}

	/// <summary>
	/// The colour of a black body at <paramref name="kelvin"/>, as Unity's lights use it, in linear RGB with the brightest
	/// channel at one. Tanner Helland's fit, which is close to Unity's within its 1,000 K to 20,000 K range.
	/// </summary>
	public static Vector3 ColorTemperatureToLinear(float kelvin)
	{
		var temperature = Math.Clamp(kelvin, 1000.0f, 40000.0f) / 100.0f;
		float red;
		float green;
		float blue;
		if (temperature <= 66.0f)
		{
			red = 255.0f;
			green = (99.4708025861f * MathF.Log(temperature)) - 161.1195681661f;
			blue = temperature <= 19.0f
				? 0.0f
				: (138.5177312231f * MathF.Log(temperature - 10.0f)) - 305.0447927307f;
		}
		else
		{
			red = 329.698727446f * MathF.Pow(temperature - 60.0f, -0.1332047592f);
			green = 288.1221695283f * MathF.Pow(temperature - 60.0f, -0.0755148492f);
			blue = 255.0f;
		}

		var srgb = Vector3.Clamp(new Vector3(red, green, blue) / 255.0f, Vector3.Zero, Vector3.One);
		var linear = new Vector3(SrgbToLinear(srgb.X), SrgbToLinear(srgb.Y), SrgbToLinear(srgb.Z));
		var brightest = MathF.Max(linear.X, MathF.Max(linear.Y, linear.Z));

		return brightest > 0.0f ? linear / brightest : Vector3.One;
	}

	private static float SrgbToLinear(float value)
	{
		return value <= 0.04045f
			? value / 12.92f
			: MathF.Pow((value + 0.055f) / 1.055f, 2.4f);
	}

	private static void RequireLength(float[] values, int length)
	{
		ArgumentNullException.ThrowIfNull(values);

		if (values.Length != length)
		{
			throw new InvalidDataException($"Expected {length} numbers but found {values.Length}.");
		}
	}
}

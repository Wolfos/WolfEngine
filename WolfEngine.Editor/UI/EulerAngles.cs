using System.Numerics;

namespace WolfEngine.Editor.UI;

/// <summary>
/// Euler angles in degrees for transform fields. Every inspector that shows a rotation uses these, so
/// the same rotation reads the same everywhere. Storage is always a quaternion; Euler values are for
/// display and editing only and can jump near gimbal lock.
/// </summary>
internal static class EulerAngles
{
	public static Vector3 ToDegrees(Quaternion rotation)
	{
		rotation = Quaternion.Normalize(rotation);

		var sinPitch = 2.0f * (rotation.W * rotation.X - rotation.Y * rotation.Z);
		sinPitch = Math.Clamp(sinPitch, -1.0f, 1.0f);
		var pitch = MathF.Asin(sinPitch);

		var yaw = MathF.Atan2(
			2.0f * (rotation.W * rotation.Y + rotation.Z * rotation.X),
			1.0f - 2.0f * (rotation.X * rotation.X + rotation.Y * rotation.Y));

		var roll = MathF.Atan2(
			2.0f * (rotation.W * rotation.Z + rotation.X * rotation.Y),
			1.0f - 2.0f * (rotation.Z * rotation.Z + rotation.X * rotation.X));

		return new Vector3(
			WrapDegrees(RadiansToDegrees(pitch)),
			WrapDegrees(RadiansToDegrees(yaw)),
			WrapDegrees(RadiansToDegrees(roll)));
	}

	public static Quaternion FromDegrees(Vector3 eulerDegrees)
	{
		var pitch = DegreesToRadians(eulerDegrees.X);
		var yaw = DegreesToRadians(eulerDegrees.Y);
		var roll = DegreesToRadians(eulerDegrees.Z);
		return Quaternion.CreateFromYawPitchRoll(yaw, pitch, roll);
	}

	private static float DegreesToRadians(float degrees) => degrees * (MathF.PI / 180.0f);

	private static float RadiansToDegrees(float radians) => radians * (180.0f / MathF.PI);

	private static float WrapDegrees(float degrees)
	{
		degrees %= 360.0f;
		if (degrees > 180.0f)
		{
			degrees -= 360.0f;
		}
		else if (degrees < -180.0f)
		{
			degrees += 360.0f;
		}

		return degrees;
	}
}

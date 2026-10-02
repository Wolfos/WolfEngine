using System.Numerics;

namespace WolfEngine.Mathematics;

/// <summary>A world-space ray with a unit-length direction when initialized with its constructor.</summary>
/// <remarks>The default value is invalid; check <see cref="IsValid"/> before using it.</remarks>
public readonly struct Ray
{
	public Ray(Vector3 origin, Vector3 direction)
	{
		if (IsFinite(origin) == false)
		{
			throw new ArgumentOutOfRangeException(nameof(origin));
		}

		if (IsFinite(direction) == false)
		{
			throw new ArgumentOutOfRangeException(nameof(direction));
		}
		var scale = MathF.Max(MathF.Abs(direction.X), MathF.Max(MathF.Abs(direction.Y), MathF.Abs(direction.Z)));
		if (scale == 0.0f)
		{
			throw new ArgumentOutOfRangeException(nameof(direction));
		}

		Origin = origin;
		Direction = Vector3.Normalize(direction / scale);
	}

	public Vector3 Origin { get; }
	public Vector3 Direction { get; }
	public bool IsValid => IsFinite(Origin) && IsFinite(Direction) && MathF.Abs(Direction.LengthSquared() - 1.0f) <= 1e-4f;

	public Vector3 GetPoint(float distance) => Origin + Direction * distance;

	private static bool IsFinite(Vector3 value) =>
		float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

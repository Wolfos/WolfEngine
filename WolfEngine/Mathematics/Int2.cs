using System.Text.Json.Serialization;

namespace WolfEngine.Mathematics;

/// <summary>
/// Simple integer vector to avoid leaking external math types.
/// </summary>
public readonly struct Int2 : IEquatable<Int2>
{
	[JsonConstructor]
	public Int2(int x, int y)
	{
		X = x;
		Y = y;
	}

	public int X { get; }

	public int Y { get; }

	public static Int2 Zero => new(0, 0);

	public bool Equals(Int2 other) => X == other.X && Y == other.Y;

	public override bool Equals(object? obj) => obj is Int2 other && Equals(other);

	public override int GetHashCode() => HashCode.Combine(X, Y);

	public override string ToString() => $"({X}, {Y})";

	public static bool operator ==(Int2 left, Int2 right) => left.Equals(right);

	public static bool operator !=(Int2 left, Int2 right) => left.Equals(right) == false;
}

namespace WolfEngine.Rendering;

public readonly struct ComputeThreadGroupSize : IEquatable<ComputeThreadGroupSize>
{
	public ComputeThreadGroupSize(uint x, uint y, uint z)
	{
		if (x == 0 || y == 0 || z == 0)
		{
			throw new ArgumentOutOfRangeException(nameof(x), "Compute threadgroup dimensions must be greater than zero.");
		}

		X = x;
		Y = y;
		Z = z;
	}

	public uint X { get; }

	public uint Y { get; }

	public uint Z { get; }

	public (uint X, uint Y, uint Z) GetDispatchGroupCount(uint workItemCountX, uint workItemCountY = 1, uint workItemCountZ = 1)
	{
		return (
			DivideRoundUp(workItemCountX, X),
			DivideRoundUp(workItemCountY, Y),
			DivideRoundUp(workItemCountZ, Z));
	}

	public bool Equals(ComputeThreadGroupSize other) => X == other.X && Y == other.Y && Z == other.Z;

	public override bool Equals(object? obj) => obj is ComputeThreadGroupSize other && Equals(other);

	public override int GetHashCode() => HashCode.Combine(X, Y, Z);

	private static uint DivideRoundUp(uint value, uint divisor)
	{
		if (divisor == 0)
		{
			throw new DivideByZeroException("Threadgroup divisor cannot be zero.");
		}

		return value == 0 ? 0 : 1 + ((value - 1) / divisor);
	}
}

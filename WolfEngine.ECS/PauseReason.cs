using System.Numerics;

namespace WolfEngine.ECS;

/// <summary>
/// One of the 32 reasons a Game world can be paused for, such as an open menu. A reason is always exactly one bit of
/// <see cref="World.PauseReasons"/>, so two reasons can never be combined into a value that also reads as a third.
/// Declare each reason once by its bit index, for example
/// <c>public static readonly PauseReason Inventory = new(0);</c>.
/// </summary>
public readonly struct PauseReason : IEquatable<PauseReason>
{
	/// <summary>How many distinct reasons <see cref="World.PauseReasons"/> can hold.</summary>
	public const int Count = 32;

	/// <summary>The reason stored in bit <paramref name="index"/>, from 0 to 31.</summary>
	public PauseReason(int index)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(index);
		ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);

		Mask = 1u << index;
	}

	/// <summary>
	/// This reason's bit in <see cref="World.PauseReasons"/>. Zero only for <c>default(PauseReason)</c> or
	/// <c>new PauseReason()</c>, which aren't reasons and are rejected by <see cref="World.Pause"/> and
	/// <see cref="World.Resume"/>.
	/// </summary>
	public uint Mask { get; }

	/// <summary>Which bit this reason is, from 0 to 31.</summary>
	public int Index => BitOperations.TrailingZeroCount(Mask);

	public bool Equals(PauseReason other) => Mask == other.Mask;

	public override bool Equals(object? obj) => obj is PauseReason other && Equals(other);

	public override int GetHashCode() => (int)Mask;

	public override string ToString() => Mask == 0 ? "PauseReason(none)" : $"PauseReason({Index})";

	public static bool operator ==(PauseReason left, PauseReason right) => left.Equals(right);

	public static bool operator !=(PauseReason left, PauseReason right) => left.Equals(right) == false;
}

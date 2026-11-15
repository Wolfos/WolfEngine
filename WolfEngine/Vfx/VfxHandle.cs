namespace WolfEngine.Vfx;

/// <summary>
/// Identifies one playing effect. A handle is safe to keep: once the effect finishes or is stopped it simply stops
/// matching anything, and every call that takes it ignores it.
/// </summary>
public readonly struct VfxHandle : IEquatable<VfxHandle>
{
	internal VfxHandle(VfxWorldRuntime runtime, int slot, int generation)
	{
		Runtime = runtime;
		Slot = slot;
		Generation = generation;
	}

	internal VfxWorldRuntime? Runtime { get; }
	internal int Slot { get; }
	internal int Generation { get; }

	public static VfxHandle Invalid => default;

	/// <summary>Whether the handle was ever issued. Use <see cref="IVfxService.IsPlaying"/> to ask whether it still plays.</summary>
	public bool IsValid => Runtime is not null;

	public bool Equals(VfxHandle other) =>
		ReferenceEquals(Runtime, other.Runtime) &&
		Slot == other.Slot &&
		Generation == other.Generation;

	public override bool Equals(object? obj) => obj is VfxHandle other && Equals(other);

	public override int GetHashCode() => HashCode.Combine(Runtime, Slot, Generation);

	public static bool operator ==(VfxHandle left, VfxHandle right) => left.Equals(right);

	public static bool operator !=(VfxHandle left, VfxHandle right) => left.Equals(right) == false;
}

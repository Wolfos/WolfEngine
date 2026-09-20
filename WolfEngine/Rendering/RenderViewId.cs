namespace WolfEngine.Rendering;

/// <summary>
/// Identifies one rendered view: a world, a camera, a target, and the persistent GPU state derived from
/// that pairing. Ids are stable for the life of the view and are never reused, so per-view state can be
/// keyed by one without a stale id resolving to a later view.
/// </summary>
/// <remarks>
/// While only one view exists, everything uses <see cref="Primary"/>. The type is introduced ahead of that
/// so per-view state has something to be keyed by, rather than being retrofitted onto call sites later.
/// </remarks>
public readonly record struct RenderViewId(int Value) : IComparable<RenderViewId>
{
	/// <summary>No view. Distinguishable from <see cref="Primary"/>, which is a real view.</summary>
	public static RenderViewId None => new(0);

	/// <summary>
	/// The view the editor's main scene viewport and the standalone game both render through. It is an
	/// ordinary view; nothing about it is privileged beyond being the one that always exists.
	/// </summary>
	public static RenderViewId Primary => new(1);

	public bool IsValid => Value > 0;

	/// <summary>Zero-based index into per-view arrays and the UI sentinel range.</summary>
	public int Index => Value - 1;

	public static RenderViewId FromIndex(int index)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(index);
		return new RenderViewId(index + 1);
	}

	public int CompareTo(RenderViewId other) => Value.CompareTo(other.Value);

	public override string ToString() => IsValid ? $"view{Value}" : "view:none";
}

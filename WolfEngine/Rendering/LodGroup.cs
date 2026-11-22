using System.Numerics;
using System.Text.Json.Serialization;
using WolfEngine.ECS;

namespace WolfEngine;

/// <summary>
/// Picks one level of detail for every mesh renderer under this entity, so the parts of an object made of several
/// renderers switch together: a tree's trunk and leaves, and the billboard that replaces them both. The level is
/// measured on the group's bounds rather than each renderer's own. Renderers without a <see cref="LodGroupMember"/>
/// take part from level 0 on. See docs/mesh-lod.md.
/// </summary>
public struct LodGroup : IEntityComponent
{
	public LodGroup()
	{
	}

	/// <summary>
	/// One threshold per level transition, decreasing: level <c>n + 1</c> draws once the bounds cover less than
	/// <c>ScreenSizes[n]</c> of the screen height. Up to seven, for eight levels.
	/// </summary>
	public float[] ScreenSizes = [];

	/// <summary>Centre of the group's bounds, in this entity's local space.</summary>
	public Vector3 BoundsCenter;

	/// <summary>Radius of the group's bounds, in this entity's local space. Zero leaves the group inactive.</summary>
	public float BoundsRadius;

	/// <summary>Number of levels the thresholds describe, level 0 included.</summary>
	[JsonIgnore]
	public readonly int LevelCount => (ScreenSizes?.Length ?? 0) + 1;

	[JsonIgnore]
	public readonly bool IsValid => BoundsRadius > 0.0f && float.IsFinite(BoundsRadius) && ScreenSizes is { Length: > 0 };
}

/// <summary>
/// The levels of its <see cref="LodGroup"/> a mesh renderer draws in. At group level <c>n</c> within the range it draws
/// its mesh's level <c>n - FirstLevel</c>, clamped to the mesh's last level, so a renderer whose mesh has fewer levels
/// keeps drawing its coarsest. Outside the range it is not drawn.
/// </summary>
public struct LodGroupMember : IEntityComponent
{
	public LodGroupMember()
	{
	}

	public int FirstLevel;

	/// <summary>The last group level drawn in. Below zero means every level from <see cref="FirstLevel"/> on.</summary>
	public int LastLevel = -1;
}

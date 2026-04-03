using WolfEngine.ECS;

namespace WolfEngine.Physics;

public struct CollisionFilter : IEntityComponent
{
	public const uint DefaultLayer = 0;
	public const uint DefaultLayerMask = uint.MaxValue;
	public const uint MaxLayer = 31;

	public uint Layer;
	public uint CollidesWith;

	public void ApplyDefaultValues()
	{
		Layer = DefaultLayer;
		CollidesWith = DefaultLayerMask;
	}

	public static CollisionFilter CreateDefault()
	{
		var filter = new CollisionFilter();
		filter.ApplyDefaultValues();
		return filter;
	}
}

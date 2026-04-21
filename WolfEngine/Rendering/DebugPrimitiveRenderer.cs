using System;
using WolfEngine.ECS;

namespace WolfEngine.Rendering;

public enum DebugPrimitiveType
{
	Box = 0,
	Sphere = 1,
	Quad = 2
}

public struct DebugPrimitiveRenderer : IEntityComponent
{
	public DebugPrimitiveType PrimitiveType;
	public ColorRGBA Tint;
	public AlphaMode AlphaMode;

	public void ApplyDefaultValues(World world, Entity entity)
	{
		_ = world;
		_ = entity;
		PrimitiveType = DebugPrimitiveType.Box;
		Tint = ColorRGBA.White;
		AlphaMode = AlphaMode.Opaque;
	}

	public AlphaMode GetResolvedAlphaMode() => AlphaMode == AlphaMode.AlphaBlend
		? AlphaMode.AlphaBlend
		: AlphaMode.Opaque;

	public DebugPrimitiveType GetResolvedPrimitiveType()
	{
		return Enum.IsDefined(PrimitiveType)
			? PrimitiveType
			: DebugPrimitiveType.Box;
	}
}

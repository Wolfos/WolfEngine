using System.Numerics;
using WolfEngine.ECS;

namespace WolfEngine.Rendering;

public struct Light: IEntityComponent
{
	public LightType Type;
	public float Intensity;
	public Vector4 Color;
}

public enum LightType
{
	Directional, Point
}
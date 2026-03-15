using WolfEngine.ECS;

namespace WolfEngine.Rendering;

public struct Light: IEntityComponent
{
	public LightType Type;
	public float Intensity;
	public ColorRGBA Color;
	public bool HorizonFade;
}

public enum LightType
{
	Directional, Point
}

using WolfEngine.ECS;

namespace WolfEngine.Rendering;

[DefaultIcon("light")]
public struct Light: IEntityComponent
{
	public LightType Type;
	public float Intensity;
	public float Range;
	public ColorRGBA Color;
	public bool HorizonFade;
}

public enum LightType
{
	Directional, Point
}

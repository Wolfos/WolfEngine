using System.Numerics;
using WolfEngine.ECS;
using WolfEngine.Mathematics;

namespace WolfEngine;

public struct Camera: IEntityComponent
{
	public Matrix4x4 Perspective { get; private set; }
	public Int2 ScreenResolution;
	public float Fov;
	
	
	public void SetPerspective(float fov)
	{
		Fov = fov;
		fov = float.DegreesToRadians(fov);
		
		Perspective =
			Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(fov, (float)ScreenResolution.X / (float)ScreenResolution.Y, 0.03f,
				10000.0f);
	}
}

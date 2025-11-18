using System.Numerics;
using WolfEngine.ECS;

namespace WolfEngine;

public struct Camera: IEntityComponent
{
	public Matrix4x4 Perspective { get; private set; }

	public int ScreenResolutionX;
	public int ScreenResolutionY;
	
	public void SetPerspective(float fov)
	{
		fov = float.DegreesToRadians(fov);
		Perspective =
			Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(fov, (float)ScreenResolutionX / (float)ScreenResolutionY, 0.03f,
				10000.0f);
	}
}

using System.Numerics;

namespace WolfEngine;

public class Camera
{
	public Matrix4x4 Transform { get; set; }
	public Matrix4x4 Perspective { get; private set; }
	public Vector3 Position { get; set; }

	public int ScreenResolutionX { get; set; }
	public int ScreenResolutionY { get; set; }

	public void SetPerspective(float fov)
	{
		fov = float.DegreesToRadians(fov);
		Perspective =
			Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(fov, (float)ScreenResolutionX / (float)ScreenResolutionY, 0.03f,
				10000.0f);
	}
}

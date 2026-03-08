using System.Numerics;
using WolfEngine.ECS;
using WolfEngine.Mathematics;

namespace WolfEngine;

public struct Camera: IEntityComponent
{
	public Matrix4x4 Perspective { get; private set; }
	public Int2 ScreenResolution;
	public float Fov;
	private byte _autoResolutionState;

	public bool AutoResolution
	{
		get => _autoResolutionState != 1;
		set => _autoResolutionState = value ? (byte)2 : (byte)1;
	}
	
	
	public void SetPerspective(float fov)
	{
		if (fov < 1)
		{
			return;
		}
		
		Fov = fov;
		fov = float.DegreesToRadians(fov);
		
		Perspective =
			Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(fov, (float)ScreenResolution.X / (float)ScreenResolution.Y, 0.03f,
				10000.0f);
	}
}

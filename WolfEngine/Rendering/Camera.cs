using System.Numerics;
using WolfEngine.ECS;
using WolfEngine.Mathematics;

namespace WolfEngine;

public struct Camera: IEntityComponent
{
	public const float DefaultNearPlane = 0.03f;
	public const float DefaultFarPlane = 10000.0f;

	public Matrix4x4 Perspective { get; private set; }
	public Int2 ScreenResolution;
	public float Fov;
	public float NearPlane;
	public float FarPlane;
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
		if (NearPlane <= 0.0f)
		{
			NearPlane = DefaultNearPlane;
		}

		if (FarPlane <= NearPlane)
		{
			FarPlane = DefaultFarPlane;
		}
		
		Perspective =
			Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(
				fov,
				(float)ScreenResolution.X / (float)ScreenResolution.Y,
				NearPlane,
				FarPlane);
	}
}

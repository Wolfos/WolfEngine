using System.Numerics;
using System.Text.Json.Serialization;
using WolfEngine.ECS;
using WolfEngine.Mathematics;

namespace WolfEngine;

public struct Camera: IEntityComponent, IJsonOnDeserialized
{
	public const float DefaultNearPlane = 0.03f;
	public const float DefaultFarPlane = 10000.0f;

	[JsonIgnore]
	public Matrix4x4 Perspective { get; private set; }
	public Int2 ScreenResolution;
	public float Fov;
	public float NearPlane;
	public float FarPlane;

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

		ScreenResolution = new Int2(
			Math.Max(ScreenResolution.X, 1),
			Math.Max(ScreenResolution.Y, 1));

		Perspective =
			Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(
				fov,
				(float)ScreenResolution.X / (float)ScreenResolution.Y,
				NearPlane,
				FarPlane);
	}

	public void OnDeserialized()
	{
		ScreenResolution = new Int2(
			Math.Max(ScreenResolution.X, 1),
			Math.Max(ScreenResolution.Y, 1));
		SetPerspective(Fov > 0.0f ? Fov : 70.0f);
	}
}

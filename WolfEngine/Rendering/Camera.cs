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

	public Matrix4x4 GetPerspective(Int2 targetSize)
	{
		var width = Math.Max(targetSize.X, 1);
		var height = Math.Max(targetSize.Y, 1);
		var nearPlane = NearPlane > 0.0f ? NearPlane : DefaultNearPlane;
		var farPlane = FarPlane > nearPlane ? FarPlane : DefaultFarPlane;
		return Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(
			float.DegreesToRadians(Fov < 1.0f ? 70.0f : Fov),
			width / (float)height,
			nearPlane,
			farPlane);
	}

	/// <summary>
	/// Builds a world-space ray from normalized viewport coordinates using this camera's current projection
	/// settings and world transform. Coordinates use a top-left origin and extrapolate outside 0..1.
	/// </summary>
	/// <remarks>Returns an invalid default ray when the camera, transform, or viewport point is invalid.</remarks>
	public Ray ViewportPointToRay(Vector2 viewportPoint, in WorldTransform cameraWorldTransform)
	{
		TryViewportPointToRay(viewportPoint, cameraWorldTransform, out var ray);
		return ray;
	}

	/// <summary>Tries to build a ray from this camera's current projection settings and world transform.</summary>
	public bool TryViewportPointToRay(
		Vector2 viewportPoint,
		in WorldTransform cameraWorldTransform,
		out Ray ray)
	{
		ray = default;
		if (ScreenResolution.X <= 0 || ScreenResolution.Y <= 0 ||
		    float.IsFinite(Fov) == false || float.IsFinite(NearPlane) == false || float.IsFinite(FarPlane) == false ||
		    Matrix4x4.Invert(cameraWorldTransform.LocalToWorld, out var view) == false)
		{
			return false;
		}

		var effectiveFov = Fov < 1.0f ? 70.0f : Fov;
		var effectiveFovRadians = float.DegreesToRadians(effectiveFov);
		var effectiveNearPlane = NearPlane > 0.0f ? NearPlane : DefaultNearPlane;
		var effectiveFarPlane = FarPlane > effectiveNearPlane ? FarPlane : DefaultFarPlane;
		if (effectiveFov <= 0.0f || effectiveFov >= 180.0f ||
		    float.IsFinite(effectiveFovRadians) == false || effectiveFovRadians <= 0.0f || effectiveFovRadians >= MathF.PI ||
		    effectiveNearPlane <= 0.0f || effectiveFarPlane <= effectiveNearPlane ||
		    ViewProjection.TryCreate(view, GetPerspective(ScreenResolution), out var viewProjection) == false)
		{
			return false;
		}

		return viewProjection.TryViewportPointToRay(viewportPoint, out ray);
	}

	public void OnDeserialized()
	{
		ScreenResolution = new Int2(
			Math.Max(ScreenResolution.X, 1),
			Math.Max(ScreenResolution.Y, 1));
		SetPerspective(Fov > 0.0f ? Fov : 70.0f);
	}
	
	public void ApplyDefaultValues(World world, Entity entity)
	{
		Fov = 70;
		NearPlane = DefaultNearPlane;
		FarPlane = DefaultFarPlane;
	}
}

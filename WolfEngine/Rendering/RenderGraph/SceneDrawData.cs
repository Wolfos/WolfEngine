#nullable enable

using System;
using System.Collections.Generic;
using System.Numerics;
using WolfEngine.ECS;
using WolfEngine.Mathematics;

namespace WolfEngine.Rendering;

/// <summary>
/// Holds the scene data (camera and draw commands) needed for rendering a frame.
/// </summary>
public sealed class SceneDrawData
{
	public SceneDrawData(
		Matrix4x4 viewMatrix,
		Matrix4x4 viewProjection,
		Matrix4x4 unjitteredProjection,
		Matrix4x4 unjitteredViewProjection,
		Matrix4x4 previousProjection,
		Matrix4x4 previousViewProjection,
		Matrix4x4 inverseProjection,
		Matrix4x4 inverseViewProjection,
		Vector3 cameraOrigin,
		Vector3 previousCameraOrigin,
		Int2 sceneFramebufferSize,
		float nearPlane,
		float farPlane,
		Vector2 jitterPixels,
		Vector2 previousJitterPixels,
		Vector2 jitterNdc,
		bool resetHistory,
		IReadOnlyList<LightPacket> lights,
		IReadOnlyList<DecalProjectorPacket> decals)
	{
		ViewMatrix = viewMatrix;
		ViewProjection = viewProjection;
		UnjitteredProjection = unjitteredProjection;
		UnjitteredViewProjection = unjitteredViewProjection;
		PreviousProjection = previousProjection;
		PreviousViewProjection = previousViewProjection;
		InverseProjection = inverseProjection;
		InverseViewProjection = inverseViewProjection;
		CameraOrigin = cameraOrigin;
		PreviousCameraOrigin = previousCameraOrigin;
		SceneFramebufferSize = sceneFramebufferSize;
		NearPlane = nearPlane;
		FarPlane = farPlane;
		JitterPixels = jitterPixels;
		PreviousJitterPixels = previousJitterPixels;
		JitterNdc = jitterNdc;
		ResetHistory = resetHistory;
		Lights = lights ?? throw new ArgumentNullException(nameof(lights));
		Decals = decals ?? throw new ArgumentNullException(nameof(decals));
	}

	public Matrix4x4 ViewMatrix { get; }

	public Matrix4x4 ViewProjection { get; }

	public Matrix4x4 UnjitteredProjection { get; }

	public Matrix4x4 UnjitteredViewProjection { get; }

	public Matrix4x4 PreviousProjection { get; }

	public Matrix4x4 PreviousViewProjection { get; }

	public Matrix4x4 InverseProjection { get; }

	public Matrix4x4 InverseViewProjection { get; }
	
	public Vector3 CameraOrigin { get; }

	public Vector3 PreviousCameraOrigin { get; }

	public Int2 SceneFramebufferSize { get; }

	public float NearPlane { get; }

	public float FarPlane { get; }

	public Vector2 JitterPixels { get; }

	public Vector2 PreviousJitterPixels { get; }

	public Vector2 JitterNdc { get; }

	public bool ResetHistory { get; }

	public IReadOnlyList<LightPacket> Lights { get; }

	public IReadOnlyList<DecalProjectorPacket> Decals { get; }
}

public readonly struct LightPacket
{
	public LightPacket(Light light, Matrix4x4 transform)
	{
		Light = light;
		Transform = transform;
	}

	public Light Light { get; }

	public Matrix4x4 Transform { get; }
}

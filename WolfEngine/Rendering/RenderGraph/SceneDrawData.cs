#nullable enable

using System.Numerics;
using WolfEngine.ECS;

namespace WolfEngine.Rendering;

/// <summary>
/// Holds the scene data (camera and draw commands) needed for rendering a frame.
/// </summary>
public sealed class SceneDrawData
{
	public SceneDrawData(Matrix4x4 viewProjection, Matrix4x4 inverseProjection, Matrix4x4 inverseViewProjection,
		Vector3 cameraOrigin,
		IReadOnlyList<LightPacket> lights)
	{
		ViewProjection = viewProjection;
		InverseProjection = inverseProjection;
		InverseViewProjection = inverseViewProjection;
		CameraOrigin = cameraOrigin;
		Lights = lights ?? throw new ArgumentNullException(nameof(lights));
	}

	public Matrix4x4 ViewProjection { get; }

	public Matrix4x4 InverseProjection { get; }

	public Matrix4x4 InverseViewProjection { get; }
	
	public Vector3 CameraOrigin { get; }

	public IReadOnlyList<LightPacket> Lights { get; }
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

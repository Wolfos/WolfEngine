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
		IReadOnlyList<DrawPacket> drawPackets, IReadOnlyList<LightPacket> lights)
	{
		ViewProjection = viewProjection;
		InverseProjection = inverseProjection;
		InverseViewProjection = inverseViewProjection;
		CameraOrigin = cameraOrigin;
		DrawPackets = drawPackets ?? throw new ArgumentNullException(nameof(drawPackets));
		Lights = lights ?? throw new ArgumentNullException(nameof(lights));
	}

	public Matrix4x4 ViewProjection { get; }

	public Matrix4x4 InverseProjection { get; }

	public Matrix4x4 InverseViewProjection { get; }
	
	public Vector3 CameraOrigin { get; }
	
	public IReadOnlyList<DrawPacket> DrawPackets { get; }

	public IReadOnlyList<LightPacket> Lights { get; }
}

/// <summary>
/// A single draw command containing mesh, material, and transform.
/// Backend-agnostic - doesn't reference D3D12 or Metal types.
/// </summary>
public readonly struct DrawPacket
{
	public DrawPacket(Mesh mesh, Material material, Matrix4x4 transform)
	{
		Mesh = mesh ?? throw new ArgumentNullException(nameof(mesh));
		Material = material ?? throw new ArgumentNullException(nameof(material));
		Transform = transform;
	}

	public Mesh Mesh { get; }
	
	public Material Material { get; }
	
	public Matrix4x4 Transform { get; }
}

public readonly struct LightPacket
{
	public LightPacket(Light light, LocalTransform localTransform)
	{
		Light = light;
		LocalTransform = localTransform;
	}

	public Light Light { get; }

	public LocalTransform LocalTransform { get; }
}

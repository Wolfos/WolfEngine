#nullable enable

using System.Numerics;

namespace WolfEngine.Rendering;

/// <summary>
/// Holds the scene data (camera and draw commands) needed for rendering a frame.
/// </summary>
public sealed class SceneDrawData
{
	public SceneDrawData(Matrix4x4 viewProjection, Vector3 cameraPosition, IReadOnlyList<DrawPacket> drawPackets)
	{
		ViewProjection = viewProjection;
		CameraPosition = cameraPosition;
		DrawPackets = drawPackets ?? throw new ArgumentNullException(nameof(drawPackets));
	}

	public Matrix4x4 ViewProjection { get; }
	
	public Vector3 CameraPosition { get; }
	
	public IReadOnlyList<DrawPacket> DrawPackets { get; }
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


#nullable enable

using System.Numerics;
using WolfEngine.ECS;

namespace WolfEngine.Rendering;

/// <summary>
/// Reusable frame data handed from the game thread to the render thread.
/// </summary>
public sealed class FrameSnapshot
{
	public FrameSnapshot()
	{
		DrawPackets = new List<DrawPacket>(128);
	}

	public Camera Camera { get; private set; }
	public Transform CameraTransform { get; private set; }
	public List<DrawPacket> DrawPackets { get; }

	public void SetCamera(Camera camera, Transform transform)
	{
		Camera = camera;
		CameraTransform = transform;
	}

	public void Clear()
	{
		DrawPackets.Clear();
	}

	public void AddDraw(Mesh mesh, Material material, Matrix4x4 transform)
	{
		DrawPackets.Add(new DrawPacket(mesh, material, transform));
	}

	public readonly struct DrawPacket
	{
		public DrawPacket(Mesh mesh, Material material, Matrix4x4 transform)
		{
			Mesh = mesh;
			Material = material;
			Transform = transform;
		}

		public Mesh Mesh { get; }
		public Material Material { get; }
		public Matrix4x4 Transform { get; }
	}
}

public sealed class FrameSnapshotBuffer
{
	private readonly FrameSnapshot[] _buffers = { new(), new() };
	private int _readIndex;
	private int _writeIndex = 1;
	private bool _hasSnapshot;

	public FrameSnapshot BeginWrite()
	{
		var snapshot = _buffers[_writeIndex];
		snapshot.Clear();
		return snapshot;
	}

	public void PublishWrite()
	{
		(_readIndex, _writeIndex) = (_writeIndex, _readIndex);
		_hasSnapshot = true;
	}

	public bool TryConsumeLatest(out FrameSnapshot snapshot)
	{
		if (_hasSnapshot == false)
		{
			snapshot = _buffers[_readIndex];
			return false;
		}

		snapshot = _buffers[_readIndex];
		return true;
	}
}

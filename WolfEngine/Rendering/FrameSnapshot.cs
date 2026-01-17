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
		DrawPackets = new List<DrawPacket>(4096);
		LightPackets = new List<LightPacket>(16);
	}

	public Camera Camera { get; private set; }
	public WorldTransform CameraWorldTransform { get; private set; }
	public List<DrawPacket> DrawPackets { get; }
	public List<LightPacket> LightPackets { get; }

	public void SetCamera(Camera camera, WorldTransform worldTransform)
	{
		Camera = camera;
		CameraWorldTransform = worldTransform;
	}

	public void Clear()
	{
		DrawPackets.Clear();
		LightPackets.Clear();
	}

	public void AddDraw(Mesh mesh, Material material, Matrix4x4 transform)
	{
		DrawPackets.Add(new DrawPacket(mesh, material, transform));
	}

	public void AddLight(Light light, Matrix4x4 transform)
	{
		LightPackets.Add(new LightPacket(light, transform));
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
}

public sealed class FrameSnapshotBuffer
{
	private readonly FrameSnapshot[] _buffers = { new(), new() };
	private readonly object _lock = new();
	private readonly ManualResetEventSlim _slotFree = new(true);
	private int _readIndex;
	private int _writeIndex = 1;
	private bool _hasPending;

	public FrameSnapshot BeginWrite()
	{
		_slotFree.Wait();
		lock (_lock)
		{
			var snapshot = _buffers[_writeIndex];
			snapshot.Clear();
			return snapshot;
		}
	}

	public void PublishWrite()
	{
		lock (_lock)
		{
			(_readIndex, _writeIndex) = (_writeIndex, _readIndex);
			_hasPending = true;
			_slotFree.Reset();
		}
	}

	public bool TryConsumeLatest(out FrameSnapshot snapshot)
	{
		lock (_lock)
		{
			if (_hasPending == false)
			{
				snapshot = _buffers[_readIndex];
				return false;
			}

			snapshot = _buffers[_readIndex];
			_hasPending = false;
			_slotFree.Set();
			return true;
		}
	}
}

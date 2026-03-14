#nullable enable

using System.Numerics;
using WolfEngine.ECS;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Rendering;

/// <summary>
/// Reusable frame data handed from the game thread to the render thread.
/// </summary>
public sealed class FrameSnapshot
{
	private static readonly Vector3 DefaultSunDirection = Vector3.Normalize(new Vector3(0.2f, 0.9f, 0.3f));

	public FrameSnapshot()
	{
		LightPackets = new List<LightPacket>(16);
		SunDirection = DefaultSunDirection;
		Config = new();
	}

	public Camera Camera { get; private set; }
	public WorldTransform CameraWorldTransform { get; private set; }
	public List<LightPacket> LightPackets { get; }
	public Vector3 SunDirection { get; private set; }
	public RenderConfig Config { get; private set; }

	public void SetCamera(Camera camera, WorldTransform worldTransform)
	{
		Camera = camera;
		CameraWorldTransform = worldTransform;
	}

	public void Clear()
	{
		LightPackets.Clear();
		SunDirection = DefaultSunDirection;
	}

	public void AddLight(Light light, Matrix4x4 transform)
	{
		LightPackets.Add(new LightPacket(light, transform));
	}

	public void SetSunDirection(Vector3 sunDirection)
	{
		SunDirection = sunDirection == Vector3.Zero
			? DefaultSunDirection
			: Vector3.Normalize(sunDirection);
	}

	public void SetConfig(RenderConfig config)
	{
		Config.VBAOConfig = config.VBAOConfig;
		Config.SkyboxConfig = config.SkyboxConfig;
		Config.TemporalAntiAliasing = config.TemporalAntiAliasing;
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

#nullable enable

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
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
		DecalPackets = new List<DecalProjectorPacket>(16);
		SkinningPackets = new List<SkinningPacket>(8);
		SunDirection = DefaultSunDirection;
		SunIntensityScale = 1.0f;
		Config = new();
		GpuDrawDatabase = new GpuDrawDatabase();
	}

	public Camera Camera { get; private set; }
	public WorldTransform CameraWorldTransform { get; private set; }
	public Camera PreviousCamera { get; private set; }
	public WorldTransform PreviousCameraWorldTransform { get; private set; }
	public bool HasPreviousCameraState { get; private set; }
	public List<LightPacket> LightPackets { get; }
	public List<DecalProjectorPacket> DecalPackets { get; }

	/// <summary>Skinned instances the render thread must deform this frame.</summary>
	public List<SkinningPacket> SkinningPackets { get; }
	public Vector3 SunDirection { get; private set; }
	public float SunIntensityScale { get; private set; }
	public RenderConfig Config { get; private set; }
	public GpuDrawDatabase GpuDrawDatabase { get; }
	private bool _hasCameraState;

	public void SetCamera(Camera camera, WorldTransform worldTransform)
	{
		if (_hasCameraState)
		{
			PreviousCamera = Camera;
			PreviousCameraWorldTransform = CameraWorldTransform;
			HasPreviousCameraState = true;
		}
		else
		{
			PreviousCamera = camera;
			PreviousCameraWorldTransform = worldTransform;
			HasPreviousCameraState = false;
		}

		Camera = camera;
		CameraWorldTransform = worldTransform;
		_hasCameraState = true;
	}

	public void Clear()
	{
		LightPackets.Clear();
		DecalPackets.Clear();
		SkinningPackets.Clear();
		SunDirection = DefaultSunDirection;
		SunIntensityScale = 1.0f;
		GpuDrawDatabase.ResetForSnapshotWrite();
	}

	/// <summary>
	/// Records a skinned instance to deform. The bone matrices are copied rather than referenced:
	/// this is the game-thread-to-render-thread handoff, and the animator will keep mutating its
	/// own array while the render thread is reading.
	/// </summary>
	public void AddSkinning(Mesh sourceMesh, Mesh instanceMesh, ReadOnlySpan<Matrix4x4> boneMatrices)
	{
		if (boneMatrices.IsEmpty)
		{
			return;
		}

		var copy = boneMatrices.ToArray();
		SkinningPackets.Add(new SkinningPacket(sourceMesh, instanceMesh, copy, copy.Length));
	}

	public void AddLight(Light light, Matrix4x4 transform)
	{
		LightPackets.Add(new LightPacket(light, transform));
	}

	public void AddDecal(DecalProjector projector, Matrix4x4 transform)
	{
		DecalPackets.Add(new DecalProjectorPacket(projector, transform));
	}

	public void SetSun(Vector3 sunDirection, float sunIntensityScale)
	{
		SunDirection = sunDirection == Vector3.Zero
			? DefaultSunDirection
			: Vector3.Normalize(sunDirection);
		SunIntensityScale = Math.Clamp(sunIntensityScale, 0.0f, 1.0f);
	}

	public void SetConfig(RenderConfig config)
	{
		Config.AmbientOcclusion = config.AmbientOcclusion;
		Config.Reflections = config.Reflections;
		Config.DiffuseGlobalIllumination = config.DiffuseGlobalIllumination;
		Config.ShadowMaps = config.ShadowMaps;
		Config.SkyboxConfig = config.SkyboxConfig;
		Config.Fsr3 = config.Fsr3;
		Config.Tonemapping = config.Tonemapping;
		Config.Bloom = config.Bloom;
		Config.Decals = config.Decals;
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
	private bool _completed;

	public bool TryBeginWrite(out FrameSnapshot snapshot)
	{
		_slotFree.Wait();
		lock (_lock)
		{
			if (_completed)
			{
				snapshot = null!;
				return false;
			}

			snapshot = _buffers[_writeIndex];
			snapshot.Clear();
			return true;
		}
	}

	public bool TryPublishWrite()
	{
		lock (_lock)
		{
			if (_completed)
			{
				return false;
			}

			(_readIndex, _writeIndex) = (_writeIndex, _readIndex);
			_hasPending = true;
			_slotFree.Reset();
			return true;
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

	public void Complete()
	{
		lock (_lock)
		{
			if (_completed)
			{
				return;
			}

			_completed = true;
			_slotFree.Set();
		}
	}
}

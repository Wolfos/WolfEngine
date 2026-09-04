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
		: this(new GpuDrawTransformHistory())
	{
	}

	/// <param name="drawTransformHistory">
	/// Previous-frame draw transforms, shared with the other snapshots in the same buffer.
	/// </param>
	internal FrameSnapshot(GpuDrawTransformHistory drawTransformHistory)
	{
		LightPackets = new List<LightPacket>(16);
		DecalPackets = new List<DecalProjectorPacket>(16);
		SkinningPackets = new List<SkinningPacket>(8);
		SunDirection = DefaultSunDirection;
		SunIntensityScale = 1.0f;
		Config = new();
		GpuDrawDatabase = new GpuDrawDatabase(drawTransformHistory);
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

	/// <summary>Packed current and previous bone matrices for this frame.</summary>
	public ReadOnlySpan<Matrix4x4> BoneMatrices => _boneMatrixArena.AsSpan(0, _boneMatrixArenaUsed);
	public Vector3 SunDirection { get; private set; }
	public float SunIntensityScale { get; private set; }
	public RenderConfig Config { get; private set; }
	public GpuDrawDatabase GpuDrawDatabase { get; }
	private bool _hasCameraState;
	private Matrix4x4[] _boneMatrixArena = new Matrix4x4[512];
	private int _boneMatrixArenaUsed;

	public void SetCamera(Camera camera, WorldTransform worldTransform)
	{
		if (HasPreviousCameraState == false)
		{
			PreviousCamera = camera;
			PreviousCameraWorldTransform = worldTransform;
		}

		Camera = camera;
		CameraWorldTransform = worldTransform;
		_hasCameraState = true;
	}

	/// <summary>Takes the previously published frame's camera as this frame's camera history.</summary>
	/// <remarks>
	/// Snapshots rotate, so the camera a snapshot itself last carried is two published frames old. A
	/// motion vector spans one frame, and the camera half of it has to line up with the transform half,
	/// which <see cref="GpuDrawTransformHistory"/> also takes from the previously published frame.
	/// </remarks>
	internal void SeedPreviousCameraFrom(FrameSnapshot published)
	{
		if (ReferenceEquals(published, this) || published._hasCameraState == false)
		{
			return;
		}

		PreviousCamera = published.Camera;
		PreviousCameraWorldTransform = published.CameraWorldTransform;
		HasPreviousCameraState = true;
	}

	public void Clear()
	{
		LightPackets.Clear();
		DecalPackets.Clear();
		SkinningPackets.Clear();
		_boneMatrixArenaUsed = 0;
		SunDirection = DefaultSunDirection;
		SunIntensityScale = 1.0f;
		HasPreviousCameraState = false;
		GpuDrawDatabase.ResetForSnapshotWrite();
	}

	/// <summary>Records current and previous poses for render-thread skinning.</summary>
	public void AddSkinning(
		Mesh sourceMesh,
		Mesh instanceMesh,
		ReadOnlySpan<Matrix4x4> boneMatrices,
		ReadOnlySpan<Matrix4x4> previousBoneMatrices)
	{
		if (boneMatrices.IsEmpty || previousBoneMatrices.Length != boneMatrices.Length)
		{
			return;
		}

		var boneCount = boneMatrices.Length;
		var required = _boneMatrixArenaUsed + (boneCount * 2);
		if (required > _boneMatrixArena.Length)
		{
			Array.Resize(ref _boneMatrixArena, Math.Max(required, _boneMatrixArena.Length * 2));
		}

		var boneMatrixOffset = _boneMatrixArenaUsed;
		var previousBoneMatrixOffset = boneMatrixOffset + boneCount;
		boneMatrices.CopyTo(_boneMatrixArena.AsSpan(boneMatrixOffset));
		previousBoneMatrices.CopyTo(_boneMatrixArena.AsSpan(previousBoneMatrixOffset));
		_boneMatrixArenaUsed = previousBoneMatrixOffset + boneCount;

		SkinningPackets.Add(new SkinningPacket(
			sourceMesh,
			instanceMesh,
			boneMatrixOffset,
			previousBoneMatrixOffset,
			boneCount));
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
		Config.Lighting = config.Lighting;
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
	private readonly FrameSnapshot[] _buffers;
	private readonly object _lock = new();
	private readonly ManualResetEventSlim _slotFree = new(true);
	private int _readIndex;
	private int _writeIndex = 1;
	private bool _hasPending;
	private bool _completed;

	public FrameSnapshotBuffer()
	{
		// One history across both slots: each snapshot's previous-frame state has to describe the frame
		// published before it, not the one that last wrote its own slot.
		var drawTransformHistory = new GpuDrawTransformHistory();
		_buffers = new FrameSnapshot[] { new(drawTransformHistory), new(drawTransformHistory) };
	}

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
			snapshot.SeedPreviousCameraFrom(_buffers[_readIndex]);
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

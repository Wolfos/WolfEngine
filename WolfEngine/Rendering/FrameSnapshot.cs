using System.Numerics;
using WolfEngine.ECS;
using WolfEngine.Rendering.Passes;
using WolfEngine.Vfx;

namespace WolfEngine.Rendering;

/// <summary>
/// Reusable frame data handed from the game thread to the render thread.
/// </summary>
public sealed class RenderViewSnapshot
{
	private static readonly Vector3 DefaultSunDirection = Vector3.Normalize(new Vector3(0.2f, 0.9f, 0.3f));

	/// <param name="drawTransformHistory">
	/// Previous-frame draw transforms, shared with the other snapshots in the same buffer.
	/// </param>
	/// <param name="drawHandles">GPU table slots, shared with the other snapshots in the same buffer.</param>
	internal RenderViewSnapshot(
		RenderViewId view,
		GpuDrawTransformHistory drawTransformHistory,
		GpuDrawHandleRegistry drawHandles)
	{
		View = view;
		LightPackets = new List<LightPacket>(16);
		DecalPackets = new List<DecalProjectorPacket>(16);
		FogVolumePackets = new List<FogVolumePacket>(16);
		SkinningPackets = new List<SkinningPacket>(8);
		OutlinePackets = new List<OutlinePacket>(8);
		SunDirection = DefaultSunDirection;
		SunIntensityScale = 1.0f;
		Config = new();
		GpuDrawDatabase = new GpuDrawDatabase(drawTransformHistory, drawHandles);
	}

	public RenderViewId View { get; }
	internal long BindingGeneration { get; private set; }
	internal World? BoundWorld { get; private set; }

	public Camera Camera { get; private set; }
	public WorldTransform CameraWorldTransform { get; private set; }
	public Camera PreviousCamera { get; private set; }
	public WorldTransform PreviousCameraWorldTransform { get; private set; }
	public bool HasPreviousCameraState { get; private set; }
	public List<LightPacket> LightPackets { get; }
	public List<DecalProjectorPacket> DecalPackets { get; }
	public List<FogVolumePacket> FogVolumePackets { get; }

	/// <summary>Meshes the render thread must outline this frame.</summary>
	public List<OutlinePacket> OutlinePackets { get; }

	/// <summary>Skinned instances the render thread must deform this frame.</summary>
	public List<SkinningPacket> SkinningPackets { get; }

	/// <summary>
	/// The effects of this view's world. Views showing the same world share one frame, so its particles are
	/// simulated once.
	/// </summary>
	internal VfxWorldFrame? VfxFrame { get; set; }

	/// <summary>The render features of this view's world and what their <c>Extract</c> captured.</summary>
	internal IReadOnlyList<Features.RenderFeatureFrame> RenderFeatureFrames => _renderFeatureFrames;

	/// <summary>Packed current and previous bone matrices for this frame.</summary>
	public ReadOnlySpan<Matrix4x4> BoneMatrices => _boneMatrixArena.AsSpan(0, _boneMatrixArenaUsed);
	public Vector3 SunDirection { get; private set; }
	public float SunIntensityScale { get; private set; }
	public RenderConfig Config { get; private set; }

	/// <summary>
	/// <see cref="ColorGradingConfig.LookupTable"/>, resolved on the game thread so the render thread never
	/// touches the asset database. Null when no table is assigned.
	/// </summary>
	public ColorLookupTable? ColorGradingLookupTable { get; private set; }
	public GpuDrawDatabase GpuDrawDatabase { get; }
	private bool _hasCameraState;
	private Matrix4x4[] _boneMatrixArena = new Matrix4x4[512];
	private int _boneMatrixArenaUsed;
	private readonly List<Features.RenderFeatureFrame> _renderFeatureFrames = new(4);
	private readonly Dictionary<Features.RenderFeature, object> _renderFeatureFramePool = new(ReferenceEqualityComparer.Instance);
	private readonly List<Features.RenderFeature> _renderFeaturePoolScratch = new();

	/// <summary>
	/// A view id is a reusable slot. Retire records and camera history when that slot is rebound, even if
	/// the same world is bound again after a destroy/create cycle.
	/// </summary>
	internal void Bind(World world, long bindingGeneration)
	{
		ArgumentNullException.ThrowIfNull(world);
		if (bindingGeneration <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(bindingGeneration));
		}
		if (ReferenceEquals(BoundWorld, world) && BindingGeneration == bindingGeneration)
		{
			return;
		}

		GpuDrawDatabase.BeginSync(reconcilePersistentMeshes: true);
		GpuDrawDatabase.EndSync();
		Camera = default;
		CameraWorldTransform = default;
		PreviousCamera = default;
		PreviousCameraWorldTransform = default;
		HasPreviousCameraState = false;
		_hasCameraState = false;
		BoundWorld = world;
		BindingGeneration = bindingGeneration;
	}

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
	internal void SeedPreviousCameraFrom(RenderViewSnapshot published)
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
		FogVolumePackets.Clear();
		SkinningPackets.Clear();
		OutlinePackets.Clear();
		VfxFrame = null;
		ClearRenderFeatureFrames();
		_boneMatrixArenaUsed = 0;
		SunDirection = DefaultSunDirection;
		SunIntensityScale = 1.0f;
		HasPreviousCameraState = false;
		GpuDrawDatabase.ResetForSnapshotWrite();
	}

	/// <summary>
	/// Game thread: runs each feature's <c>Extract</c> into this snapshot's frame object for it, reusing the object
	/// from the last time this snapshot was written. A feature that throws is disabled and reported.
	/// </summary>
	internal void ExtractRenderFeatures(World world, Features.RenderFeatureRegistry registry)
	{
		var features = registry.GetFeatures(world);
		for (var i = 0; i < features.Count; i++)
		{
			var feature = features[i];
			if (feature.IsRetired || feature.IsFaulted)
			{
				continue;
			}

			try
			{
				if (_renderFeatureFramePool.TryGetValue(feature, out var frame) == false)
				{
					frame = feature.CreateFrame();
					_renderFeatureFramePool[feature] = frame;
				}

				feature.ExtractFrame(world, frame);
				_renderFeatureFrames.Add(new Features.RenderFeatureFrame(feature, frame));
			}
			catch (Exception exception)
			{
				registry.Fault(feature, exception);
			}
		}
	}

	/// <summary>
	/// Drops this snapshot's frames, and the pooled frame objects of features that are gone, so an unloaded
	/// gameplay assembly is not kept alive by a snapshot.
	/// </summary>
	private void ClearRenderFeatureFrames()
	{
		_renderFeatureFrames.Clear();
		if (_renderFeatureFramePool.Count == 0)
		{
			return;
		}

		foreach (var feature in _renderFeatureFramePool.Keys)
		{
			if (feature.IsRetired || feature.IsFaulted)
			{
				_renderFeaturePoolScratch.Add(feature);
			}
		}

		foreach (var feature in _renderFeaturePoolScratch)
		{
			_renderFeatureFramePool.Remove(feature);
		}

		_renderFeaturePoolScratch.Clear();
	}

	/// <summary>Records current and previous poses for render-thread skinning.</summary>
	public void AddSkinning(
		Mesh sourceMesh,
		Mesh instanceMesh,
		ReadOnlySpan<Matrix4x4> boneMatrices,
		ReadOnlySpan<Matrix4x4> previousBoneMatrices, uint poseGeneration = uint.MaxValue)
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
			boneCount, poseGeneration));
	}

	public void AddLight(Light light, Matrix4x4 transform)
	{
		LightPackets.Add(new LightPacket(light, transform));
	}

	public void AddDecal(DecalProjector projector, Matrix4x4 transform)
	{
		DecalPackets.Add(new DecalProjectorPacket(projector, transform));
	}

	public void AddOutline(Mesh mesh, Matrix4x4 transform, ColorRGBA color, float thicknessPixels)
	{
		OutlinePackets.Add(new OutlinePacket(mesh, transform, color, thicknessPixels));
	}

	public void AddFogVolume(FogVolume volume, Matrix4x4 transform)
	{
		FogVolumePackets.Add(new FogVolumePacket(volume, transform));
	}

	public void SetSun(Vector3 sunDirection, float sunIntensityScale)
	{
		SunDirection = sunDirection == Vector3.Zero
			? DefaultSunDirection
			: Vector3.Normalize(sunDirection);
		SunIntensityScale = Math.Clamp(sunIntensityScale, 0.0f, 1.0f);
	}

	/// <summary>
	/// Copies every setting into the snapshot's own config, which the render thread reads while the game thread
	/// may be editing the source. A new <see cref="RenderConfig"/> property has to be added here;
	/// FrameSnapshotConfigTests fails until it is.
	/// </summary>
	public void SetConfig(RenderConfig config)
	{
		Config.RenderScale = config.RenderScale;
		Config.AmbientOcclusion = config.AmbientOcclusion;
		Config.Reflections = config.Reflections;
		Config.DiffuseGlobalIllumination = config.DiffuseGlobalIllumination;
		Config.Lighting = config.Lighting;
		Config.ShadowMaps = config.ShadowMaps;
		Config.VolumetricFog = config.VolumetricFog;
		Config.SkyboxConfig = config.SkyboxConfig;
		Config.AntiAliasing = config.AntiAliasing;
		Config.Tonemapping = config.Tonemapping;
		Config.ColorGrading = config.ColorGrading;
		Config.Bloom = config.Bloom;
		Config.Decals = config.Decals;
		Config.MotionVectorDebug = config.MotionVectorDebug;
	}

	public void SetColorGradingLookupTable(ColorLookupTable? lookupTable)
	{
		ColorGradingLookupTable = lookupTable;
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

/// <summary>
/// Reusable frame data handed from the game thread to the render thread, partitioned by render view.
/// </summary>
/// <remarks>
/// The parameterless members are a compatibility facade for the primary view. New multi-view code should
/// enumerate <see cref="Views"/> or address an entry through <see cref="GetOrCreateView"/>.
/// </remarks>
public sealed class FrameSnapshot
{
	private readonly GpuDrawTransformHistory _drawTransformHistory;
	private readonly GpuDrawHandleRegistry _drawHandles;
	private readonly List<RenderViewSnapshot> _viewEntries = [];
	private readonly List<RenderViewSnapshot> _views = [];
	private readonly List<VfxWorldFrame> _vfxFrames = [];
	private int _vfxFrameCount;
	private FrameSnapshot? _previousPublished;

	public FrameSnapshot()
		: this(new GpuDrawTransformHistory(), new GpuDrawHandleRegistry())
	{
	}

	internal FrameSnapshot(GpuDrawTransformHistory drawTransformHistory, GpuDrawHandleRegistry drawHandles)
	{
		_drawTransformHistory = drawTransformHistory;
		_drawHandles = drawHandles;
		GetOrCreateView(RenderViewId.Primary);
	}

	public IReadOnlyList<RenderViewSnapshot> Views => _views;

	internal RenderViewSnapshot BindView(RenderViewId view, World world, long bindingGeneration)
	{
		var snapshot = GetOrCreateView(view);
		snapshot.Bind(world, bindingGeneration);
		if (_previousPublished is not null &&
		    _previousPublished.TryGetView(view, out var previous) &&
		    ReferenceEquals(previous.BoundWorld, world) &&
		    previous.BindingGeneration == bindingGeneration)
		{
			snapshot.SeedPreviousCameraFrom(previous);
		}

		return snapshot;
	}

	internal RenderViewSnapshot GetOrCreateView(RenderViewId view)
	{
		if (view.IsValid == false)
		{
			throw new ArgumentException("A snapshot entry needs a valid render view id.", nameof(view));
		}

		for (var i = 0; i < _viewEntries.Count; i++)
		{
			var existing = _viewEntries[i];
			if (existing.View == view)
			{
				Activate(existing);
				return existing;
			}
		}

		var snapshot = new RenderViewSnapshot(view, _drawTransformHistory, _drawHandles);
		if (_previousPublished is not null && _previousPublished.TryGetView(view, out var previous))
		{
			snapshot.SeedPreviousCameraFrom(previous);
		}

		var insertIndex = _viewEntries.FindIndex(candidate => candidate.View.CompareTo(view) > 0);
		if (insertIndex < 0)
		{
			_viewEntries.Add(snapshot);
		}
		else
		{
			_viewEntries.Insert(insertIndex, snapshot);
		}
		Activate(snapshot);

		return snapshot;
	}

	private void Activate(RenderViewSnapshot snapshot)
	{
		if (_views.Contains(snapshot))
		{
			return;
		}

		var insertIndex = _views.FindIndex(candidate => candidate.View.CompareTo(snapshot.View) > 0);
		if (insertIndex < 0)
		{
			_views.Add(snapshot);
		}
		else
		{
			_views.Insert(insertIndex, snapshot);
		}
	}

	public bool TryGetView(RenderViewId view, out RenderViewSnapshot snapshot)
	{
		for (var i = 0; i < _views.Count; i++)
		{
			if (_views[i].View == view)
			{
				snapshot = _views[i];
				return true;
			}
		}

		snapshot = null!;
		return false;
	}

	private RenderViewSnapshot Primary => GetOrCreateView(RenderViewId.Primary);

	public Camera Camera => Primary.Camera;
	public WorldTransform CameraWorldTransform => Primary.CameraWorldTransform;
	public Camera PreviousCamera => Primary.PreviousCamera;
	public WorldTransform PreviousCameraWorldTransform => Primary.PreviousCameraWorldTransform;
	public bool HasPreviousCameraState => Primary.HasPreviousCameraState;
	public List<RenderViewSnapshot.LightPacket> LightPackets => Primary.LightPackets;
	public List<DecalProjectorPacket> DecalPackets => Primary.DecalPackets;
	public List<FogVolumePacket> FogVolumePackets => Primary.FogVolumePackets;
	public List<OutlinePacket> OutlinePackets => Primary.OutlinePackets;
	public List<SkinningPacket> SkinningPackets => Primary.SkinningPackets;
	public ReadOnlySpan<Matrix4x4> BoneMatrices => Primary.BoneMatrices;
	public Vector3 SunDirection => Primary.SunDirection;
	public float SunIntensityScale => Primary.SunIntensityScale;
	public RenderConfig Config => Primary.Config;
	public ColorLookupTable? ColorGradingLookupTable => Primary.ColorGradingLookupTable;
	public GpuDrawDatabase GpuDrawDatabase => Primary.GpuDrawDatabase;

	public void SetCamera(Camera camera, WorldTransform worldTransform) => Primary.SetCamera(camera, worldTransform);
	public void AddSkinning(
		Mesh sourceMesh,
		Mesh instanceMesh,
		ReadOnlySpan<Matrix4x4> boneMatrices,
		ReadOnlySpan<Matrix4x4> previousBoneMatrices) =>
		Primary.AddSkinning(sourceMesh, instanceMesh, boneMatrices, previousBoneMatrices);
	public void AddLight(Light light, Matrix4x4 transform) => Primary.AddLight(light, transform);
	public void AddDecal(DecalProjector projector, Matrix4x4 transform) => Primary.AddDecal(projector, transform);
	public void AddOutline(Mesh mesh, Matrix4x4 transform, ColorRGBA color, float thicknessPixels) =>
		Primary.AddOutline(mesh, transform, color, thicknessPixels);
	public void AddFogVolume(FogVolume volume, Matrix4x4 transform) => Primary.AddFogVolume(volume, transform);
	public void SetSun(Vector3 sunDirection, float sunIntensityScale) => Primary.SetSun(sunDirection, sunIntensityScale);
	public void SetConfig(RenderConfig config) => Primary.SetConfig(config);
	public void SetColorGradingLookupTable(ColorLookupTable? lookupTable) =>
		Primary.SetColorGradingLookupTable(lookupTable);

	/// <summary>
	/// Game thread: the effects frame for <paramref name="world"/>, extracted the first time a view of that world asks
	/// for it in this snapshot.
	/// </summary>
	internal VfxWorldFrame ExtractVfx(World world, VfxService vfx, IRenderResourceScheduler? resources)
	{
		for (var i = 0; i < _vfxFrameCount; i++)
		{
			if (ReferenceEquals(_vfxFrames[i].World, world))
			{
				return _vfxFrames[i];
			}
		}

		if (_vfxFrameCount == _vfxFrames.Count)
		{
			_vfxFrames.Add(new VfxWorldFrame());
		}

		var frame = _vfxFrames[_vfxFrameCount++];
		vfx.Extract(world, frame, resources);

		return frame;
	}

	public void Clear()
	{
		_previousPublished = null;
		for (var i = 0; i < _vfxFrameCount; i++)
		{
			_vfxFrames[i].Reset(null);
		}

		_vfxFrameCount = 0;
		_views.Clear();
		for (var i = 0; i < _viewEntries.Count; i++)
		{
			_viewEntries[i].Clear();
		}
	}

	/// <summary>Seeds camera history independently for every retained view entry.</summary>
	internal void SeedPreviousCameraFrom(FrameSnapshot published)
	{
		if (ReferenceEquals(published, this))
		{
			return;
		}

		_previousPublished = published;
		for (var i = 0; i < _viewEntries.Count; i++)
		{
			var view = _viewEntries[i];
			if (published.TryGetView(view.View, out var previous))
			{
				view.SeedPreviousCameraFrom(previous);
			}
		}
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
		// The GPU draw tables are shared too, so both slots must agree on which draw owns each table slot.
		var drawHandles = new GpuDrawHandleRegistry();
		_buffers = new FrameSnapshot[] { new(drawTransformHistory, drawHandles), new(drawTransformHistory, drawHandles) };
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

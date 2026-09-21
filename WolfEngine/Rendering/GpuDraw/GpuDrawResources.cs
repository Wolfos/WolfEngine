using System.Runtime.InteropServices;
using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering;

public sealed class GpuDrawResources : IDisposable
{
	public const int IndirectCommandBufferSlotCount = 4;
	public const int MaxFramesInFlight = 4;
	public const int MaxDrawCount = 65535;
	public const int MaxInstanceCount = 65535;
	public const int MaxMaterialCount = 65535;
	public const int MaxMeshCount = 65535;
	public const int MaxTerrainLayerCount = MaxMaterialCount * 16;
	public const int HardeningCounterCount = 16;
	public const int MaxShadowViewCount = ShadowMapPass.MaxCascadeCount;
	private readonly IShaderProvider _shaderCompiler;

	public IGfxBuffer? InstanceBuffer { get; private set; }
	public IGfxBuffer? MaterialBuffer { get; private set; }
	public IGfxBuffer? TerrainMaterialBuffer { get; private set; }
	public IGfxBuffer? TerrainLayerBuffer { get; private set; }
	public IGfxBuffer? MeshBuffer { get; private set; }
	public IGfxBuffer? DrawCommandBuffer { get; private set; }
	public IGfxBuffer? DrawArgsBuffer { get; private set; }
	public IGfxBuffer? ShadowDrawArgsBuffer { get; private set; }
	public IGfxBuffer? DrawGenerationBuffer => _drawGenerationBuffers[_activeFrameSlot];
	public IGfxBuffer? InstanceGenerationBuffer => _instanceGenerationBuffers[_activeFrameSlot];
	public IGfxBuffer? MaterialGenerationBuffer => _materialGenerationBuffers[_activeFrameSlot];
	public IGfxBuffer? MeshGenerationBuffer => _meshGenerationBuffers[_activeFrameSlot];
	public IGfxBuffer? DiagnosticsCounterBuffer { get; private set; }
	private readonly IGfxBuffer?[] _instanceUpdateBuffers = new IGfxBuffer?[MaxFramesInFlight];
	private readonly IGfxBuffer?[] _meshUpdateBuffers = new IGfxBuffer?[MaxFramesInFlight];
	private readonly IGfxBuffer?[] _materialUpdateBuffers = new IGfxBuffer?[MaxFramesInFlight];
	private readonly IGfxBuffer?[] _terrainMaterialUpdateBuffers = new IGfxBuffer?[MaxFramesInFlight];
	private readonly IGfxBuffer?[] _terrainLayerUpdateBuffers = new IGfxBuffer?[MaxFramesInFlight];
	private readonly IGfxBuffer?[] _clusterAabbBuffers = new IGfxBuffer?[MaxFramesInFlight];
	private readonly IGfxBuffer?[] _clusterHeaderBuffers = new IGfxBuffer?[MaxFramesInFlight];
	private readonly IGfxBuffer?[] _clusterLightIndexBuffers = new IGfxBuffer?[MaxFramesInFlight];
	private readonly IGfxBuffer?[] _clusterWriteCursorBuffers = new IGfxBuffer?[MaxFramesInFlight];
	private readonly IGfxBuffer?[] _fogCellHeaderBuffers = new IGfxBuffer?[MaxFramesInFlight];
	private readonly IGfxBuffer?[] _fogVolumeIndexBuffers = new IGfxBuffer?[MaxFramesInFlight];
	private readonly IGfxBuffer?[] _drawCountPerBucketBuffers = new IGfxBuffer?[MaxFramesInFlight];
	private readonly IGfxBuffer?[] _shadowDrawCountPerBucketBuffers = new IGfxBuffer?[MaxFramesInFlight];
	private readonly IGfxBuffer?[] _drawExecutionRangePerBucketBuffers = new IGfxBuffer?[MaxFramesInFlight];
	private readonly IGfxBuffer?[] _shadowDrawExecutionRangePerBucketBuffers = new IGfxBuffer?[MaxFramesInFlight];
	private readonly IGfxBuffer?[] _drawGenerationBuffers = new IGfxBuffer?[MaxFramesInFlight];
	private readonly IGfxBuffer?[] _instanceGenerationBuffers = new IGfxBuffer?[MaxFramesInFlight];
	private readonly IGfxBuffer?[] _materialGenerationBuffers = new IGfxBuffer?[MaxFramesInFlight];
	private readonly IGfxBuffer?[] _meshGenerationBuffers = new IGfxBuffer?[MaxFramesInFlight];
	private int _activeFrameSlot;
	private int _activeIndirectCommandSlot;
	private GraphicsBackendKind? _constantBufferLayoutBackend;
	private ShaderConstantBufferLayout? _gBufferCameraLayout;
	private int _cameraBufferSizeInBytes;
	private int _shadowCameraBufferSizeInBytes;
	private int _transparentEnvironmentBufferSizeInBytes;
	private int _transparentLightingBufferSizeInBytes;
	private int _ddgiDebugBufferSizeInBytes;
	private int _decalProjectorCapacity;
	private ClusteredLightingFrameLayout _clusteredLightingLayout;
	private ulong _indirectBindingVersion = 1;
	private bool _pendingBindingInvalidation;
	public uint ActiveDrawCommandUpperBound { get; set; } = 1;

	/// <summary>
	/// The packed streams every mesh is suballocated from. Shared draws address them through the base
	/// vertex and start index in their draw arguments, so a pass binds these once rather than carrying
	/// per-command buffer views.
	/// </summary>
	public IGfxBuffer? PackedMeshVertexBuffer { get; set; }
	public IGfxBuffer? PackedMeshIndexBuffer { get; set; }
	public uint PackedMeshVertexStride { get; set; }

	public IGfxBuffer? SkinVertexBuffer { get; set; }
	public IGfxBuffer? BoneMatrixBuffer { get; set; }
	public IGfxBuffer? SkinnedInstanceBuffer { get; set; }

	/// <summary>
	/// Publishes a single binding-version change if any buffer in the batch just completed was replaced.
	/// Consumers treat a version change as "the bindings moved this frame", so one capacity pass must
	/// count once no matter how many buffers and frame slots it touched.
	/// </summary>
	private void CommitBindingInvalidation(string reason)
	{
		if (_pendingBindingInvalidation == false)
		{
			return;
		}

		_pendingBindingInvalidation = false;
		_indirectBindingVersion = checked(_indirectBindingVersion + 1UL);
		if (GraphicsConfig.LogGpuDrawEvents)
		{
			Console.WriteLine(
				$"[gpu draw] {reason} replaced live buffers; indirect binding version is now " +
				$"{_indirectBindingVersion}, forcing a full GPU state refresh.");
		}
	}

	public GpuDrawResources(IShaderProvider shaderCompiler)
	{
		_shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
	}

	public int ActiveIndirectCommandSlot
	{
		get => _activeIndirectCommandSlot;
		set
		{
			if (value < 0 || value >= IndirectCommandBufferSlotCount)
			{
				throw new ArgumentOutOfRangeException(nameof(value), value,
					"Indirect command buffer slot is out of range.");
			}

			_activeIndirectCommandSlot = value;
		}
	}

	public int ActiveFrameSlot
	{
		get => _activeFrameSlot;
		set
		{
			if (value < 0 || value >= MaxFramesInFlight)
			{
				throw new ArgumentOutOfRangeException(nameof(value), value, "Frame slot is out of range.");
			}

			_activeFrameSlot = value;
		}
	}

	public IGfxBuffer? InstanceUpdateBuffer => _instanceUpdateBuffers[_activeFrameSlot];

	public IGfxBuffer? MeshUpdateBuffer => _meshUpdateBuffers[_activeFrameSlot];

	public IGfxBuffer? MaterialUpdateBuffer => _materialUpdateBuffers[_activeFrameSlot];

	public IGfxBuffer? TerrainMaterialUpdateBuffer => _terrainMaterialUpdateBuffers[_activeFrameSlot];

	public IGfxBuffer? TerrainLayerUpdateBuffer => _terrainLayerUpdateBuffers[_activeFrameSlot];

	public IGfxBuffer? CameraBuffer => ActiveView.Camera[_activeFrameSlot];

	public IGfxBuffer? ShadowCameraBuffer => ActiveView.ShadowCamera[_activeFrameSlot];

	public IGfxBuffer? TransparentEnvironmentBuffer => ActiveView.TransparentEnvironment[_activeFrameSlot];

	public IGfxBuffer? TransparentLightingBuffer => ActiveView.TransparentLighting[_activeFrameSlot];

	public IGfxBuffer? DdgiDebugBuffer => ActiveView.DdgiDebug[_activeFrameSlot];

	public IGfxBuffer? DecalProjectorBuffer => ActiveView.DecalProjector[_activeFrameSlot];
	public IGfxBuffer? FogVolumeBuffer => ActiveView.FogVolume[_activeFrameSlot];
	public IGfxBuffer? FogCellHeaderBuffer => _fogCellHeaderBuffers[_activeFrameSlot];
	public IGfxBuffer? FogVolumeIndexBuffer => _fogVolumeIndexBuffers[_activeFrameSlot];

	public IGfxBuffer? ClusterPointLightBuffer => ActiveView.ClusterPointLight[_activeFrameSlot];

	public IGfxBuffer? ClusterAabbBuffer => _clusterAabbBuffers[_activeFrameSlot];

	public IGfxBuffer? ClusterHeaderBuffer => _clusterHeaderBuffers[_activeFrameSlot];

	public IGfxBuffer? ClusterLightIndexBuffer => _clusterLightIndexBuffers[_activeFrameSlot];

	public IGfxBuffer? ClusterWriteCursorBuffer => _clusterWriteCursorBuffers[_activeFrameSlot];

	public IGfxBuffer? ClusterOverflowBuffer => ActiveView.ClusterOverflow[_activeFrameSlot];

	public IGfxBuffer? DrawCountPerBucketBuffer => _drawCountPerBucketBuffers[_activeFrameSlot];

	public IGfxBuffer? ShadowDrawCountPerBucketBuffer => _shadowDrawCountPerBucketBuffers[_activeFrameSlot];

	public IGfxBuffer? DrawExecutionRangePerBucketBuffer => _drawExecutionRangePerBucketBuffers[_activeFrameSlot];

	public IGfxBuffer? ShadowDrawExecutionRangePerBucketBuffer =>
		_shadowDrawExecutionRangePerBucketBuffers[_activeFrameSlot];

	public ShaderConstantBufferLayout GBufferCameraLayout => _gBufferCameraLayout
	                                                         ?? throw new InvalidOperationException(
		                                                         "GpuDraw camera layout was not initialized.");

	public ClusteredLightingFrameLayout ClusteredLightingLayout => _clusteredLightingLayout;

	/// <summary>
	/// Buffers the CPU fills while one view's passes execute. The whole frame is one command list submitted at
	/// the end, so a buffer shared by two views would hold the second view's data by the time the GPU ran the
	/// first view's passes; each view therefore has its own set, per frame slot. Buffers the GPU writes in a
	/// view's passes and reads later in the same view's passes can stay shared, because the GPU runs the views'
	/// passes in order.
	/// </summary>
	/// <remarks>
	/// Indirect draw records bake these buffers' addresses, so a view's buffers go with that view's own indirect
	/// command sets.
	/// </remarks>
	private sealed class ViewFrameBuffers
	{
		public readonly IGfxBuffer?[] Camera = new IGfxBuffer?[MaxFramesInFlight];
		public readonly IGfxBuffer?[] ShadowCamera = new IGfxBuffer?[MaxFramesInFlight];
		public readonly IGfxBuffer?[] TransparentEnvironment = new IGfxBuffer?[MaxFramesInFlight];
		public readonly IGfxBuffer?[] TransparentLighting = new IGfxBuffer?[MaxFramesInFlight];
		public readonly IGfxBuffer?[] DdgiDebug = new IGfxBuffer?[MaxFramesInFlight];
		public readonly IGfxBuffer?[] ClusterPointLight = new IGfxBuffer?[MaxFramesInFlight];
		public readonly IGfxBuffer?[] ClusterOverflow = new IGfxBuffer?[MaxFramesInFlight];
		public readonly IGfxBuffer?[] DecalProjector = new IGfxBuffer?[MaxFramesInFlight];
		public readonly IGfxBuffer?[] FogVolume = new IGfxBuffer?[MaxFramesInFlight];

		public IEnumerable<IGfxBuffer?[]> All =>
		[
			Camera, ShadowCamera, TransparentEnvironment, TransparentLighting, DdgiDebug,
			ClusterPointLight, ClusterOverflow, DecalProjector, FogVolume
		];
	}

	private readonly List<ViewFrameBuffers?> _viewFrameBuffers = new();
	private int _activeViewIndex;

	private ViewFrameBuffers ActiveView
	{
		get
		{
			while (_viewFrameBuffers.Count <= _activeViewIndex)
			{
				_viewFrameBuffers.Add(null);
			}

			return _viewFrameBuffers[_activeViewIndex] ??= new ViewFrameBuffers();
		}
	}

	/// <summary>
	/// Selects the view whose per-view buffers the accessors return. Set before each view pass executes; the
	/// view's buffers are created on first use through the usual capacity calls.
	/// </summary>
	public int ActiveViewIndex
	{
		get => _activeViewIndex;
		set
		{
			ArgumentOutOfRangeException.ThrowIfNegative(value);
			_activeViewIndex = value;
		}
	}

	/// <summary>
	/// Retires a destroyed view's buffers. Retirement rather than disposal, because frames still in flight may
	/// read them. The primary view's are kept: the renderer always records through it.
	/// </summary>
	public void ReleaseView(IGfxDevice device, int viewIndex)
	{
		ArgumentNullException.ThrowIfNull(device);
		if (viewIndex <= 0 || viewIndex >= _viewFrameBuffers.Count || _viewFrameBuffers[viewIndex] is not { } buffers)
		{
			return;
		}

		foreach (var slots in buffers.All)
		{
			for (var i = 0; i < slots.Length; i++)
			{
				if (slots[i] is IDisposable disposable)
				{
					device.Retire(disposable, slots[i]!.Name ?? "Per-view GPU draw buffer");
				}

				slots[i] = null;
			}
		}

		_viewFrameBuffers[viewIndex] = null;
		if (_activeViewIndex == viewIndex)
		{
			_activeViewIndex = 0;
		}
	}

	internal ulong IndirectBindingVersion => _indirectBindingVersion;

	public void EnsureCreated(IGfxDevice device)
	{
		if (device is null)
		{
			throw new ArgumentNullException(nameof(device));
		}

		if (MaxFramesInFlight != IndirectCommandBufferSlotCount)
		{
			throw new InvalidOperationException(
				$"GpuDraw requires MaxFramesInFlight ({MaxFramesInFlight}) to match IndirectCommandBufferSlotCount ({IndirectCommandBufferSlotCount}).");
		}

		EnsureConstantBufferLayouts(device.BackendKind);

		InstanceBuffer ??= device.CreateBuffer(new BufferDescriptor(
			(ulong)(MaxInstanceCount * Marshal.SizeOf<GpuInstanceData>()),
			BufferUsage.Structured,
			BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource,
			name: "InstanceBuffer"));

		MaterialBuffer ??= device.CreateBuffer(new BufferDescriptor(
			(ulong)(MaxMaterialCount * Marshal.SizeOf<GpuMaterialData>()),
			BufferUsage.Structured,
			BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource,
			name: "MaterialBuffer"));

		TerrainMaterialBuffer ??= device.CreateBuffer(new BufferDescriptor(
			(ulong)(MaxMaterialCount * Marshal.SizeOf<GpuTerrainMaterialData>()),
			BufferUsage.Structured,
			BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource,
			name: "TerrainMaterialBuffer"));

		TerrainLayerBuffer ??= device.CreateBuffer(new BufferDescriptor(
			(ulong)(MaxTerrainLayerCount * Marshal.SizeOf<GpuTerrainLayerData>()),
			BufferUsage.Structured,
			BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource,
			name: "TerrainLayerBuffer"));

		MeshBuffer ??= device.CreateBuffer(new BufferDescriptor(
			(ulong)(MaxMeshCount * Marshal.SizeOf<GpuMeshData>()),
			BufferUsage.Structured,
			BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource));

		DrawCommandBuffer ??= device.CreateBuffer(new BufferDescriptor(
			(ulong)(MaxDrawCount * Marshal.SizeOf<GpuDrawCommand>()),
			BufferUsage.Structured,
			BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource));

		DrawArgsBuffer ??= device.CreateBuffer(new BufferDescriptor(
			(ulong)(MaxDrawCount * Marshal.SizeOf<GpuDrawArgs>()),
			BufferUsage.Indirect,
			BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource,
			name: "DrawArgsBuffer"));

		ShadowDrawArgsBuffer ??= device.CreateBuffer(new BufferDescriptor(
			(ulong)(MaxShadowViewCount * MaxDrawCount * Marshal.SizeOf<GpuDrawArgs>()),
			BufferUsage.Indirect,
			BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource,
			name: "ShadowDrawArgsBuffer"));

		DiagnosticsCounterBuffer ??= device.CreateBuffer(new BufferDescriptor(
			(ulong)(HardeningCounterCount * sizeof(uint)),
			BufferUsage.Structured,
			BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource));

		for (var i = 0; i < MaxFramesInFlight; i++)
		{
			_instanceUpdateBuffers[i] ??= device.CreateBuffer(new BufferDescriptor(
				(ulong)(MaxDrawCount * Marshal.SizeOf<GpuDrawInstanceUpdateData>()),
				BufferUsage.Structured,
				BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource));

			_meshUpdateBuffers[i] ??= device.CreateBuffer(new BufferDescriptor(
				(ulong)(MaxDrawCount * Marshal.SizeOf<GpuDrawMeshUpdateData>()),
				BufferUsage.Structured,
				BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource));

			_materialUpdateBuffers[i] ??= device.CreateBuffer(new BufferDescriptor(
				(ulong)(MaxDrawCount * Marshal.SizeOf<GpuDrawMaterialUpdateData>()),
				BufferUsage.Structured,
				BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource));

			_terrainMaterialUpdateBuffers[i] ??= device.CreateBuffer(new BufferDescriptor(
				(ulong)(MaxDrawCount * Marshal.SizeOf<GpuTerrainMaterialUpdateData>()),
				BufferUsage.Structured,
				BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource));

			_terrainLayerUpdateBuffers[i] ??= device.CreateBuffer(new BufferDescriptor(
				(ulong)(MaxTerrainLayerCount * Marshal.SizeOf<GpuTerrainLayerUpdateData>()),
				BufferUsage.Structured,
				BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource));

			ActiveView.Camera[i] = EnsureConstantBufferCapacity(
				device,
				ActiveView.Camera[i],
				_cameraBufferSizeInBytes,
				$"CameraBuffer[{i}]");

			ActiveView.ShadowCamera[i] = EnsureConstantBufferCapacity(
				device,
				ActiveView.ShadowCamera[i],
				_shadowCameraBufferSizeInBytes,
				$"ShadowCameraBuffer[{i}]");

			ActiveView.TransparentEnvironment[i] = EnsureConstantBufferCapacity(
				device,
				ActiveView.TransparentEnvironment[i],
				_transparentEnvironmentBufferSizeInBytes,
				$"TransparentEnvironmentBuffer[{i}]");

			ActiveView.TransparentLighting[i] = EnsureConstantBufferCapacity(
				device,
				ActiveView.TransparentLighting[i],
				_transparentLightingBufferSizeInBytes,
				$"TransparentLightingBuffer[{i}]");

			ActiveView.DdgiDebug[i] = EnsureConstantBufferCapacity(
				device,
				ActiveView.DdgiDebug[i],
				_ddgiDebugBufferSizeInBytes,
				$"DdgiDebugBuffer[{i}]");

			_drawCountPerBucketBuffers[i] ??= device.CreateBuffer(new BufferDescriptor(
				(ulong)(GpuDrawExecutionLanes.ExecutionLaneCount * sizeof(uint)),
				BufferUsage.Indirect,
				BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource));

			_shadowDrawCountPerBucketBuffers[i] ??= device.CreateBuffer(new BufferDescriptor(
				(ulong)(MaxShadowViewCount * GpuDrawExecutionLanes.ExecutionLaneCount * sizeof(uint)),
				BufferUsage.Indirect,
				BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource));

			_drawExecutionRangePerBucketBuffers[i] ??= device.CreateBuffer(new BufferDescriptor(
				(ulong)(GpuDrawExecutionLanes.ExecutionLaneCount * 2 * sizeof(uint)),
				BufferUsage.Indirect,
				BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource));

			_shadowDrawExecutionRangePerBucketBuffers[i] ??= device.CreateBuffer(new BufferDescriptor(
				(ulong)(MaxShadowViewCount * GpuDrawExecutionLanes.ExecutionLaneCount * 2 * sizeof(uint)),
				BufferUsage.Indirect,
				BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource));

			_drawGenerationBuffers[i] ??= device.CreateBuffer(new BufferDescriptor(
				(ulong)((MaxDrawCount + 1) * sizeof(uint)),
				BufferUsage.Structured,
				BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource));

			_instanceGenerationBuffers[i] ??= device.CreateBuffer(new BufferDescriptor(
				(ulong)((MaxInstanceCount + 1) * sizeof(uint)),
				BufferUsage.Structured,
				BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource));

			_materialGenerationBuffers[i] ??= device.CreateBuffer(new BufferDescriptor(
				(ulong)((MaxMaterialCount + 1) * sizeof(uint)),
				BufferUsage.Structured,
				BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource,
				name: $"MaterialGenerationBuffer[{i}]"));

			_meshGenerationBuffers[i] ??= device.CreateBuffer(new BufferDescriptor(
				(ulong)((MaxMeshCount + 1) * sizeof(uint)),
				BufferUsage.Structured,
				BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource));
		}
	}

	public void EnsureClusteredLightingCapacity(IGfxDevice device, Int2 sceneFramebufferSize)
	{
		ArgumentNullException.ThrowIfNull(device);

		var requiredGrid = ClusteredLightingShared.ComputeGrid(sceneFramebufferSize);
		var requiredClusterCount = ClusteredLightingShared.ComputeClusterCount(sceneFramebufferSize);
		var requiredLightIndexCapacity = ClusteredLightingShared.ComputeIndexCapacity(sceneFramebufferSize);
		_clusteredLightingLayout =
			new ClusteredLightingFrameLayout(requiredGrid, requiredClusterCount, requiredLightIndexCapacity);

		for (var i = 0; i < MaxFramesInFlight; i++)
		{
			ActiveView.ClusterPointLight[i] = EnsureStructuredBufferCapacity(
				device,
				ActiveView.ClusterPointLight[i],
				ClusteredLightingShared.MaxPointLights,
				Marshal.SizeOf<PointLightGpuData>(),
				$"ClusterPointLightBuffer[{i}]");
			_clusterAabbBuffers[i] = EnsureStructuredBufferCapacity(
				device,
				_clusterAabbBuffers[i],
				requiredClusterCount,
				Marshal.SizeOf<ClusterAabbGpuData>(),
				$"ClusterAabbBuffer[{i}]");
			_clusterHeaderBuffers[i] = EnsureStructuredBufferCapacity(
				device,
				_clusterHeaderBuffers[i],
				requiredClusterCount,
				Marshal.SizeOf<ClusterHeaderGpuData>(),
				$"ClusterHeaderBuffer[{i}]");
			_clusterLightIndexBuffers[i] = EnsureStructuredBufferCapacity(
				device,
				_clusterLightIndexBuffers[i],
				requiredLightIndexCapacity,
				sizeof(uint),
				$"ClusterLightIndexBuffer[{i}]");
			_clusterWriteCursorBuffers[i] = EnsureStructuredBufferCapacity(
				device,
				_clusterWriteCursorBuffers[i],
				requiredClusterCount,
				sizeof(uint),
				$"ClusterWriteCursorBuffer[{i}]");
			ActiveView.ClusterOverflow[i] = EnsureStructuredBufferCapacity(
				device,
				ActiveView.ClusterOverflow[i],
				2,
				sizeof(uint),
				$"ClusterOverflowBuffer[{i}]");
		}

		CommitBindingInvalidation("Clustered lighting capacity growth");
	}

	public void EnsureDecalCapacity(IGfxDevice device, int maxProjectorCount)
	{
		ArgumentNullException.ThrowIfNull(device);

		var clampedCount = Math.Max(1, maxProjectorCount);
		_decalProjectorCapacity = Math.Max(_decalProjectorCapacity, clampedCount);
		for (var i = 0; i < MaxFramesInFlight; i++)
		{
			ActiveView.DecalProjector[i] = EnsureStructuredBufferCapacity(
				device,
				ActiveView.DecalProjector[i],
				_decalProjectorCapacity,
				Marshal.SizeOf<GpuDecalProjectorData>(),
				$"DecalProjectorBuffer[{i}]");
		}

		CommitBindingInvalidation("Decal capacity growth");
	}

	public void EnsureFogVolumeCapacity(IGfxDevice device, int volumeCount, int cellCount, int indexCount)
	{
		ArgumentNullException.ThrowIfNull(device);
		for (var i = 0; i < MaxFramesInFlight; i++)
		{
			ActiveView.FogVolume[i] = EnsureStructuredBufferCapacity(
				device, ActiveView.FogVolume[i], volumeCount, Marshal.SizeOf<GpuFogVolumeData>(), $"FogVolumeBuffer[{i}]");
			_fogCellHeaderBuffers[i] = EnsureStructuredBufferCapacity(
				device, _fogCellHeaderBuffers[i], cellCount, Marshal.SizeOf<GpuFogCellHeader>(), $"FogCellHeaderBuffer[{i}]");
			_fogVolumeIndexBuffers[i] = EnsureStructuredBufferCapacity(
				device, _fogVolumeIndexBuffers[i], indexCount, sizeof(uint), $"FogVolumeIndexBuffer[{i}]");
		}
		CommitBindingInvalidation("Fog volume capacity growth");
	}

	public IGfxBuffer? GetDrawGenerationBufferSlot(int frameSlot)
	{
		ValidateFrameSlot(frameSlot);
		return _drawGenerationBuffers[frameSlot];
	}

	public IGfxBuffer? GetInstanceGenerationBufferSlot(int frameSlot)
	{
		ValidateFrameSlot(frameSlot);
		return _instanceGenerationBuffers[frameSlot];
	}

	public IGfxBuffer? GetMeshGenerationBufferSlot(int frameSlot)
	{
		ValidateFrameSlot(frameSlot);
		return _meshGenerationBuffers[frameSlot];
	}

	public IGfxBuffer? GetMaterialGenerationBufferSlot(int frameSlot)
	{
		ValidateFrameSlot(frameSlot);
		return _materialGenerationBuffers[frameSlot];
	}

	public static int GetShadowDrawArgsElementOffset(int cascadeIndex)
	{
		ValidateShadowCascadeIndex(cascadeIndex);
		return cascadeIndex * MaxDrawCount;
	}

	public static ulong GetShadowDrawArgsOffsetBytes(int cascadeIndex) =>
		(ulong)GetShadowDrawArgsElementOffset(cascadeIndex) * (ulong)Marshal.SizeOf<GpuDrawArgs>();

	public static int GetShadowLaneElementOffset(int cascadeIndex, int executionLaneIndex)
	{
		ValidateShadowCascadeIndex(cascadeIndex);
		if (executionLaneIndex < 0 || executionLaneIndex >= GpuDrawExecutionLanes.ExecutionLaneCount)
		{
			throw new ArgumentOutOfRangeException(
				nameof(executionLaneIndex),
				executionLaneIndex,
				"Execution lane index is out of range.");
		}

		return (cascadeIndex * GpuDrawExecutionLanes.ExecutionLaneCount) + executionLaneIndex;
	}

	public static ulong GetShadowExecutionRangeOffsetBytes(int cascadeIndex, int executionLaneIndex) =>
		(ulong)(GetShadowLaneElementOffset(cascadeIndex, executionLaneIndex) * 2 * sizeof(uint));

	public void Dispose()
	{
		(InstanceBuffer as IDisposable)?.Dispose();
		(MaterialBuffer as IDisposable)?.Dispose();
		(TerrainMaterialBuffer as IDisposable)?.Dispose();
		(TerrainLayerBuffer as IDisposable)?.Dispose();
		(MeshBuffer as IDisposable)?.Dispose();
		(DrawCommandBuffer as IDisposable)?.Dispose();
		(DrawArgsBuffer as IDisposable)?.Dispose();
		(ShadowDrawArgsBuffer as IDisposable)?.Dispose();
		(DiagnosticsCounterBuffer as IDisposable)?.Dispose();
		foreach (var viewBuffers in _viewFrameBuffers)
		{
			if (viewBuffers is null)
			{
				continue;
			}

			foreach (var slots in viewBuffers.All)
			{
				for (var i = 0; i < slots.Length; i++)
				{
					(slots[i] as IDisposable)?.Dispose();
					slots[i] = null;
				}
			}
		}

		_viewFrameBuffers.Clear();
		for (var i = 0; i < MaxFramesInFlight; i++)
		{
			(_instanceUpdateBuffers[i] as IDisposable)?.Dispose();
			(_meshUpdateBuffers[i] as IDisposable)?.Dispose();
			(_materialUpdateBuffers[i] as IDisposable)?.Dispose();
			(_terrainMaterialUpdateBuffers[i] as IDisposable)?.Dispose();
			(_terrainLayerUpdateBuffers[i] as IDisposable)?.Dispose();
			(_fogCellHeaderBuffers[i] as IDisposable)?.Dispose();
			(_fogVolumeIndexBuffers[i] as IDisposable)?.Dispose();
			(_clusterAabbBuffers[i] as IDisposable)?.Dispose();
			(_clusterHeaderBuffers[i] as IDisposable)?.Dispose();
			(_clusterLightIndexBuffers[i] as IDisposable)?.Dispose();
			(_clusterWriteCursorBuffers[i] as IDisposable)?.Dispose();
			(_drawCountPerBucketBuffers[i] as IDisposable)?.Dispose();
			(_shadowDrawCountPerBucketBuffers[i] as IDisposable)?.Dispose();
			(_drawExecutionRangePerBucketBuffers[i] as IDisposable)?.Dispose();
			(_shadowDrawExecutionRangePerBucketBuffers[i] as IDisposable)?.Dispose();
			(_drawGenerationBuffers[i] as IDisposable)?.Dispose();
			(_instanceGenerationBuffers[i] as IDisposable)?.Dispose();
			(_materialGenerationBuffers[i] as IDisposable)?.Dispose();
			(_meshGenerationBuffers[i] as IDisposable)?.Dispose();
			_instanceUpdateBuffers[i] = null;
			_meshUpdateBuffers[i] = null;
			_materialUpdateBuffers[i] = null;
			_terrainMaterialUpdateBuffers[i] = null;
			_terrainLayerUpdateBuffers[i] = null;
			_fogCellHeaderBuffers[i] = null;
			_fogVolumeIndexBuffers[i] = null;
			_clusterAabbBuffers[i] = null;
			_clusterHeaderBuffers[i] = null;
			_clusterLightIndexBuffers[i] = null;
			_clusterWriteCursorBuffers[i] = null;
			_drawCountPerBucketBuffers[i] = null;
			_shadowDrawCountPerBucketBuffers[i] = null;
			_drawExecutionRangePerBucketBuffers[i] = null;
			_shadowDrawExecutionRangePerBucketBuffers[i] = null;
			_drawGenerationBuffers[i] = null;
			_instanceGenerationBuffers[i] = null;
			_materialGenerationBuffers[i] = null;
			_meshGenerationBuffers[i] = null;
		}

		TerrainMaterialBuffer = null;
		TerrainLayerBuffer = null;
		ShadowDrawArgsBuffer = null;
	}

	private static void ValidateFrameSlot(int frameSlot)
	{
		if (frameSlot < 0 || frameSlot >= MaxFramesInFlight)
		{
			throw new ArgumentOutOfRangeException(nameof(frameSlot), frameSlot, "Frame slot is out of range.");
		}
	}

	private static void ValidateShadowCascadeIndex(int cascadeIndex)
	{
		if (cascadeIndex < 0 || cascadeIndex >= MaxShadowViewCount)
		{
			throw new ArgumentOutOfRangeException(
				nameof(cascadeIndex),
				cascadeIndex,
				"Shadow cascade index is out of range.");
		}
	}

	private void EnsureConstantBufferLayouts(GraphicsBackendKind backendKind)
	{
		if (_constantBufferLayoutBackend.HasValue && _constantBufferLayoutBackend.Value == backendKind)
		{
			return;
		}

		var gbuffer = _shaderCompiler.GetGraphicsShaderWithReflection(
			EngineShaderPrograms.GBuffer,
			"vertexShader",
			"fragmentShader",
			backendKind);
		_gBufferCameraLayout = gbuffer.ReflectionLayout.GetConstantBuffer("CameraParams");
		_cameraBufferSizeInBytes = _gBufferCameraLayout.SizeInBytes;

		var shadow = _shaderCompiler.GetGraphicsShaderWithReflection(
			EngineShaderPrograms.ShadowMap,
			"vertexShader",
			"fragmentShader",
			backendKind);
		_shadowCameraBufferSizeInBytes = shadow.ReflectionLayout.GetConstantBuffer("CameraParams").SizeInBytes;

		var transparent = _shaderCompiler.GetGraphicsShaderWithReflection(
			EngineShaderPrograms.TransparentForward,
			"vertexShader",
			"fragmentShader",
			backendKind);
		_transparentEnvironmentBufferSizeInBytes = transparent.ReflectionLayout
			.GetConstantBuffer("TransparentEnvironmentParams").SizeInBytes;
		_transparentLightingBufferSizeInBytes =
			transparent.ReflectionLayout.GetConstantBuffer("LightingParams").SizeInBytes;

		var debugPrimitive = _shaderCompiler.GetGraphicsShaderWithReflection(
			EngineShaderPrograms.DebugPrimitiveForward,
			"vertexShader",
			"fragmentShader",
			backendKind);
		_ddgiDebugBufferSizeInBytes =
			debugPrimitive.ReflectionLayout.GetConstantBuffer("DdgiDebugParams").SizeInBytes;

		_constantBufferLayoutBackend = backendKind;
	}

	private static IGfxBuffer EnsureConstantBufferCapacity(
		IGfxDevice device,
		IGfxBuffer? existingBuffer,
		int minimumSizeInBytes,
		string debugName)
	{
		if (minimumSizeInBytes <= 0)
		{
			throw new InvalidOperationException($"Reflected constant-buffer size for '{debugName}' must be positive.");
		}

		if (existingBuffer is null)
		{
			return device.CreateBuffer(new BufferDescriptor(
				(ulong)minimumSizeInBytes,
				BufferUsage.Constant,
				BufferFlags.AllowShaderResource,
				debugName));
		}

		var existingSize = checked((int)existingBuffer.Descriptor.SizeInBytes);
		if (existingSize < minimumSizeInBytes)
		{
			throw new InvalidOperationException(
				$"Existing constant buffer '{debugName}' is too small ({existingSize} bytes). " +
				$"Reflected shader layout requires at least {minimumSizeInBytes} bytes.");
		}

		return existingBuffer;
	}

	private IGfxBuffer EnsureStructuredBufferCapacity(
		IGfxDevice device,
		IGfxBuffer? existingBuffer,
		int elementCount,
		int elementSizeInBytes,
		string debugName)
	{
		var sizeInBytes = checked((ulong)Math.Max(elementCount, 1) * (ulong)Math.Max(elementSizeInBytes, 1));
		if (existingBuffer is not null && existingBuffer.Descriptor.SizeInBytes >= sizeInBytes)
		{
			return existingBuffer;
		}

		if (existingBuffer is not null)
		{
			// Frames still in flight, and the frame currently being recorded, hold this buffer's GPU
			// virtual address - both bound directly and baked into indirect command records. Destroying
			// it here is a use-after-free that surfaces as a device hang at ExecuteIndirect.
			if (existingBuffer is IDisposable disposableBuffer)
			{
				device.Retire(disposableBuffer, debugName);
			}

			// Only a replacement invalidates bindings. A first allocation replaces nothing, so no record
			// can be holding a stale address and there is nothing to re-encode; bumping the version for it
			// would force a full GPU state refresh that buys nothing. Deferred so that one capacity pass
			// over every buffer and frame slot counts as a single change rather than one per buffer.
			_pendingBindingInvalidation = true;
			if (GraphicsConfig.LogGpuDrawEvents)
			{
				Console.WriteLine(
					$"[gpu draw] '{debugName}' grew {existingBuffer.Descriptor.SizeInBytes} -> {sizeInBytes} bytes.");
			}
		}

		return device.CreateBuffer(new BufferDescriptor(
			sizeInBytes,
			BufferUsage.Structured,
			BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource,
			debugName));
	}
}

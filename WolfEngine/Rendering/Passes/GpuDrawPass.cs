#nullable enable

using System.Numerics;
using System.Runtime.InteropServices;
using WolfEngine.Profiling;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

public sealed class GpuDrawPass
{
	private readonly IShaderProvider _shaderCompiler;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private readonly GpuDrawResources _gpuDrawResources;
	private readonly GpuDrawHardeningStats _hardeningStats;
	private readonly IRenderer _renderer;
	private readonly IGpuDrawBackendBridge _backendBridge;
	private DescriptorHandle _terrainLayerSampler = DescriptorHandle.Invalid;
	private DescriptorHandle _terrainControlSampler = DescriptorHandle.Invalid;
	private DescriptorHandle _terrainHeightSampler = DescriptorHandle.Invalid;
	private IGfxPipeline? _instanceUpdatePipeline;
	private IGfxPipeline? _meshUpdatePipeline;
	private IGfxPipeline? _materialUpdatePipeline;
	private IGfxPipeline? _terrainMaterialUpdatePipeline;
	private IGfxPipeline? _terrainLayerUpdatePipeline;
	private IGfxPipeline? _cullPipeline;
	private IGfxPipeline? _compactPipeline;
	private GraphicsBackendKind? _computeReflectionBackendKind;
	private ReadOnlyMemory<byte> _instanceUpdateShaderBytecode;
	private ReadOnlyMemory<byte> _meshUpdateShaderBytecode;
	private ReadOnlyMemory<byte> _materialUpdateShaderBytecode;
	private ReadOnlyMemory<byte> _terrainMaterialUpdateShaderBytecode;
	private ReadOnlyMemory<byte> _terrainLayerUpdateShaderBytecode;
	private ReadOnlyMemory<byte> _cullShaderBytecode;
	private ReadOnlyMemory<byte> _compactShaderBytecode;
	private ComputeThreadGroupSize? _instanceUpdateThreadGroupSize;
	private ComputeThreadGroupSize? _meshUpdateThreadGroupSize;
	private ComputeThreadGroupSize? _materialUpdateThreadGroupSize;
	private ComputeThreadGroupSize? _terrainMaterialUpdateThreadGroupSize;
	private ComputeThreadGroupSize? _terrainLayerUpdateThreadGroupSize;
	private ComputeThreadGroupSize? _cullThreadGroupSize;
	private ComputeThreadGroupSize? _compactThreadGroupSize;
	private uint[]? _executionRangeReset;
	private ComputeResourceBindings? _terrainMaterialUpdateBindings;
	private ComputeResourceBindings? _terrainLayerUpdateBindings;
	private ShaderPropertyWriter? _instanceUpdateParamsWriter;
	private ShaderPropertyWriter? _meshUpdateParamsWriter;
	private ShaderPropertyWriter? _materialUpdateParamsWriter;
	private ShaderPropertyWriter? _terrainMaterialUpdateParamsWriter;
	private ShaderPropertyWriter? _terrainLayerUpdateParamsWriter;
	private ShaderPropertyWriter? _cullParamsWriter;
	private ShaderPropertyWriter? _compactParamsWriter;
	private readonly List<GpuDrawUpdate> _updates = new();
	private readonly List<GpuDrawInstanceUpdateData> _instanceUpdateData = new();
	private readonly List<GpuDrawMeshUpdateData> _meshUpdateData = new();
	private readonly List<GpuDrawMaterialUpdateData> _materialUpdateData = new();
	private readonly List<GpuTerrainMaterialUpdateData> _terrainMaterialUpdateData = new();
	private readonly Dictionary<uint, int> _terrainMaterialUpdateIndices = new();
	private readonly List<GpuTerrainLayerUpdateData> _terrainLayerUpdateData = new();
	private readonly List<GpuDrawEntry> _drawEntries = new();
	private readonly List<uint> _drawGenerations = new();
	private readonly List<uint> _instanceGenerations = new();
	private readonly List<uint> _meshGenerations = new();
	private readonly List<uint> _materialGenerations = new();
	private readonly List<SharedDrawIndirectCommandSet> _knownIndirectCommandSets = new();
	private readonly uint[] _lastGpuDiagnosticCounters = new uint[GpuDrawResources.HardeningCounterCount];
	private readonly List<StructuralCommandRecord> _structuralReplayRecords = new();
	private readonly IGfxPipeline?[] _gbufferPipelines = new IGfxPipeline?[GpuDrawExecutionLanes.ExecutionLaneCount];
	private readonly SharedDrawGraphicsBufferBindings?[] _gbufferBufferBindings = new SharedDrawGraphicsBufferBindings?[GpuDrawExecutionLanes.ExecutionLaneCount];
	private readonly ShaderReflectionLayout?[] _gbufferReflections = new ShaderReflectionLayout?[GpuDrawExecutionLanes.ExecutionLaneCount];
	private readonly SharedDrawIndirectCommandSet _gbufferIndirectCommandSet = new();
	private bool _gbufferCompactionActive;
	private readonly Dictionary<uint, MaterialDrawState> _materialDrawStates = new();
	private readonly Dictionary<uint, TerrainDrawSurface> _terrainMaterialStates = new();
	private readonly Dictionary<uint, TerrainMaterialAllocation> _terrainMaterialAllocations = new();
	private int _nextTerrainLayerSlot = 1;
	private int _terrainLogHealthyBudget;
	private int _activeIndirectSlot = -1;
	private ulong _latestStructuralVersion;
	private ulong _nextStructuralVersion = 1;
	private uint _bindlessEpoch = 1;
	private bool _supportsIndirectStructuralUpdates;
	private bool _loggedCapacityExceeded;
	private bool _loggedUpdateOverflowRecovery;
	private bool _loggedUnsupportedDrawKind;
	private bool _loggedCompactionUnavailable;
	private bool _gpuStateBootstrapPending = true;

	private const uint DrawFlagActive = GpuDrawFlags.Active;
	private const int DrawFlagBucketShift = GpuDrawFlags.BucketShift;
	private const uint DrawFlagBucketMask = GpuDrawFlags.BucketMask;

	private readonly struct StructuralCommandRecord
	{
		public StructuralCommandRecord(
			ulong version,
			GpuDrawUpdateType type,
			GpuDrawHandle drawHandle,
			GpuDrawKind drawKind,
			GpuDrawBucketId bucketId,
			int executionIndex,
			Mesh? mesh)
		{
			Version = version;
			Type = type;
			DrawHandle = drawHandle;
			DrawKind = drawKind;
			BucketId = bucketId;
			ExecutionIndex = executionIndex;
			Mesh = mesh;
		}

		public ulong Version { get; }
		public GpuDrawUpdateType Type { get; }
		public GpuDrawHandle DrawHandle { get; }
		public GpuDrawKind DrawKind { get; }
		public GpuDrawBucketId BucketId { get; }
		public int ExecutionIndex { get; }
		public Mesh? Mesh { get; }
	}

	private readonly record struct MaterialDrawState(
		GpuDrawKind DrawKind,
		GpuDrawBucketId BucketId,
		int ExecutionIndex,
		uint DrawFlags);

	private readonly record struct TerrainMaterialAllocation(uint LayerStart, uint LayerCount, bool Reallocated);

	private readonly record struct ComputeResourceBindings(params uint[] Slots);

	public GpuDrawPass(IShaderProvider shaderCompiler,
		BindlessResourceRegistry bindlessRegistry, GpuDrawResources gpuDrawResources,
		GpuDrawHardeningStats hardeningStats, IRenderer renderer,
		IGpuDrawBackendBridge backendBridge)
	{
		_shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
		_gpuDrawResources = gpuDrawResources ?? throw new ArgumentNullException(nameof(gpuDrawResources));
		_hardeningStats = hardeningStats ?? throw new ArgumentNullException(nameof(hardeningStats));
		_renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
		_backendBridge = backendBridge ?? throw new ArgumentNullException(nameof(backendBridge));
		_knownIndirectCommandSets.Add(_gbufferIndirectCommandSet);
	}

	public void RecordUpdate(RenderGraphContext context)
	{
		var drawDatabase = context.GpuDrawDatabase;
		var device = _renderer.GetGfxDevice();
		_bindlessRegistry.EnsureInitialized(device);
		EnsureTerrainSamplers();
		_gpuDrawResources.EnsureCreated(device);
		SampleVisibilityDiagnostics();
		_hardeningStats.ResetSubmissionDiagnostics();
		EnsureGBufferPipelines(device);
		var primaryGBufferPipeline = GetPrimaryGBufferPipeline();
		var backendSignals = _backendBridge.PrepareFrame(device, _renderer, _gpuDrawResources, primaryGBufferPipeline);
		_supportsIndirectStructuralUpdates = backendSignals.SupportsIndirectStructuralUpdates;
		if (backendSignals.RequiresFullSlotReencode)
		{
			_bindlessEpoch++;
		}

		var activeSlot = AdvanceActiveIndirectSlot();
		_gpuDrawResources.ActiveIndirectCommandSlot = activeSlot;
		_gpuDrawResources.ActiveFrameSlot = activeSlot;
		var requireFullGpuStateRefresh = backendSignals.RequiresFullSlotReencode;

		drawDatabase.ConsumeUpdates(_updates);
		UploadGenerationTables(drawDatabase);
		if (_gpuStateBootstrapPending)
		{
			var appended = AppendFullGpuStateRefreshUpdates(drawDatabase, _updates);
			if (appended > 0)
			{
				_gpuStateBootstrapPending = false;
			}
		}

		if (requireFullGpuStateRefresh)
		{
			var refreshed = AppendFullGpuStateRefreshUpdates(drawDatabase, _updates);
			if (GraphicsConfig.LogGpuDrawEvents)
			{
				Console.WriteLine(
					$"[gpu draw] full GPU state refresh on frame slot {activeSlot}: re-added {refreshed} draws, " +
					$"bindless epoch {_bindlessEpoch}. Terrain layer allocations were reset.");
			}
		}

		if (_updates.Count > GpuDrawResources.MaxDrawCount)
		{
			var droppedDeltaCount = _updates.Count;
			_updates.Clear();
			AppendClearAllDraws(_updates);
			_gpuStateBootstrapPending = true;
			var rebuiltCount = 0;
			_hardeningStats.IncrementUpdateOverflowRecoveries();
			if (_loggedUpdateOverflowRecovery == false)
			{
				_loggedUpdateOverflowRecovery = true;
				Console.WriteLine(
					$"GpuDraw: update backlog overflow ({droppedDeltaCount} > {GpuDrawResources.MaxDrawCount}); switched to full-state refresh ({rebuiltCount} updates).");
			}
		}

		_gpuDrawResources.ActiveDrawCommandUpperBound = drawDatabase.GetActiveDrawCommandUpperBound();
		// Republished every frame because capacity growth can replace the packed buffers underneath us.
		_gpuDrawResources.PackedMeshVertexBuffer = _renderer.GetPackedMeshVertexBuffer();
		_gpuDrawResources.PackedMeshIndexBuffer = _renderer.GetPackedMeshIndexBuffer();
		_gpuDrawResources.PackedMeshVertexStride = _renderer.GetPackedMeshVertexStride();
		_instanceUpdateData.Clear();
		_meshUpdateData.Clear();
		_materialUpdateData.Clear();
		_terrainMaterialUpdateData.Clear();
		_terrainMaterialUpdateIndices.Clear();
		_terrainLayerUpdateData.Clear();

		var forceGpuRefresh = _gpuStateBootstrapPending || requireFullGpuStateRefresh;
		if (forceGpuRefresh)
		{
			_terrainMaterialStates.Clear();
			_terrainMaterialAllocations.Clear();
			_nextTerrainLayerSlot = 1;
		}

		// Per batch, so a repaint reports its suspects plus a few healthy draws for comparison.
		_terrainLogHealthyBudget = 4;

		var updateCount = Math.Min(_updates.Count, GpuDrawResources.MaxDrawCount);

		for (var i = 0; i < updateCount; i++)
		{
			var update = _updates[i];
			var drawIdInRange = update.DrawIndex > 0 && update.DrawIndex < GpuDrawResources.MaxDrawCount;
			if (drawIdInRange == false)
			{
				LogCapacityExceededOnce(in update);
				continue;
			}

			if (update.Type != GpuDrawUpdateType.Remove)
			{
				var instanceIdInRange =
					update.InstanceIndex > 0 && update.InstanceIndex < GpuDrawResources.MaxInstanceCount;
				var meshIdInRange = update.MeshIndex > 0 && update.MeshIndex < GpuDrawResources.MaxMeshCount;
				var materialIdInRange =
					update.MaterialIndex > 0 && update.MaterialIndex < GpuDrawResources.MaxMaterialCount;
				if (instanceIdInRange == false || meshIdInRange == false || materialIdInRange == false)
				{
					LogCapacityExceededOnce(in update);
					update = GpuDrawUpdate.CreateRemove(update.DrawKind, update.DrawHandle, update.InstanceHandle);
				}
			}

			var drawKind = update.DrawKind;
			var mesh = update.Mesh;
			var material = update.Material;

			if (mesh is not null && GpuDrawClassification.SupportsMeshBackedGeometry(drawKind))
			{
				_renderer.EnsureMeshResources(mesh);
			}

			uint vertexHandle = _bindlessRegistry.ErrorBufferHandle.Value;
			uint indexHandle = _bindlessRegistry.ErrorBufferHandle.Value;
			uint indexCount = 0;
			uint indexFormat = 0;
			uint startIndex = 0;
			int baseVertex = 0;

			if (GpuDrawClassification.SupportsMeshBackedGeometry(drawKind) &&
			    mesh?.VertexBuffer is not null &&
			    mesh.IndexBuffer is not null)
			{
				var registeredVertexHandle = _bindlessRegistry.RegisterBuffer(mesh.VertexBuffer).Value;
				var registeredIndexHandle = _bindlessRegistry.RegisterBuffer(mesh.IndexBuffer).Value;
				if (registeredVertexHandle != _bindlessRegistry.ErrorBufferHandle.Value &&
				    registeredIndexHandle != _bindlessRegistry.ErrorBufferHandle.Value)
				{
					vertexHandle = registeredVertexHandle;
					indexHandle = registeredIndexHandle;
					indexCount = mesh.IndexCount;
					indexFormat = 0;
					startIndex = checked((uint)(mesh.PackedIndexOffsetBytes / sizeof(uint)));
					baseVertex = mesh.PackedBaseVertex;
				}
				else
				{
					_hardeningStats.IncrementFallbackProxySubstitutions();
				}
			}

			uint albedoHandle = _bindlessRegistry.ErrorTextureHandle.Value;
			uint ormHandle = _bindlessRegistry.ErrorTextureHandle.Value;
			uint normalHandle = _bindlessRegistry.ErrorTextureHandle.Value;
			uint emissiveHandle = _bindlessRegistry.ErrorTextureHandle.Value;
			uint samplerHandle = _bindlessRegistry.ErrorSamplerHandle.Value;
			uint heightmapHandle = _bindlessRegistry.ErrorTextureHandle.Value;
			uint layerIndexMapHandle = _bindlessRegistry.ErrorTextureHandle.Value;
			uint layerWeightMapHandle = _bindlessRegistry.ErrorTextureHandle.Value;
			uint hasLayerMaps = 0;
			uint heightmapSamplerHandle = _terrainHeightSampler.Value;
			uint layerSamplerHandle = _terrainLayerSampler.Value;
			uint layerMapSamplerHandle = _terrainControlSampler.Value;
			uint layerStart = 0;
			uint layerCount = 0;
			var heightBlendSharpness = 0.0f;
			var autoMaterialBlendDegrees = 0.0f;
			var terrainHeightScale = 0.0f;
			var baseColor = ColorRGBA.White;
			var metallicRoughness = Vector4.One;
			var emissiveFactorIntensity = Vector4.Zero;
			var bucketId = GpuDrawBucketId.Opaque;
			var executionLaneIndex = 0;
			uint drawFlags = update.Type == GpuDrawUpdateType.Remove ? 0u : CreateDrawFlags(executionLaneIndex);
			var alphaCutoff = 0.0f;

			var terrainSurfaceReady = true;
			if (GpuDrawClassification.SupportsTerrainMaterialInterpretation(drawKind) &&
			    update.TerrainSurface.HasValue)
			{
				var pendingTerrainSurface = update.TerrainSurface.Value;
				baseColor = ColorRGBA.White;
				metallicRoughness = Vector4.Zero;
				emissiveFactorIntensity = Vector4.Zero;
				layerCount = (uint)Math.Max(pendingTerrainSurface.LayerCount, 1);
				heightBlendSharpness = pendingTerrainSurface.HeightBlendSharpness;
				autoMaterialBlendDegrees = pendingTerrainSurface.AutoMaterialBlendDegrees;
				terrainHeightScale = pendingTerrainSurface.HeightScale;
				if (pendingTerrainSurface.Heightmap is { } heightmap)
				{
					heightmapHandle = RegisterTerrainTexture(heightmap);
				}

				if (pendingTerrainSurface.LayerIndexMap is { } layerIndexMap)
				{
					layerIndexMapHandle = RegisterTerrainTexture(layerIndexMap);
				}

				if (pendingTerrainSurface.LayerWeightMap is { } layerWeightMap)
				{
					layerWeightMapHandle = RegisterTerrainTexture(layerWeightMap);
				}

				if (pendingTerrainSurface.LayerIndexMap is not null && pendingTerrainSurface.LayerWeightMap is not null)
				{
					hasLayerMaps = 1;
				}

				terrainSurfaceReady = heightmapHandle != _bindlessRegistry.ErrorTextureHandle.Value;
			}

			var materialReady = drawKind switch
			{
				GpuDrawKind.Mesh => material?.HasGpuResources ?? false,
				GpuDrawKind.DebugPrimitive => material is not null,
				GpuDrawKind.Terrain => material is not null && terrainSurfaceReady,
				_ => false
			};

			var materialResources = material?.Resources;
			if (material is not null)
			{
				if (GpuDrawClassification.TryResolveExecutionLane(drawKind, material, out var laneDefinition))
				{
					bucketId = laneDefinition.BucketId;
					executionLaneIndex = laneDefinition.ExecutionIndex;
					switch (material.AlphaMode)
					{
						case AlphaMode.AlphaTest:
							alphaCutoff = Math.Clamp(material.AlphaCutoff, 0.0f, 1.0f);
							break;
					}
				}
				else
				{
					LogUnsupportedDrawKindOnce(drawKind);
					_hardeningStats.IncrementMaterialFallbackDrawHits();
					drawFlags = 0u;
				}

				var desiredFlags = update.Type == GpuDrawUpdateType.Remove ? 0u : CreateDrawFlags(executionLaneIndex);
				if (drawFlags != 0u)
				{
					drawFlags = materialReady ? desiredFlags : 0u;
				}

				_materialDrawStates[update.MaterialHandle.Value] =
					new MaterialDrawState(drawKind, bucketId, executionLaneIndex, drawFlags);
				if (materialReady == false && update.Type != GpuDrawUpdateType.Remove)
				{
					_hardeningStats.AddMaterialFallbackIncident(bucketId);
				}
			}
			else if (update.Type != GpuDrawUpdateType.Remove &&
			         _materialDrawStates.TryGetValue(update.MaterialHandle.Value, out var cachedState))
			{
				drawFlags = cachedState.DrawFlags;
				drawKind = cachedState.DrawKind;
				bucketId = cachedState.BucketId;
				executionLaneIndex = cachedState.ExecutionIndex;
			}

			if (backendSignals.SupportsIndirectStructuralUpdates &&
			    IsStructuralUpdateType(update.Type) &&
			    drawDatabase.IsCurrentDrawHandle(update.DrawHandle) &&
			    update.DrawIndex > 0 &&
			    update.DrawIndex < GpuDrawResources.MaxDrawCount)
			{
				AppendStructuralRecord(update, mesh, drawKind, bucketId, executionLaneIndex);
			}

			if (materialResources is not null && GpuDrawClassification.SupportsTexturedPbrMaterialInterpretation(drawKind))
			{
				albedoHandle = materialResources.AlbedoTexture.IsValid
					? materialResources.AlbedoTexture.Value
					: _bindlessRegistry.ErrorTextureHandle.Value;
				ormHandle = materialResources.OrmTexture.IsValid
					? materialResources.OrmTexture.Value
					: _bindlessRegistry.ErrorTextureHandle.Value;
				normalHandle = materialResources.NormalTexture.IsValid
					? materialResources.NormalTexture.Value
					: _bindlessRegistry.ErrorTextureHandle.Value;
				emissiveHandle = materialResources.EmissiveTexture.IsValid
					? materialResources.EmissiveTexture.Value
					: _bindlessRegistry.ErrorTextureHandle.Value;
				samplerHandle = materialResources.Sampler.IsValid
					? materialResources.Sampler.Value
					: _bindlessRegistry.ErrorSamplerHandle.Value;
				if (albedoHandle == _bindlessRegistry.ErrorTextureHandle.Value ||
				    ormHandle == _bindlessRegistry.ErrorTextureHandle.Value ||
				    normalHandle == _bindlessRegistry.ErrorTextureHandle.Value ||
				    emissiveHandle == _bindlessRegistry.ErrorTextureHandle.Value ||
				    samplerHandle == _bindlessRegistry.ErrorSamplerHandle.Value)
				{
					_hardeningStats.IncrementFallbackProxySubstitutions();
				}

				baseColor = material!.Color;
				metallicRoughness = new Vector4(material.MetallicFactor, material.RoughnessFactor, alphaCutoff, material.NormalScale);
				emissiveFactorIntensity = new Vector4(material.EmissiveFactor, material.EmissiveIntensity);
			}
			else if (material is not null && GpuDrawClassification.SupportsUnlitTintMaterialInterpretation(drawKind))
			{
				baseColor = material.Color;
				metallicRoughness = Vector4.Zero;
				emissiveFactorIntensity = Vector4.Zero;
			}

			_instanceUpdateData.Add(new GpuDrawInstanceUpdateData(
				update.PreviousWorld,
				update.World,
				update.BoundsCenterRadius,
				update.TerrainInstanceData.ChunkOriginSize,
				update.TerrainInstanceData.HeightmapUvScaleOffset,
				(uint)update.Type,
				update.DrawHandle.Value,
				update.InstanceHandle.Value,
				(uint)drawKind,
				update.MeshHandle.Value,
				update.MaterialHandle.Value,
				drawFlags));

			if (update.Type is GpuDrawUpdateType.Add or GpuDrawUpdateType.UpdateMesh)
			{
				_meshUpdateData.Add(new GpuDrawMeshUpdateData(
					update.MeshHandle.Value,
					vertexHandle,
					indexHandle,
					indexCount,
					indexFormat,
					baseVertex,
					startIndex));
			}

			if (update.Type is GpuDrawUpdateType.Add or GpuDrawUpdateType.UpdateMaterial)
			{
				_materialUpdateData.Add(new GpuDrawMaterialUpdateData(
					update.MaterialHandle.Value,
					baseColor,
					metallicRoughness,
					emissiveFactorIntensity,
					albedoHandle,
					ormHandle,
					normalHandle,
					emissiveHandle,
					samplerHandle));

				if (GpuDrawClassification.SupportsTerrainMaterialInterpretation(drawKind) &&
				    update.TerrainSurface is { } terrainSurface)
				{
					var allocation = EnsureTerrainMaterialAllocation(update.MaterialHandle.Value, layerCount);
					layerStart = allocation.LayerStart;
					if (layerStart == 0)
					{
						layerCount = 1;
					}
					// Every chunk sharing a material queues an update, and the update shader writes
					// g_TerrainMaterialTable[materialIndex] with one thread per entry. Left uncoalesced
					// that is several threads racing for one slot, so keep a single entry per material.
					AddOrReplaceTerrainMaterialUpdate(new GpuTerrainMaterialUpdateData(
						update.MaterialHandle.Value,
						heightmapHandle,
						layerIndexMapHandle,
						layerWeightMapHandle,
						hasLayerMaps,
						heightmapSamplerHandle,
						layerSamplerHandle,
						layerMapSamplerHandle,
						layerStart,
						layerCount,
						heightBlendSharpness,
						terrainHeightScale,
						autoMaterialBlendDegrees));


					if (layerStart != 0)
					{
						var previousTerrainState = forceGpuRefresh ||
						                           allocation.Reallocated ||
						                           _terrainMaterialStates.TryGetValue(update.MaterialHandle.Value, out var uploadedSurface) == false
							? default(TerrainDrawSurface?)
							: uploadedSurface;
						AppendTerrainLayerUpdates(update.MaterialHandle.Value, layerStart, terrainSurface, previousTerrainState, forceGpuRefresh || allocation.Reallocated);
					}
					_terrainMaterialStates[update.MaterialHandle.Value] = terrainSurface;
				}
			}

			LogTerrainDrawDataIfEnabled(
				in update,
				drawKind,
				heightmapHandle,
				layerIndexMapHandle,
				layerWeightMapHandle,
				layerStart,
				layerCount,
				terrainHeightScale,
				indexCount,
				baseVertex,
				terrainSurfaceReady);
		}

		if (_instanceUpdateData.Count == 0 &&
		    _meshUpdateData.Count == 0 &&
		    _materialUpdateData.Count == 0 &&
		    _terrainMaterialUpdateData.Count == 0 &&
		    _terrainLayerUpdateData.Count == 0)
		{
			return;
		}

		DispatchInstanceUpdates(context, device);
		DispatchMeshUpdates(context, device);
		DispatchMaterialUpdates(context, device);
		DispatchTerrainMaterialUpdates(context, device);
		DispatchTerrainLayerUpdates(context, device);

		PublishSubmittedBucketDiagnostics(drawDatabase);
	}

	public void RecordCull(RenderGraphContext context, SceneDrawData sceneData)
	{
		ArgumentNullException.ThrowIfNull(sceneData);
		Span<Matrix4x4> viewProjections = stackalloc Matrix4x4[1];
		viewProjections[0] = sceneData.ViewProjection;
		RecordCullForViews(
			context,
			viewProjections,
			sceneData.CameraOrigin,
			useShadowBuffers: false,
			DrawPassParticipation.None);
	}

	public void RecordCullForView(RenderGraphContext context, Matrix4x4 viewProjection, Vector3 cameraOrigin,
		bool useShadowBuffers = false)
	{
		Span<Matrix4x4> viewProjections = stackalloc Matrix4x4[1];
		viewProjections[0] = viewProjection;
		RecordCullForViews(
			context,
			viewProjections,
			cameraOrigin,
			useShadowBuffers,
			useShadowBuffers ? DrawPassParticipation.ShadowCaster : DrawPassParticipation.None);
	}

	public void RecordCullForViews(
		RenderGraphContext context,
		ReadOnlySpan<Matrix4x4> viewProjections,
		Vector3 cameraOrigin,
		bool useShadowBuffers,
		DrawPassParticipation participation)
	{
		if (viewProjections.IsEmpty || viewProjections.Length > GpuDrawResources.MaxShadowViewCount)
		{
			throw new ArgumentOutOfRangeException(
				nameof(viewProjections),
				viewProjections.Length,
				$"Cull view count must be between 1 and {GpuDrawResources.MaxShadowViewCount}.");
		}

		var device = _renderer.GetGfxDevice();
		_bindlessRegistry.EnsureInitialized(device);
		_gpuDrawResources.EnsureCreated(device);
		var drawArgsBuffer = useShadowBuffers
			? _gpuDrawResources.ShadowDrawArgsBuffer
			: _gpuDrawResources.DrawArgsBuffer;
		var drawCountPerBucketBuffer = useShadowBuffers
			? _gpuDrawResources.ShadowDrawCountPerBucketBuffer
			: _gpuDrawResources.DrawCountPerBucketBuffer;
		var drawExecutionRangePerBucketBuffer = useShadowBuffers
			? _gpuDrawResources.ShadowDrawExecutionRangePerBucketBuffer
			: _gpuDrawResources.DrawExecutionRangePerBucketBuffer;

		var executionLaneCount = GpuDrawExecutionLanes.ExecutionLaneCount;
		if ((uint)executionLaneCount > (DrawFlagBucketMask + 1))
		{
			throw new InvalidOperationException(
				$"Configured execution lane count {executionLaneCount} exceeds encoded bucket capacity {DrawFlagBucketMask + 1}.");
		}
		if (executionLaneCount > 32)
		{
			throw new InvalidOperationException(
				$"Configured execution lane count {executionLaneCount} exceeds the culling participation-mask capacity of 32.");
		}

		var outputViewCount = viewProjections.Length;
		Span<uint> resetCounts =
			stackalloc uint[GpuDrawResources.MaxShadowViewCount * GpuDrawExecutionLanes.ExecutionLaneCount];
		resetCounts = resetCounts[..(outputViewCount * executionLaneCount)];
		resetCounts.Clear();
		WriteBuffer<uint>(drawCountPerBucketBuffer!, resetCounts, "DrawCountPerBucketBuffer");

		Span<uint> resetRanges =
			stackalloc uint[GpuDrawResources.MaxShadowViewCount * GpuDrawExecutionLanes.ExecutionLaneCount * 2];
		resetRanges = resetRanges[..(outputViewCount * executionLaneCount * 2)];
		resetRanges.Clear();
		WriteBuffer<uint>(drawExecutionRangePerBucketBuffer!, resetRanges, "DrawExecutionRangePerBucketBuffer");

		var pipeline = EnsureCullPipeline(device);
		var commandList = context.CommandList;
		using (FrameProfiler.Instance.Measure("GpuDraw.Cull"))
		{
			commandList.BindPipeline(pipeline);

			Span<Vector4> planes = stackalloc Vector4[6];
			var cullParamsWriter = _cullParamsWriter
			                       ?? throw new InvalidOperationException(
				                       "GpuDraw cull reflection writer was not initialized.");
			cullParamsWriter.Clear();
			for (var viewIndex = 0; viewIndex < outputViewCount; viewIndex++)
			{
				ExtractFrustumPlanes(viewProjections[viewIndex], planes);
				for (var planeIndex = 0; planeIndex < planes.Length; planeIndex++)
				{
					var flattenedPlaneIndex = (viewIndex * planes.Length) + planeIndex;
					cullParamsWriter.SetVector4($"planes[{flattenedPlaneIndex}]", planes[planeIndex]);
				}
			}

			cullParamsWriter.SetVector4(
				"cameraPositionAndMaxDrawCount",
				new Vector4(
					cameraOrigin,
					_gpuDrawResources.ActiveDrawCommandUpperBound));
			cullParamsWriter.SetUInt("viewCount", (uint)outputViewCount);
			cullParamsWriter.SetUInt("bucketCount", (uint)executionLaneCount);
			cullParamsWriter.SetUInt("maxVisiblePerBucket", GpuDrawResources.MaxDrawCount);
			cullParamsWriter.SetUInt("fallbackMeshHandle", context.GpuDrawDatabase.FallbackMeshHandle.Value);
			cullParamsWriter.SetUInt("outputDrawArgsStride", GpuDrawResources.MaxDrawCount);
			cullParamsWriter.SetUInt("outputLaneStride", (uint)executionLaneCount);
			cullParamsWriter.SetUInt("participatingLaneMask", BuildParticipatingLaneMask(participation));
			commandList.SetComputeConstants(cullParamsWriter.RegisterIndex, cullParamsWriter.AsBytes());

			commandList.SetComputeBuffer(0, _gpuDrawResources.DrawCommandBuffer!);
			commandList.SetComputeBuffer(1, _gpuDrawResources.InstanceBuffer!);
			commandList.SetComputeBuffer(2, _gpuDrawResources.MeshBuffer!);
			commandList.SetComputeBuffer(3, drawArgsBuffer!);
			commandList.SetComputeBuffer(4, drawCountPerBucketBuffer!);
			commandList.SetComputeBuffer(5, drawExecutionRangePerBucketBuffer!);
			commandList.SetComputeBuffer(6, _gpuDrawResources.DrawGenerationBuffer!);
			commandList.SetComputeBuffer(7, _gpuDrawResources.InstanceGenerationBuffer!);
			commandList.SetComputeBuffer(8, _gpuDrawResources.MeshGenerationBuffer!);
			commandList.SetComputeBuffer(9, _gpuDrawResources.MaterialGenerationBuffer!);
			commandList.SetComputeBuffer(10, _gpuDrawResources.DiagnosticsCounterBuffer!);

			var threadGroupSize = _cullThreadGroupSize
			                      ?? throw new InvalidOperationException(
				                      "GpuDraw cull threadgroup size was not initialized.");
			var (groupCountX, groupCountY, groupCountZ) = threadGroupSize.GetDispatchGroupCount(
				_gpuDrawResources.ActiveDrawCommandUpperBound);
			commandList.Dispatch(groupCountX, groupCountY, groupCountZ);
		}
	}

	private static uint BuildParticipatingLaneMask(DrawPassParticipation participation)
	{
		if (participation == DrawPassParticipation.None)
		{
			return uint.MaxValue;
		}

		var laneDefinitions = GpuDrawExecutionLanes.GetDefinitionsForPass(participation);
		var mask = 0u;
		for (var i = 0; i < laneDefinitions.Length; i++)
		{
			var executionIndex = laneDefinitions[i].ExecutionIndex;
			if ((uint)executionIndex >= 32)
			{
				throw new InvalidOperationException(
					$"Execution lane {executionIndex} cannot be represented by the culling participation mask.");
			}

			mask |= 1u << executionIndex;
		}

		return mask;
	}

	/// <summary>
	/// Queues a terrain material update, replacing any earlier entry for the same material in this batch.
	/// The update shader is indexed by material, so duplicate entries are concurrent writes to one slot
	/// with a nondeterministic winner rather than an ordered last-write-wins.
	/// </summary>
	private void AddOrReplaceTerrainMaterialUpdate(in GpuTerrainMaterialUpdateData update)
	{
		if (_terrainMaterialUpdateIndices.TryGetValue(update.MaterialHandle, out var existingIndex))
		{
			_terrainMaterialUpdateData[existingIndex] = update;
			return;
		}

		_terrainMaterialUpdateIndices[update.MaterialHandle] = _terrainMaterialUpdateData.Count;
		_terrainMaterialUpdateData.Add(update);
	}

	private TerrainMaterialAllocation EnsureTerrainMaterialAllocation(uint materialHandle, uint requiredLayerCount)
	{
		requiredLayerCount = Math.Max(requiredLayerCount, 1u);

		// A full refresh clears _terrainMaterialAllocations before the update loop runs, so the first
		// request after it already allocates fresh. Bypassing the cache on top of that gave every chunk
		// sharing a material its own range - N-1 of them orphaned, since only the last is retained - and
		// left the chunks disagreeing about where their material's layers live.
		if (_terrainMaterialAllocations.TryGetValue(materialHandle, out var existingAllocation) &&
		    existingAllocation.LayerCount >= requiredLayerCount)
		{
			return existingAllocation with { Reallocated = false };
		}

		if ((uint)_nextTerrainLayerSlot + requiredLayerCount > GpuDrawResources.MaxTerrainLayerCount)
		{
			return new TerrainMaterialAllocation(0, 1, true);
		}

		var allocation = new TerrainMaterialAllocation((uint)_nextTerrainLayerSlot, requiredLayerCount, true);
		_nextTerrainLayerSlot += (int)requiredLayerCount;
		_terrainMaterialAllocations[materialHandle] = allocation;
		return allocation;
	}

	private void AppendTerrainLayerUpdates(
		uint materialHandle,
		uint layerStart,
		in TerrainDrawSurface currentSurface,
		TerrainDrawSurface? previousSurface,
		bool forceAllLayers)
	{
		var activeLayerCount = Math.Max(currentSurface.LayerCount, 1);
		for (var layerIndex = 0; layerIndex < activeLayerCount && layerIndex < currentSurface.Layers.Count; layerIndex++)
		{
			var currentLayer = currentSurface.Layers[layerIndex];
			var layerChanged = forceAllLayers ||
			                  previousSurface.HasValue == false ||
			                  layerIndex >= previousSurface.Value.Layers.Count ||
			                  !TerrainLayerEquals(currentLayer, previousSurface.Value.Layers[layerIndex]);
			if (layerChanged == false)
			{
				continue;
			}

			uint albedoHandle = 0;
			uint normalHandle = 0;
			uint ormHandle = 0;
			uint heightHandle = 0;
			uint hasHeight = 0;
			uint hasNormal = 0;
			uint hasOrm = 0;
			float scale = 1.0f;
			uint autoMaterial = 0;
			uint useMinimumSlope = 0;
			float minimumSlopeDegrees = 0.0f;
			PopulateTerrainLayerHandles(
				currentLayer,
				ref albedoHandle,
				ref normalHandle,
				ref ormHandle,
				ref heightHandle,
				ref hasHeight,
				ref hasNormal,
				ref hasOrm,
				ref scale,
				ref autoMaterial,
				ref useMinimumSlope,
				ref minimumSlopeDegrees);
			_terrainLayerUpdateData.Add(new GpuTerrainLayerUpdateData(
				materialHandle,
				layerStart,
				(uint)layerIndex,
				albedoHandle,
				normalHandle,
				ormHandle,
				heightHandle,
				hasHeight,
				scale,
				autoMaterial,
				useMinimumSlope,
				minimumSlopeDegrees,
				hasNormal,
				hasOrm));
		}
	}

	private static bool TerrainLayerEquals(in TerrainResolvedLayer left, in TerrainResolvedLayer right)
	{
		return ReferenceEquals(left.Albedo, right.Albedo) &&
		       left.AlbedoResourceRevision == right.AlbedoResourceRevision &&
		       ReferenceEquals(left.Normal, right.Normal) &&
		       left.NormalResourceRevision == right.NormalResourceRevision &&
		       ReferenceEquals(left.Orm, right.Orm) &&
		       left.OrmResourceRevision == right.OrmResourceRevision &&
		       ReferenceEquals(left.Height, right.Height) &&
		       left.HeightResourceRevision == right.HeightResourceRevision &&
		       Math.Abs(left.Scale - right.Scale) <= 0.0001f &&
		       left.AutoMaterial == right.AutoMaterial &&
		       left.UseMinimumSlope == right.UseMinimumSlope &&
		       Math.Abs(left.MinimumSlopeDegrees - right.MinimumSlopeDegrees) <= 0.0001f;
	}

	private void DispatchInstanceUpdates(RenderGraphContext context, IGfxDevice device)
	{
		if (_instanceUpdateData.Count == 0)
		{
			return;
		}

		WriteBuffer<GpuDrawInstanceUpdateData>(_gpuDrawResources.InstanceUpdateBuffer!, CollectionsMarshal.AsSpan(_instanceUpdateData), "InstanceUpdateBuffer");
		DispatchUpdatePass(
			context,
			EnsureInstanceUpdatePipeline(device),
			_instanceUpdateParamsWriter!,
			_instanceUpdateThreadGroupSize!.Value,
			(uint)_instanceUpdateData.Count,
			commandList =>
			{
				commandList.SetComputeBuffer(0, _gpuDrawResources.InstanceUpdateBuffer!);
				commandList.SetComputeBuffer(1, _gpuDrawResources.InstanceBuffer!);
				commandList.SetComputeBuffer(2, _gpuDrawResources.DrawCommandBuffer!);
				commandList.SetComputeBuffer(3, _gpuDrawResources.DrawGenerationBuffer!);
				commandList.SetComputeBuffer(4, _gpuDrawResources.InstanceGenerationBuffer!);
				commandList.SetComputeBuffer(5, _gpuDrawResources.DiagnosticsCounterBuffer!);
			});
	}

	private void DispatchMeshUpdates(RenderGraphContext context, IGfxDevice device)
	{
		if (_meshUpdateData.Count == 0)
		{
			return;
		}

		WriteBuffer<GpuDrawMeshUpdateData>(_gpuDrawResources.MeshUpdateBuffer!, CollectionsMarshal.AsSpan(_meshUpdateData), "MeshUpdateBuffer");
		DispatchUpdatePass(
			context,
			EnsureMeshUpdatePipeline(device),
			_meshUpdateParamsWriter!,
			_meshUpdateThreadGroupSize!.Value,
			(uint)_meshUpdateData.Count,
			commandList =>
			{
				commandList.SetComputeBuffer(0, _gpuDrawResources.MeshUpdateBuffer!);
				commandList.SetComputeBuffer(1, _gpuDrawResources.MeshBuffer!);
				commandList.SetComputeBuffer(2, _gpuDrawResources.MeshGenerationBuffer!);
				commandList.SetComputeBuffer(3, _gpuDrawResources.DiagnosticsCounterBuffer!);
			});
	}

	private void DispatchMaterialUpdates(RenderGraphContext context, IGfxDevice device)
	{
		if (_materialUpdateData.Count == 0)
		{
			return;
		}

		WriteBuffer<GpuDrawMaterialUpdateData>(_gpuDrawResources.MaterialUpdateBuffer!, CollectionsMarshal.AsSpan(_materialUpdateData), "MaterialUpdateBuffer");
		DispatchUpdatePass(
			context,
			EnsureMaterialUpdatePipeline(device),
			_materialUpdateParamsWriter!,
			_materialUpdateThreadGroupSize!.Value,
			(uint)_materialUpdateData.Count,
			commandList =>
			{
				commandList.SetComputeBuffer(0, _gpuDrawResources.MaterialUpdateBuffer!);
				commandList.SetComputeBuffer(1, _gpuDrawResources.MaterialBuffer!);
				commandList.SetComputeBuffer(2, _gpuDrawResources.MaterialGenerationBuffer!);
				commandList.SetComputeBuffer(3, _gpuDrawResources.DiagnosticsCounterBuffer!);
			});
	}

	private void DispatchTerrainMaterialUpdates(RenderGraphContext context, IGfxDevice device)
	{
		if (_terrainMaterialUpdateData.Count == 0)
		{
			return;
		}

		WriteBuffer<GpuTerrainMaterialUpdateData>(_gpuDrawResources.TerrainMaterialUpdateBuffer!, CollectionsMarshal.AsSpan(_terrainMaterialUpdateData), "TerrainMaterialUpdateBuffer");
		DispatchUpdatePass(
			context,
			EnsureTerrainMaterialUpdatePipeline(device),
			_terrainMaterialUpdateParamsWriter!,
			_terrainMaterialUpdateThreadGroupSize!.Value,
			(uint)_terrainMaterialUpdateData.Count,
			commandList =>
			{
				var slots = _terrainMaterialUpdateBindings?.Slots
					?? throw new InvalidOperationException("Terrain material update bindings were not reflected.");
				commandList.SetComputeBuffer(slots[0], _gpuDrawResources.TerrainMaterialUpdateBuffer!);
				commandList.SetComputeBuffer(slots[1], _gpuDrawResources.TerrainMaterialBuffer!);
				commandList.SetComputeBuffer(slots[2], _gpuDrawResources.MaterialGenerationBuffer!);
				commandList.SetComputeBuffer(slots[3], _gpuDrawResources.DiagnosticsCounterBuffer!);
			});
		GetPackedHandleIndex(_terrainMaterialUpdateData[0].MaterialHandle);
	}

	private void DispatchTerrainLayerUpdates(RenderGraphContext context, IGfxDevice device)
	{
		if (_terrainLayerUpdateData.Count == 0)
		{
			return;
		}

		WriteBuffer<GpuTerrainLayerUpdateData>(_gpuDrawResources.TerrainLayerUpdateBuffer!, CollectionsMarshal.AsSpan(_terrainLayerUpdateData), "TerrainLayerUpdateBuffer");
		DispatchUpdatePass(
			context,
			EnsureTerrainLayerUpdatePipeline(device),
			_terrainLayerUpdateParamsWriter!,
			_terrainLayerUpdateThreadGroupSize!.Value,
			(uint)_terrainLayerUpdateData.Count,
			commandList =>
			{
				var slots = _terrainLayerUpdateBindings?.Slots
					?? throw new InvalidOperationException("Terrain layer update bindings were not reflected.");
				commandList.SetComputeBuffer(slots[0], _gpuDrawResources.TerrainLayerUpdateBuffer!);
				commandList.SetComputeBuffer(slots[1], _gpuDrawResources.TerrainLayerBuffer!);
				commandList.SetComputeBuffer(slots[2], _gpuDrawResources.MaterialGenerationBuffer!);
				commandList.SetComputeBuffer(slots[3], _gpuDrawResources.DiagnosticsCounterBuffer!);
			});
	}

	private void DispatchUpdatePass(
		RenderGraphContext context,
		IGfxPipeline pipeline,
		ShaderPropertyWriter updateParamsWriter,
		ComputeThreadGroupSize threadGroupSize,
		uint updateCount,
		Action<IGfxCommandList> bindBuffers)
	{
		var commandList = context.CommandList;
		using (FrameProfiler.Instance.Measure("GpuDraw.Update"))
		{
			commandList.BindPipeline(pipeline);
			updateParamsWriter.Clear();
			updateParamsWriter.SetUInt("updateCount", updateCount);
			commandList.SetComputeConstants(updateParamsWriter.RegisterIndex, updateParamsWriter.AsBytes());
			bindBuffers(commandList);
			var (groupCountX, groupCountY, groupCountZ) = threadGroupSize.GetDispatchGroupCount(updateCount);
			commandList.Dispatch(groupCountX, groupCountY, groupCountZ);
		}
	}

	private IGfxPipeline EnsureInstanceUpdatePipeline(IGfxDevice device)
	{
		if (_instanceUpdatePipeline is not null)
		{
			return _instanceUpdatePipeline;
		}

		EnsureComputeReflectionResources(device.BackendKind);
		var pipelineKey = new PipelineKey(PassKind.Compute, null, null, "CSUpdateInstance", default, default, default);
		_instanceUpdatePipeline = device.GetOrCreatePipeline(
			pipelineKey,
			new ShaderBytecodeSet(compute: _instanceUpdateShaderBytecode, computeThreadGroupSize: _instanceUpdateThreadGroupSize));
		return _instanceUpdatePipeline;
	}

	private IGfxPipeline EnsureMeshUpdatePipeline(IGfxDevice device)
	{
		if (_meshUpdatePipeline is not null)
		{
			return _meshUpdatePipeline;
		}

		EnsureComputeReflectionResources(device.BackendKind);
		var pipelineKey = new PipelineKey(PassKind.Compute, null, null, "CSUpdateMesh", default, default, default);
		_meshUpdatePipeline = device.GetOrCreatePipeline(
			pipelineKey,
			new ShaderBytecodeSet(compute: _meshUpdateShaderBytecode, computeThreadGroupSize: _meshUpdateThreadGroupSize));
		return _meshUpdatePipeline;
	}

	private IGfxPipeline EnsureMaterialUpdatePipeline(IGfxDevice device)
	{
		if (_materialUpdatePipeline is not null)
		{
			return _materialUpdatePipeline;
		}

		EnsureComputeReflectionResources(device.BackendKind);
		var pipelineKey = new PipelineKey(PassKind.Compute, null, null, "CSUpdateMaterial", default, default, default);
		_materialUpdatePipeline = device.GetOrCreatePipeline(
			pipelineKey,
			new ShaderBytecodeSet(compute: _materialUpdateShaderBytecode, computeThreadGroupSize: _materialUpdateThreadGroupSize));
		return _materialUpdatePipeline;
	}

	private IGfxPipeline EnsureTerrainMaterialUpdatePipeline(IGfxDevice device)
	{
		if (_terrainMaterialUpdatePipeline is not null)
		{
			return _terrainMaterialUpdatePipeline;
		}

		EnsureComputeReflectionResources(device.BackendKind);
		var pipelineKey = new PipelineKey(PassKind.Compute, null, null, "CSUpdateTerrainMaterial", default, default, default);
		_terrainMaterialUpdatePipeline = device.GetOrCreatePipeline(
			pipelineKey,
			new ShaderBytecodeSet(compute: _terrainMaterialUpdateShaderBytecode, computeThreadGroupSize: _terrainMaterialUpdateThreadGroupSize));
		return _terrainMaterialUpdatePipeline;
	}

	private IGfxPipeline EnsureTerrainLayerUpdatePipeline(IGfxDevice device)
	{
		if (_terrainLayerUpdatePipeline is not null)
		{
			return _terrainLayerUpdatePipeline;
		}

		EnsureComputeReflectionResources(device.BackendKind);
		var pipelineKey = new PipelineKey(PassKind.Compute, null, null, "CSUpdateTerrainLayer", default, default, default);
		_terrainLayerUpdatePipeline = device.GetOrCreatePipeline(
			pipelineKey,
			new ShaderBytecodeSet(compute: _terrainLayerUpdateShaderBytecode, computeThreadGroupSize: _terrainLayerUpdateThreadGroupSize));
		return _terrainLayerUpdatePipeline;
	}

	private IGfxPipeline EnsureCullPipeline(IGfxDevice device)
	{
		if (_cullPipeline is not null)
		{
			return _cullPipeline;
		}

		EnsureComputeReflectionResources(device.BackendKind);
		var pipelineKey = new PipelineKey(PassKind.Compute, null, null, "CSCull", default, default, default);
		_cullPipeline = device.GetOrCreatePipeline(
			pipelineKey,
			new ShaderBytecodeSet(compute: _cullShaderBytecode, computeThreadGroupSize: _cullThreadGroupSize));
		return _cullPipeline;
	}

	/// <summary>
	/// Compacts a command set's visible draws into dense per-page command lists so the following
	/// ExecuteIndirect walks only what survived culling. Must run after the cull dispatch that produced
	/// <paramref name="drawArgsBuffer"/> and after the pass's commands have been encoded.
	/// </summary>
	/// <param name="drawArgsBaseOffsetBytes">
	/// Byte offset of this view's slice of the draw args buffer, so shadow cascades compact against
	/// their own visibility rather than the first cascade's.
	/// </param>
	/// <returns>False when the backend cannot compact, meaning the caller must execute the full range.</returns>
	public bool RecordIndirectCompaction(
		RenderGraphContext context,
		SharedDrawIndirectCommandSet commandSet,
		DrawPassParticipation participation,
		IGfxBuffer? drawArgsBuffer,
		ulong drawArgsBaseOffsetBytes,
		Func<GpuDrawExecutionLaneDefinition, bool> laneAvailable)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(commandSet);
		ArgumentNullException.ThrowIfNull(laneAvailable);

		if (drawArgsBuffer is null ||
		    _gpuDrawResources.DrawCommandBuffer is not { } drawCommandBuffer ||
		    _supportsIndirectStructuralUpdates == false)
		{
			return false;
		}

		var device = _renderer.GetGfxDevice();
		commandSet.EnsureCreated(device);
		var executionRangeBuffer = commandSet.CompactedExecutionRangeBuffer;
		if (executionRangeBuffer is null)
		{
			return false;
		}

		var activeSlot = _gpuDrawResources.ActiveIndirectCommandSlot;
		var laneDefinitions = GpuDrawExecutionLanes.GetDefinitionsForPass(participation);
		if (ResolveCompactionKind(commandSet, activeSlot, laneDefinitions, laneAvailable) is not { } compactionKind)
		{
			// Nothing has been encoded for this pass yet, so there is nothing to compact and nothing to
			// say about it. Executing the empty full range costs the same as executing an empty compaction.
			return false;
		}

		if (compactionKind == IndirectCompactionKind.None)
		{
			LogCompactionUnavailableOnce(participation);
			return false;
		}

		var commandList = context.CommandList;
		using (FrameProfiler.Instance.Measure("GpuDraw.Compact"))
		{
			// Both resets leave every range's location field at the zero compaction emits from, and both
			// have to land ahead of the dispatches below. The record-copy path gets that from the upload
			// copy its CPU write stages, whose transition back to UnorderedAccess orders it; the native
			// path has no such copy, so the backend clears the table on the GPU timeline instead.
			if (compactionKind == IndirectCompactionKind.CommandRecords)
			{
				WriteBuffer<uint>(
					executionRangeBuffer,
					RentExecutionRangeReset(),
					"SharedDrawCompactedExecutionRanges");
				commandList.BindPipeline(EnsureCompactPipeline(device));
			}
			else
			{
				commandList.ResetNativeIndirectCompactionRanges(executionRangeBuffer);
			}

			for (var i = 0; i < laneDefinitions.Length; i++)
			{
				var lane = laneDefinitions[i];
				if (laneAvailable(lane) == false)
				{
					continue;
				}

				var pages = commandSet.GetAllocatedPages(activeSlot, lane.ExecutionIndex);
				for (var pageIndex = 0; pageIndex < pages.Length; pageIndex++)
				{
					var page = pages[pageIndex];
					if (_gpuDrawResources.ActiveDrawCommandUpperBound <= page.PageStartCommandIndex)
					{
						continue;
					}

					var rangeIndex = SharedDrawIndirectCommandSet.GetCompactedExecutionRangeIndex(
						activeSlot,
						lane.ExecutionIndex,
						page.PageIndex);
					var request = new NativeIndirectCompactionRequest(
						page.CommandBuffer,
						executionRangeBuffer,
						(uint)rangeIndex,
						drawArgsBuffer,
						drawArgsBaseOffsetBytes,
						drawCommandBuffer,
						page.PageStartCommandIndex,
						page.PageCommandCapacity,
						(uint)lane.ExecutionIndex,
						_gpuDrawResources.ActiveDrawCommandUpperBound);

					if (compactionKind == IndirectCompactionKind.NativeCommands)
					{
						commandList.RecordNativeIndirectCompaction(request);
						continue;
					}

					RecordRecordCopyCompaction(commandList, in request);
				}
			}
		}

		return true;
	}

	/// <summary>
	/// Dispatches the shared kernel that copies a page's surviving command records into its dense list.
	/// </summary>
	private void RecordRecordCopyCompaction(IGfxCommandList commandList, in NativeIndirectCompactionRequest request)
	{
		var templateBuffer = request.CommandBuffer.TemplateRecordBuffer;
		var compactedBuffer = request.CommandBuffer.CompactedRecordBuffer;
		if (templateBuffer is null || compactedBuffer is null)
		{
			return;
		}

		var compactParamsWriter = _compactParamsWriter
		                          ?? throw new InvalidOperationException(
			                          "GpuDraw compaction reflection writer was not initialized.");
		var threadGroupSize = _compactThreadGroupSize
		                      ?? throw new InvalidOperationException(
			                      "GpuDraw compaction threadgroup size was not initialized.");

		compactParamsWriter.Clear();
		compactParamsWriter.SetUInt("pageStartCommandIndex", request.PageStartCommandIndex);
		compactParamsWriter.SetUInt("pageCommandCapacity", request.PageCommandCapacity);
		compactParamsWriter.SetUInt("laneIndex", request.LaneIndex);
		compactParamsWriter.SetUInt("executionRangeIndex", request.ExecutionRangeIndex);
		compactParamsWriter.SetUInt("recordStrideUints", request.CommandBuffer.RecordStrideInBytes / sizeof(uint));
		compactParamsWriter.SetUInt(
			"recordIndexCountUintOffset",
			request.CommandBuffer.RecordIndexCountOffsetInBytes / sizeof(uint));
		compactParamsWriter.SetUInt("activeDrawCommandUpperBound", request.ActiveDrawCommandUpperBound);
		commandList.SetComputeConstants(compactParamsWriter.RegisterIndex, compactParamsWriter.AsBytes());

		commandList.SetComputeBuffer(0, compactedBuffer);
		commandList.SetComputeBuffer(1, request.ExecutionRangeBuffer);
		commandList.SetComputeReadOnlyBuffer(2, templateBuffer);
		commandList.SetComputeReadOnlyBuffer(3, request.DrawArgsBuffer, request.DrawArgsBaseOffsetBytes);
		commandList.SetComputeReadOnlyBuffer(4, request.DrawCommandBuffer);

		var (groupCountX, groupCountY, groupCountZ) =
			threadGroupSize.GetDispatchGroupCount(request.PageCommandCapacity);
		commandList.Dispatch(groupCountX, groupCountY, groupCountZ);
	}

	/// <summary>
	/// The zeroed range table, kept as a field because it is sized by the lane and page counts and would
	/// otherwise be a multi-kilobyte stack allocation on every compacting pass.
	/// </summary>
	private uint[] RentExecutionRangeReset()
	{
		var length = SharedDrawIndirectCommandSet.CompactedExecutionRangeEntryCount *
		             (IndirectCompactionExecutionRange.StrideInBytes / sizeof(uint));
		if (_executionRangeReset is null || _executionRangeReset.Length != length)
		{
			_executionRangeReset = new uint[length];
			return _executionRangeReset;
		}

		Array.Clear(_executionRangeReset);
		return _executionRangeReset;
	}

	/// <summary>
	/// Compaction is all-or-nothing per command set: a lane left on the full-range path would execute
	/// stale commands that the range table no longer describes. Pages also have to agree on how they
	/// compact, since one dispatch shape has to cover the whole set.
	/// </summary>
	/// <returns>
	/// Null when the set has no pages to compact, which is not a backend limitation and must not be
	/// reported as one.
	/// </returns>
	private static IndirectCompactionKind? ResolveCompactionKind(
		SharedDrawIndirectCommandSet commandSet,
		int activeSlot,
		ReadOnlySpan<GpuDrawExecutionLaneDefinition> laneDefinitions,
		Func<GpuDrawExecutionLaneDefinition, bool> laneAvailable)
	{
		var resolved = (IndirectCompactionKind?)null;
		for (var i = 0; i < laneDefinitions.Length; i++)
		{
			var lane = laneDefinitions[i];
			if (laneAvailable(lane) == false)
			{
				continue;
			}

			var pages = commandSet.GetAllocatedPages(activeSlot, lane.ExecutionIndex);
			for (var pageIndex = 0; pageIndex < pages.Length; pageIndex++)
			{
				var kind = pages[pageIndex].CommandBuffer.CompactionKind;
				if (kind == IndirectCompactionKind.None || (resolved is { } previous && previous != kind))
				{
					return IndirectCompactionKind.None;
				}

				resolved = kind;
			}
		}

		return resolved;
	}

	private IGfxPipeline EnsureCompactPipeline(IGfxDevice device)
	{
		if (_compactPipeline is not null)
		{
			return _compactPipeline;
		}

		EnsureComputeReflectionResources(device.BackendKind);
		var pipelineKey = new PipelineKey(PassKind.Compute, null, null, "CSCompact", default, default, default);
		_compactPipeline = device.GetOrCreatePipeline(
			pipelineKey,
			new ShaderBytecodeSet(compute: _compactShaderBytecode, computeThreadGroupSize: _compactThreadGroupSize));
		return _compactPipeline;
	}

	private void EnsureComputeReflectionResources(GraphicsBackendKind backendKind)
	{
		if (_computeReflectionBackendKind.HasValue &&
		    _computeReflectionBackendKind.Value == backendKind &&
		    _instanceUpdateParamsWriter is not null &&
		    _meshUpdateParamsWriter is not null &&
		    _materialUpdateParamsWriter is not null &&
		    _terrainMaterialUpdateParamsWriter is not null &&
		    _terrainLayerUpdateParamsWriter is not null &&
		    _cullParamsWriter is not null &&
		    _instanceUpdateThreadGroupSize.HasValue &&
		    _meshUpdateThreadGroupSize.HasValue &&
		    _materialUpdateThreadGroupSize.HasValue &&
		    _terrainMaterialUpdateThreadGroupSize.HasValue &&
		    _terrainLayerUpdateThreadGroupSize.HasValue &&
		    _cullThreadGroupSize.HasValue &&
		    _compactThreadGroupSize.HasValue &&
		    _compactParamsWriter is not null &&
		    _compactShaderBytecode.IsEmpty == false &&
		    _instanceUpdateShaderBytecode.IsEmpty == false &&
		    _meshUpdateShaderBytecode.IsEmpty == false &&
		    _materialUpdateShaderBytecode.IsEmpty == false &&
		    _terrainMaterialUpdateShaderBytecode.IsEmpty == false &&
		    _terrainLayerUpdateShaderBytecode.IsEmpty == false &&
		    _cullShaderBytecode.IsEmpty == false)
		{
			return;
		}

		var instanceUpdateCompiled = _shaderCompiler.GetComputeShaderWithReflection(
			EngineShaderPrograms.GpuDrawInstanceUpdate,
			"CSUpdateInstance",
			backendKind);
		_instanceUpdateShaderBytecode = instanceUpdateCompiled.Bytecode;
		_instanceUpdateThreadGroupSize = instanceUpdateCompiled.ThreadGroupSize;
		_instanceUpdateParamsWriter =
			new ShaderPropertyWriter(instanceUpdateCompiled.ReflectionLayout.GetConstantBuffer("UpdateParams"));

		var meshUpdateCompiled = _shaderCompiler.GetComputeShaderWithReflection(
			EngineShaderPrograms.GpuDrawMeshUpdate,
			"CSUpdateMesh",
			backendKind);
		_meshUpdateShaderBytecode = meshUpdateCompiled.Bytecode;
		_meshUpdateThreadGroupSize = meshUpdateCompiled.ThreadGroupSize;
		_meshUpdateParamsWriter =
			new ShaderPropertyWriter(meshUpdateCompiled.ReflectionLayout.GetConstantBuffer("UpdateParams"));

		var materialUpdateCompiled = _shaderCompiler.GetComputeShaderWithReflection(
			EngineShaderPrograms.GpuDrawMaterialUpdate,
			"CSUpdateMaterial",
			backendKind);
		_materialUpdateShaderBytecode = materialUpdateCompiled.Bytecode;
		_materialUpdateThreadGroupSize = materialUpdateCompiled.ThreadGroupSize;
		_materialUpdateParamsWriter =
			new ShaderPropertyWriter(materialUpdateCompiled.ReflectionLayout.GetConstantBuffer("UpdateParams"));

		var terrainMaterialUpdateCompiled = _shaderCompiler.GetComputeShaderWithReflection(
			EngineShaderPrograms.GpuDrawTerrainMaterialUpdate,
			"CSUpdateTerrainMaterial",
			backendKind);
		_terrainMaterialUpdateShaderBytecode = terrainMaterialUpdateCompiled.Bytecode;
		_terrainMaterialUpdateThreadGroupSize = terrainMaterialUpdateCompiled.ThreadGroupSize;
		_terrainMaterialUpdateParamsWriter =
			new ShaderPropertyWriter(terrainMaterialUpdateCompiled.ReflectionLayout.GetConstantBuffer("UpdateParams"));
		_terrainMaterialUpdateBindings = new ComputeResourceBindings(
			terrainMaterialUpdateCompiled.ReflectionLayout.GetResource("g_Updates").RegisterIndex,
			terrainMaterialUpdateCompiled.ReflectionLayout.GetResource("g_TerrainMaterialTable").RegisterIndex,
			terrainMaterialUpdateCompiled.ReflectionLayout.GetResource("g_MaterialGenerations").RegisterIndex,
			terrainMaterialUpdateCompiled.ReflectionLayout.GetResource("g_Diagnostics").RegisterIndex);

		var terrainLayerUpdateCompiled = _shaderCompiler.GetComputeShaderWithReflection(
			EngineShaderPrograms.GpuDrawTerrainLayerUpdate,
			"CSUpdateTerrainLayer",
			backendKind);
		_terrainLayerUpdateShaderBytecode = terrainLayerUpdateCompiled.Bytecode;
		_terrainLayerUpdateThreadGroupSize = terrainLayerUpdateCompiled.ThreadGroupSize;
		_terrainLayerUpdateParamsWriter =
			new ShaderPropertyWriter(terrainLayerUpdateCompiled.ReflectionLayout.GetConstantBuffer("UpdateParams"));
		_terrainLayerUpdateBindings = new ComputeResourceBindings(
			terrainLayerUpdateCompiled.ReflectionLayout.GetResource("g_Updates").RegisterIndex,
			terrainLayerUpdateCompiled.ReflectionLayout.GetResource("g_TerrainLayerTable").RegisterIndex,
			terrainLayerUpdateCompiled.ReflectionLayout.GetResource("g_MaterialGenerations").RegisterIndex,
			terrainLayerUpdateCompiled.ReflectionLayout.GetResource("g_Diagnostics").RegisterIndex);

		var cullCompiled = _shaderCompiler.GetComputeShaderWithReflection(
			EngineShaderPrograms.GpuDrawCull,
			"CSCull",
			backendKind);
		_cullShaderBytecode = cullCompiled.Bytecode;
		_cullThreadGroupSize = cullCompiled.ThreadGroupSize;
		_cullParamsWriter = new ShaderPropertyWriter(cullCompiled.ReflectionLayout.GetConstantBuffer("CullParams"));

		var compactCompiled = _shaderCompiler.GetComputeShaderWithReflection(
			EngineShaderPrograms.GpuDrawCompact,
			"CSCompact",
			backendKind);
		_compactShaderBytecode = compactCompiled.Bytecode;
		_compactThreadGroupSize = compactCompiled.ThreadGroupSize;
		_compactParamsWriter = new ShaderPropertyWriter(compactCompiled.ReflectionLayout.GetConstantBuffer("CompactParams"));

		_computeReflectionBackendKind = backendKind;
	}

	private void EnsureGBufferPipelines(IGfxDevice device)
	{
		var laneDefinitions = GpuDrawExecutionLanes.GetDefinitionsForPass(DrawPassParticipation.GBuffer);
		var allPipelinesReady = true;
		for (var i = 0; i < laneDefinitions.Length; i++)
		{
			var lane = laneDefinitions[i];
			if (GetGBufferPipeline(lane) is null)
			{
				allPipelinesReady = false;
				break;
			}
		}

		if (allPipelinesReady)
		{
			return;
		}

		var renderState = new RenderStateDescriptor(
			FillMode.Solid,
			CullMode.Back,
			depthTestEnabled: true,
			depthWriteEnabled: true,
			BlendMode.Opaque);
		for (var i = 0; i < laneDefinitions.Length; i++)
		{
			var lane = laneDefinitions[i];
			if (GetGBufferPipeline(lane) is not null)
			{
				continue;
			}

			var compiled = GraphicsShaderCompiler.CompileWithReflection(
				_shaderCompiler,
				device.BackendKind,
				GetGBufferShaderPath(lane.DrawKind),
				"vertexShader",
				"fragmentShader",
				lane.PreprocessorDefine);
			var shaderSet = compiled.Bytecode;
			var pipelineKey = new PipelineKey(
				PassKind.Graphics,
				vertexEntryPoint: "vertexShader",
				pixelEntryPoint: "fragmentShader",
				computeEntryPoint: null,
				renderTargets: new(new[]
				{
					TextureFormat.Bgra8Unorm,
					TextureFormat.Rgba16Float,
					TextureFormat.Rgba8Unorm,
					TextureFormat.Rgba16Float,
					TextureFormat.Rgba16Float
				}),
				depthStencil: new DepthStencilFormat(TextureFormat.D32Float),
				renderState: renderState,
				layout: GraphicsLayoutKind.Material,
				shaderVariant: $"GBuffer:{lane.ShaderVariant}");
			_gbufferPipelines[lane.ExecutionIndex] = device.GetOrCreatePipeline(pipelineKey, shaderSet);
			var bindings = SharedDrawGraphicsBufferBindings.FromGBufferReflection(compiled.ReflectionLayout);
			_gbufferBufferBindings[lane.ExecutionIndex] = bindings;
			_gbufferReflections[lane.ExecutionIndex] = compiled.ReflectionLayout;
		}
	}

	private GraphicsPassBindingSet? CreateGBufferPassBindingSet(GpuDrawExecutionLaneDefinition lane)
	{
		var reflection = _gbufferReflections[lane.ExecutionIndex];
		if (reflection is null)
		{
			return null;
		}

		return GraphicsPassBindingSet.FromReflection(reflection,
			new Dictionary<string, IGfxBuffer?>(StringComparer.Ordinal)
			{
				["CameraParams"] = _gpuDrawResources.CameraBuffer,
				["g_TerrainMaterialTable"] = _gpuDrawResources.TerrainMaterialBuffer,
				["g_TerrainLayerTable"] = _gpuDrawResources.TerrainLayerBuffer
			},
			SharedDrawPerDrawBindings.ResourceNames);
	}

	private IGfxPipeline? GetPrimaryGBufferPipeline()
	{
		var laneDefinitions = GpuDrawExecutionLanes.GetDefinitionsForPass(DrawPassParticipation.GBuffer);
		for (var i = 0; i < laneDefinitions.Length; i++)
		{
			var pipeline = GetGBufferPipeline(laneDefinitions[i]);
			if (pipeline is not null)
			{
				return pipeline;
			}
		}

		return null;
	}

	private IGfxPipeline? GetGBufferPipeline(GpuDrawExecutionLaneDefinition lane) => _gbufferPipelines[lane.ExecutionIndex];

	private static void WriteBuffer<T>(IGfxBuffer buffer, ReadOnlySpan<T> data, string bufferName) where T : unmanaged
	{
		if (buffer is not IWritableGpuBuffer writableBuffer)
		{
			throw new NotImplementedException(
				$"Buffer '{bufferName}' does not support CPU writes on this backend.");
		}

		writableBuffer.Write(data);
	}

	/// <summary>
	/// Dumps the CPU-side inputs that decide where a terrain draw's vertices end up, so a hang in the
	/// terrain GBuffer lane can be split into "the authoring path queued nonsense" and "the data was fine
	/// on the way in". Entries that cannot produce sane geometry are always reported; healthy ones are
	/// rate-limited so a repaint of a many-chunk terrain stays readable.
	/// </summary>
	private void LogTerrainDrawDataIfEnabled(
		in GpuDrawUpdate update,
		GpuDrawKind drawKind,
		uint heightmapHandle,
		uint layerIndexMapHandle,
		uint layerWeightMapHandle,
		uint layerStart,
		uint layerCount,
		float heightScale,
		uint indexCount,
		int baseVertex,
		bool surfaceReady)
	{
		if (GraphicsConfig.LogTerrainDrawData == false || drawKind != GpuDrawKind.Terrain)
		{
			return;
		}

		var chunk = update.TerrainInstanceData.ChunkOriginSize;
		var uv = update.TerrainInstanceData.HeightmapUvScaleOffset;
		var problems = new List<string>();

		if (IsFinite(chunk) == false)
		{
			problems.Add("chunkOriginSize is not finite");
		}
		else if (chunk.Z <= 0.0f || chunk.W <= 0.0f)
		{
			problems.Add($"chunk size is degenerate ({chunk.Z}x{chunk.W})");
		}

		if (IsFinite(uv) == false)
		{
			problems.Add("heightmapUvScaleOffset is not finite");
		}

		if (float.IsFinite(heightScale) == false)
		{
			problems.Add("heightScale is not finite");
		}
		else if (heightScale < 0.0f)
		{
			problems.Add($"heightScale is negative ({heightScale})");
		}

		if (layerCount == 0)
		{
			problems.Add("layerCount is zero");
		}
		else if ((ulong)layerStart + layerCount > (ulong)GpuDrawResources.MaxTerrainLayerCount)
		{
			problems.Add($"layer range {layerStart}..{layerStart + layerCount} overruns the layer table");
		}

		if (indexCount == 0)
		{
			problems.Add("indexCount is zero");
		}

		if (update.Type != GpuDrawUpdateType.Remove && update.TerrainSurface is null)
		{
			problems.Add("no terrain surface, so the material table keeps whatever it held");
		}

		if (update.Type != GpuDrawUpdateType.Remove && update.TerrainSurface is not null && surfaceReady == false)
		{
			problems.Add("heightmap is not bound yet, so the draw is held back");
		}

		if (baseVertex < 0)
		{
			problems.Add($"baseVertex is negative ({baseVertex})");
		}

		if (problems.Count == 0 && _terrainLogHealthyBudget <= 0)
		{
			return;
		}

		if (problems.Count == 0)
		{
			_terrainLogHealthyBudget--;
		}

		var verdict = problems.Count == 0 ? "ok" : $"SUSPECT: {string.Join("; ", problems)}";
		Console.WriteLine(
			$"[terrain draw] draw={update.DrawHandle.Value} material={update.MaterialHandle.Value} type={update.Type} " +
			$"chunkOrigin=({chunk.X}, {chunk.Y}) chunkSize=({chunk.Z}, {chunk.W}) " +
			$"uvScaleOffset=({uv.X}, {uv.Y}, {uv.Z}, {uv.W}) heightScale={heightScale} " +
			$"heightmapHandle={heightmapHandle} layerMapHandles=({layerIndexMapHandle},{layerWeightMapHandle}) " +
			$"layers={layerStart}+{layerCount} " +
			$"indexCount={indexCount} baseVertex={baseVertex} {verdict}");
	}

	private static bool IsFinite(Vector4 value) =>
		float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);

	private int AdvanceActiveIndirectSlot()
	{
		_activeIndirectSlot = (_activeIndirectSlot + 1) % GpuDrawResources.IndirectCommandBufferSlotCount;
		return _activeIndirectSlot;
	}

	private static bool IsStructuralUpdateType(GpuDrawUpdateType type) =>
		type is GpuDrawUpdateType.Add
			or GpuDrawUpdateType.Remove
			or GpuDrawUpdateType.UpdateMaterial
			or GpuDrawUpdateType.UpdateMesh;

	/// <summary>
	/// Count buffer for the GBuffer command set when compaction ran this frame, otherwise null so the
	/// pass falls back to executing its full command range.
	/// </summary>
	public IGfxBuffer? GBufferCompactedExecutionRangeBuffer =>
		_gbufferCompactionActive ? _gbufferIndirectCommandSet.CompactedExecutionRangeBuffer : null;

	/// <summary>
	/// Encodes the GBuffer commands and compacts them against the camera cull results. Runs in the cull
	/// pass rather than the GBuffer pass because compaction is compute work that has to complete before
	/// the render pass that consumes it begins.
	/// </summary>
	public void RecordGBufferIndirectCompaction(RenderGraphContext context)
	{
		ArgumentNullException.ThrowIfNull(context);
		EnsureGBufferIndirectCommands(context);
		_gbufferCompactionActive = RecordIndirectCompaction(
			context,
			_gbufferIndirectCommandSet,
			DrawPassParticipation.GBuffer,
			_gpuDrawResources.DrawArgsBuffer,
			drawArgsBaseOffsetBytes: 0,
			static _ => true);
	}

	public void EnsureGBufferIndirectCommands(RenderGraphContext context)
	{
		ArgumentNullException.ThrowIfNull(context);
		var device = _renderer.GetGfxDevice();
		EnsureGBufferPipelines(device);
		EnsureIndirectCommandsForPass(
			context.GpuDrawDatabase,
			_gbufferIndirectCommandSet,
			DrawPassParticipation.GBuffer,
			SharedDrawIndirectEncodeResources.FromGpuDrawResources(_gpuDrawResources),
			static _ => true,
			lane => _gbufferBufferBindings[lane.ExecutionIndex],
			CreateGBufferPassBindingSet);
	}

	public void EnsureIndirectCommandsForPass(
		GpuDrawDatabase drawDatabase,
		SharedDrawIndirectCommandSet commandSet,
		DrawPassParticipation participation,
		in SharedDrawIndirectEncodeResources resources,
		Func<GpuDrawExecutionLaneDefinition, bool> laneAvailable,
		Func<GpuDrawExecutionLaneDefinition, SharedDrawGraphicsBufferBindings?> bindingResolver,
		Func<GpuDrawExecutionLaneDefinition, GraphicsPassBindingSet?> passBindingResolver)
	{
		ArgumentNullException.ThrowIfNull(drawDatabase);
		ArgumentNullException.ThrowIfNull(commandSet);
		ArgumentNullException.ThrowIfNull(laneAvailable);
		ArgumentNullException.ThrowIfNull(bindingResolver);
		ArgumentNullException.ThrowIfNull(passBindingResolver);

		if (_supportsIndirectStructuralUpdates == false)
		{
			return;
		}

		var device = _renderer.GetGfxDevice();
		RegisterIndirectCommandSet(commandSet);
		commandSet.EnsureCreated(device);
		var activeSlot = _gpuDrawResources.ActiveIndirectCommandSlot;
		var frameSlot = _gpuDrawResources.ActiveFrameSlot;
		// Read the binding version here rather than trusting the frame-start backend signal: capacity
		// growth happens while passes are recording, so a pass that encodes after it would otherwise
		// execute records still holding the replaced buffers' addresses.
		var bindingVersion = _gpuDrawResources.IndirectBindingVersion;
		if (commandSet.RequiresFullReencode(activeSlot, frameSlot, _bindlessEpoch, bindingVersion))
		{
			using (FrameProfiler.Instance.Measure("GpuDraw.FullSlotReencode"))
			{
				ReencodeAllIndirectCommands(
					drawDatabase,
					commandSet,
					activeSlot,
					device,
					participation,
					resources,
					laneAvailable,
					bindingResolver,
					passBindingResolver);
			}

			commandSet.MarkSlotEncoded(activeSlot, frameSlot, _bindlessEpoch, bindingVersion, _latestStructuralVersion);
			CompactStructuralReplayRecords();
			return;
		}

		using (FrameProfiler.Instance.Measure("GpuDraw.StructuralReplay"))
		{
			ReplayPendingStructuralRecords(
				drawDatabase,
				commandSet,
				activeSlot,
				device,
				participation,
				resources,
				laneAvailable,
				bindingResolver,
				passBindingResolver);
		}
		CompactStructuralReplayRecords();
	}

	public List<GBufferExecutionBucket> BuildGBufferBuckets()
	{
		var laneDefinitions = GpuDrawExecutionLanes.GetDefinitionsForPass(DrawPassParticipation.GBuffer);
		var buckets = new List<GBufferExecutionBucket>(laneDefinitions.Length);
		var activeIndirectSlot = _gpuDrawResources.ActiveIndirectCommandSlot;
		for (var i = 0; i < laneDefinitions.Length; i++)
		{
			var laneDefinition = laneDefinitions[i];
			var pipeline = GetGBufferPipeline(laneDefinition);
			var bufferBindings = _gbufferBufferBindings[laneDefinition.ExecutionIndex];
			var passBindings = CreateGBufferPassBindingSet(laneDefinition);
			if (pipeline is null || bufferBindings is null || passBindings is null)
			{
				continue;
			}

			buckets.Add(new GBufferExecutionBucket(
				laneDefinition.DrawKind,
				laneDefinition.BucketId,
				laneDefinition.ExecutionIndex,
				laneDefinition.DebugName,
				bufferBindings.Value,
				passBindings,
				pipeline,
				_gbufferIndirectCommandSet.GetAllocatedPages(activeIndirectSlot, laneDefinition.ExecutionIndex)));
		}

		return buckets;
	}

	private void ReplayPendingStructuralRecords(
		GpuDrawDatabase drawDatabase,
		SharedDrawIndirectCommandSet commandSet,
		int slotIndex,
		IGfxDevice device,
		DrawPassParticipation participation,
		in SharedDrawIndirectEncodeResources resources,
		Func<GpuDrawExecutionLaneDefinition, bool> laneAvailable,
		Func<GpuDrawExecutionLaneDefinition, SharedDrawGraphicsBufferBindings?> bindingResolver,
		Func<GpuDrawExecutionLaneDefinition, GraphicsPassBindingSet?> passBindingResolver)
	{
		var appliedVersion = commandSet.GetAppliedStructuralVersion(slotIndex);
		if (_latestStructuralVersion <= appliedVersion)
		{
			return;
		}

		for (var i = 0; i < _structuralReplayRecords.Count; i++)
		{
			var record = _structuralReplayRecords[i];
			if (record.Version <= appliedVersion)
			{
				continue;
			}

			ApplyStructuralRecord(
				drawDatabase,
				record,
				commandSet,
				slotIndex,
				device,
				participation,
				resources,
				laneAvailable,
				bindingResolver,
				passBindingResolver);
		}

		commandSet.SetAppliedStructuralVersion(slotIndex, _latestStructuralVersion);
	}

	private void ApplyStructuralRecord(
		GpuDrawDatabase drawDatabase,
		in StructuralCommandRecord record,
		SharedDrawIndirectCommandSet commandSet,
		int slotIndex,
		IGfxDevice device,
		DrawPassParticipation participation,
		in SharedDrawIndirectEncodeResources resources,
		Func<GpuDrawExecutionLaneDefinition, bool> laneAvailable,
		Func<GpuDrawExecutionLaneDefinition, SharedDrawGraphicsBufferBindings?> bindingResolver,
		Func<GpuDrawExecutionLaneDefinition, GraphicsPassBindingSet?> passBindingResolver)
	{
		if (drawDatabase.IsCurrentDrawHandle(record.DrawHandle) == false)
		{
			return;
		}

		if (record.DrawHandle.Index == 0 || record.DrawHandle.Index >= GpuDrawResources.MaxDrawCount)
		{
			return;
		}

		var commandIndex = (uint)record.DrawHandle.Index;
		if (record.Type == GpuDrawUpdateType.Remove ||
		    GpuDrawClassification.SupportsMeshBackedGeometry(record.DrawKind) == false ||
		    record.Mesh is null)
		{
			ResetCommandAcrossBuckets(commandSet, slotIndex, commandIndex);
			return;
		}

		_renderer.EnsureMeshResources(record.Mesh);
		EncodeCommandForPassLane(
			commandIndex,
			record.DrawKind,
			record.BucketId,
			record.Mesh,
			commandSet,
			slotIndex,
			device,
			participation,
			resources,
			laneAvailable,
			bindingResolver,
			passBindingResolver);
	}

	private void AppendStructuralRecord(in GpuDrawUpdate update, Mesh? mesh, GpuDrawKind drawKind,
		GpuDrawBucketId bucketId, int executionLaneIndex)
	{
		var version = _nextStructuralVersion++;
		var type = update.Type == GpuDrawUpdateType.Remove ? GpuDrawUpdateType.Remove : update.Type;
		_structuralReplayRecords.Add(
			new StructuralCommandRecord(version, type, update.DrawHandle, drawKind, bucketId, executionLaneIndex, mesh));
		_latestStructuralVersion = version;
	}

	private void ReencodeAllIndirectCommands(GpuDrawDatabase drawDatabase,
		SharedDrawIndirectCommandSet commandSet,
		int slotIndex,
		IGfxDevice device,
		DrawPassParticipation participation,
		in SharedDrawIndirectEncodeResources resources,
		Func<GpuDrawExecutionLaneDefinition, bool> laneAvailable,
		Func<GpuDrawExecutionLaneDefinition, SharedDrawGraphicsBufferBindings?> bindingResolver,
		Func<GpuDrawExecutionLaneDefinition, GraphicsPassBindingSet?> passBindingResolver)
	{
		for (var executionIndex = 0; executionIndex < GpuDrawExecutionLanes.ExecutionLaneCount; executionIndex++)
		{
			var pages = commandSet.GetAllocatedPages(slotIndex, executionIndex);
			for (var pageIndex = 0; pageIndex < pages.Length; pageIndex++)
			{
				var page = pages[pageIndex];
				for (var commandIndex = 0u; commandIndex < page.PageCommandCapacity; commandIndex++)
				{
					_backendBridge.ResetCommand(page.CommandBuffer, commandIndex);
				}
			}
		}

		drawDatabase.CollectDrawEntries(_drawEntries);
		for (var i = 0; i < _drawEntries.Count; i++)
		{
			var entry = _drawEntries[i];
			if (entry.DrawIndex <= 0 || entry.DrawIndex >= GpuDrawResources.MaxDrawCount)
			{
				continue;
			}

			if (GpuDrawClassification.SupportsMeshBackedGeometry(entry.DrawKind) == false)
			{
				LogUnsupportedDrawKindOnce(entry.DrawKind);
				continue;
			}

			_renderer.EnsureMeshResources(entry.Mesh);
			var bucketId = GpuDrawClassification.ResolveBucketId(entry.DrawKind, entry.Material);
			EncodeCommandForPassLane(
				(uint)entry.DrawIndex,
				entry.DrawKind,
				bucketId,
				entry.Mesh,
				commandSet,
				slotIndex,
				device,
				participation,
				resources,
				laneAvailable,
				bindingResolver,
				passBindingResolver);
		}
	}

	private void RegisterIndirectCommandSet(SharedDrawIndirectCommandSet commandSet)
	{
		for (var i = 0; i < _knownIndirectCommandSets.Count; i++)
		{
			if (ReferenceEquals(_knownIndirectCommandSets[i], commandSet))
			{
				return;
			}
		}

		_knownIndirectCommandSets.Add(commandSet);
	}

	private void CompactStructuralReplayRecords()
	{
		if (_structuralReplayRecords.Count == 0 || _knownIndirectCommandSets.Count == 0)
		{
			return;
		}

		var minAppliedVersion = ulong.MaxValue;
		for (var commandSetIndex = 0; commandSetIndex < _knownIndirectCommandSets.Count; commandSetIndex++)
		{
			var commandSet = _knownIndirectCommandSets[commandSetIndex];
			for (var slotIndex = 0; slotIndex < GpuDrawResources.IndirectCommandBufferSlotCount; slotIndex++)
			{
				var appliedVersion = commandSet.GetAppliedStructuralVersion(slotIndex);
				if (appliedVersion < minAppliedVersion)
				{
					minAppliedVersion = appliedVersion;
				}
			}
		}

		if (minAppliedVersion == 0)
		{
			return;
		}

		var removeCount = 0;
		for (; removeCount < _structuralReplayRecords.Count; removeCount++)
		{
			if (_structuralReplayRecords[removeCount].Version > minAppliedVersion)
			{
				break;
			}
		}

		if (removeCount > 0)
		{
			_structuralReplayRecords.RemoveRange(0, removeCount);
		}
	}

	private void ResetCommandAcrossBuckets(
		SharedDrawIndirectCommandSet commandSet,
		int slotIndex,
		uint commandIndex)
	{
		for (var executionIndex = 0; executionIndex < GpuDrawExecutionLanes.ExecutionLaneCount; executionIndex++)
		{
			if (commandSet.TryGetPageForCommand(
				    slotIndex,
				    executionIndex,
				    commandIndex,
				    out var commandBuffer,
				    out var pageCommandIndex))
			{
				_backendBridge.ResetCommand(commandBuffer, pageCommandIndex);
			}
		}
	}

	private void EncodeCommandForPassLane(
		uint commandIndex,
		GpuDrawKind drawKind,
		GpuDrawBucketId bucketId,
		Mesh mesh,
		SharedDrawIndirectCommandSet commandSet,
		int slotIndex,
		IGfxDevice device,
		DrawPassParticipation participation,
		in SharedDrawIndirectEncodeResources resources,
		Func<GpuDrawExecutionLaneDefinition, bool> laneAvailable,
		Func<GpuDrawExecutionLaneDefinition, SharedDrawGraphicsBufferBindings?> bindingResolver,
		Func<GpuDrawExecutionLaneDefinition, GraphicsPassBindingSet?> passBindingResolver)
	{
		if (GpuDrawExecutionLanes.TryGetDefinition(drawKind, bucketId, out var targetLane) == false ||
		    targetLane.SupportsPass(participation) == false ||
		    laneAvailable(targetLane) == false)
		{
			ResetCommandAcrossBuckets(commandSet, slotIndex, commandIndex);
			return;
		}

		for (var executionIndex = 0; executionIndex < GpuDrawExecutionLanes.ExecutionLaneCount; executionIndex++)
		{
			if (executionIndex == targetLane.ExecutionIndex)
			{
				var bindings = bindingResolver(targetLane)
				               ?? throw new InvalidOperationException(
					               $"Missing reflected shared-draw buffer bindings for execution lane '{targetLane.DebugName}'.");
				var passBindings = passBindingResolver(targetLane)
					?? throw new InvalidOperationException($"Missing reflected graphics pass bindings for execution lane '{targetLane.DebugName}'.");
				var commandBuffer = commandSet.EnsurePageForCommand(
					device,
					slotIndex,
					executionIndex,
					commandIndex,
					out var pageCommandIndex);
				if (_backendBridge.TryEncodeIndexedDrawCommand(commandBuffer, pageCommandIndex, commandIndex, mesh, resources, passBindings, bindings.ToPerDrawBindings()) == false)
				{
					_backendBridge.ResetCommand(commandBuffer, pageCommandIndex);
				}
				continue;
			}

			if (commandSet.TryGetPageForCommand(
				    slotIndex,
				    executionIndex,
				    commandIndex,
				    out var staleCommandBuffer,
				    out var stalePageCommandIndex))
			{
				_backendBridge.ResetCommand(staleCommandBuffer, stalePageCommandIndex);
			}
		}
	}

	private static void ExtractFrustumPlanes(Matrix4x4 viewProjection, Span<Vector4> planes)
	{
		var col1 = new Vector4(viewProjection.M11, viewProjection.M21, viewProjection.M31, viewProjection.M41);
		var col2 = new Vector4(viewProjection.M12, viewProjection.M22, viewProjection.M32, viewProjection.M42);
		var col3 = new Vector4(viewProjection.M13, viewProjection.M23, viewProjection.M33, viewProjection.M43);
		var col4 = new Vector4(viewProjection.M14, viewProjection.M24, viewProjection.M34, viewProjection.M44);

		planes[0] = NormalizePlane(col4 + col1);
		planes[1] = NormalizePlane(col4 - col1);
		planes[2] = NormalizePlane(col4 + col2);
		planes[3] = NormalizePlane(col4 - col2);
		planes[4] = NormalizePlane(col3);
		planes[5] = NormalizePlane(col4 - col3);
	}

	private static Vector4 NormalizePlane(Vector4 plane)
	{
		var normal = new Vector3(plane.X, plane.Y, plane.Z);
		var length = normal.Length();
		if (length <= 0.0f)
		{
			return plane;
		}

		var invLength = 1.0f / length;
		return plane * invLength;
	}

	private static uint CreateDrawFlags(int bucketIndex)
	{
		return DrawFlagActive | ((uint)bucketIndex & DrawFlagBucketMask) << DrawFlagBucketShift;
	}

	private int AppendFullGpuStateRefreshUpdates(GpuDrawDatabase drawDatabase, List<GpuDrawUpdate> destination)
	{
		var initialCount = destination.Count;
		drawDatabase.CollectDrawEntries(_drawEntries);
		for (var i = 0; i < _drawEntries.Count; i++)
		{
			var entry = _drawEntries[i];
			destination.Add(GpuDrawUpdate.CreateAdd(
				entry.DrawKind,
				entry.DrawHandle,
				entry.InstanceHandle,
				entry.MeshHandle,
				entry.MaterialHandle,
				entry.PreviousWorld,
				entry.World,
				entry.BoundsCenterRadius,
				entry.Mesh,
				entry.Material,
				entry.TerrainInstanceData,
				entry.TerrainSurface));
		}

		return destination.Count - initialCount;
	}

	private void AppendClearAllDraws(List<GpuDrawUpdate> destination)
	{
		for (var drawIndex = 1; drawIndex < GpuDrawResources.MaxDrawCount; drawIndex++)
		{
			var drawHandle = GpuDrawHandle.Create(drawIndex, 1);
			destination.Add(GpuDrawUpdate.CreateRemove(GpuDrawKind.Mesh, drawHandle, GpuDrawHandle.Invalid));
		}
	}

	private void UploadGenerationTables(GpuDrawDatabase drawDatabase)
	{
		drawDatabase.CopyGenerationTables(
			_drawGenerations,
			_instanceGenerations,
			_meshGenerations,
			_materialGenerations);

		WriteBuffer<uint>(_gpuDrawResources.DrawGenerationBuffer!, CollectionsMarshal.AsSpan(_drawGenerations),
			"DrawGenerationBuffer");
		WriteBuffer<uint>(_gpuDrawResources.InstanceGenerationBuffer!, CollectionsMarshal.AsSpan(_instanceGenerations),
			"InstanceGenerationBuffer");
		WriteBuffer<uint>(_gpuDrawResources.MeshGenerationBuffer!, CollectionsMarshal.AsSpan(_meshGenerations),
			"MeshGenerationBuffer");
		WriteBuffer<uint>(_gpuDrawResources.MaterialGenerationBuffer!, CollectionsMarshal.AsSpan(_materialGenerations),
			"MaterialGenerationBuffer");

		UploadFallbackTableEntries();
	}

	private void SampleGpuDiagnosticCounters()
	{
		_backendBridge.SampleGpuDiagnosticCounters(
			_gpuDrawResources.DiagnosticsCounterBuffer,
			_lastGpuDiagnosticCounters,
			_hardeningStats);
	}

	private void SampleVisibilityDiagnostics()
	{
		_backendBridge.SampleVisibilityDiagnostics(
			_gpuDrawResources.DrawCountPerBucketBuffer,
			_gpuDrawResources.DrawExecutionRangePerBucketBuffer,
			_hardeningStats);
		SampleGpuDiagnosticCounters();
	}

	private void PublishSubmittedBucketDiagnostics(GpuDrawDatabase drawDatabase)
	{
		drawDatabase.CollectDrawEntries(_drawEntries);
		var definitions = GBufferDrawBuckets.StableOrderDefinitions;
		Span<int> submittedCounts = stackalloc int[definitions.Length];
		submittedCounts.Clear();
		for (var i = 0; i < _drawEntries.Count; i++)
		{
			var entry = _drawEntries[i];
			var bucketId = GpuDrawClassification.ResolveBucketId(entry.DrawKind, entry.Material);
			submittedCounts[GetStableBucketIndex(bucketId)]++;
		}

		for (var i = 0; i < definitions.Length; i++)
		{
			_hardeningStats.SetSubmittedDrawCount(definitions[i].BucketId, submittedCounts[i]);
		}
	}

	private static int GetStableBucketIndex(GpuDrawBucketId bucketId)
	{
		var definitions = GBufferDrawBuckets.StableOrderDefinitions;
		for (var i = 0; i < definitions.Length; i++)
		{
			if (definitions[i].BucketId == bucketId)
			{
				return i;
			}
		}

		throw new InvalidOperationException($"Unknown draw bucket id '{bucketId}'.");
	}

	private void EnsureTerrainSamplers()
	{
		if (_terrainLayerSampler.IsValid == false)
		{
			_terrainLayerSampler = _bindlessRegistry.GetSamplerHandle(new SamplerDescriptor(
				FilterMode.Anisotropic,
				AddressMode.Wrap,
				AddressMode.Wrap,
				AddressMode.Wrap,
				maxAnisotropy: 8.0f));
		}

		if (_terrainControlSampler.IsValid == false)
		{
			_terrainControlSampler = _bindlessRegistry.GetSamplerHandle(new SamplerDescriptor(
				FilterMode.Bilinear,
				AddressMode.Clamp,
				AddressMode.Clamp,
				AddressMode.Clamp));
		}

		if (_terrainHeightSampler.IsValid == false)
		{
			_terrainHeightSampler = _bindlessRegistry.GetSamplerHandle(new SamplerDescriptor(
				FilterMode.Bilinear,
				AddressMode.Clamp,
				AddressMode.Clamp,
				AddressMode.Clamp));
		}
	}

	private void PopulateTerrainLayerHandles(
		in TerrainResolvedLayer layer,
		ref uint albedoHandle,
		ref uint normalHandle,
		ref uint ormHandle,
		ref uint heightHandle,
		ref uint hasHeight,
		ref uint hasNormal,
		ref uint hasOrm,
		ref float scale,
		ref uint autoMaterial,
		ref uint useMinimumSlope,
		ref float minimumSlopeDegrees)
	{
		albedoHandle = RegisterTerrainTexture(layer.Albedo);
		normalHandle = RegisterTerrainTexture(layer.Normal);
		ormHandle = RegisterTerrainTexture(layer.Orm);
		heightHandle = RegisterTerrainTexture(layer.Height);
		hasHeight = layer.Height is null ? 0u : 1u;
		hasNormal = layer.Normal is null ? 0u : 1u;
		hasOrm = layer.Orm is null ? 0u : 1u;
		scale = Math.Max(layer.Scale, 0.001f);
		autoMaterial = layer.AutoMaterial ? 1u : 0u;
		useMinimumSlope = layer.UseMinimumSlope ? 1u : 0u;
		minimumSlopeDegrees = Math.Clamp(layer.MinimumSlopeDegrees, 0.0f, 90.0f);
	}

	private uint RegisterTerrainTexture(Texture? texture)
	{
		if (texture is null)
		{
			return _bindlessRegistry.ErrorTextureHandle.Value;
		}

		return _bindlessRegistry.GetTextureHandle(texture.Resources).Value;
	}

	private static uint GetPackedHandleIndex(uint handle) => handle & 0xFFFFu;

	private static ShaderProgramId GetGBufferShaderPath(GpuDrawKind drawKind) => drawKind switch
	{
		GpuDrawKind.Mesh => EngineShaderPrograms.GBuffer,
		GpuDrawKind.DebugPrimitive => EngineShaderPrograms.DebugPrimitiveGBuffer,
		GpuDrawKind.Terrain => EngineShaderPrograms.TerrainSharedGBuffer,
		_ => throw new NotSupportedException($"G-buffer shared draw kind '{drawKind}' does not define a shader.")
	};

	private void UploadFallbackTableEntries()
	{
		var fallbackMeshData = new GpuMeshData(
			_bindlessRegistry.ErrorBufferHandle.Value,
			_bindlessRegistry.ErrorBufferHandle.Value,
			0,
			0,
			0,
			0);
		var fallbackMaterialData = new GpuMaterialData(
			new ColorRGBA(1.0f, 0.0f, 1.0f, 1.0f),
			Vector4.One,
			Vector4.Zero,
			_bindlessRegistry.ErrorTextureHandle.Value,
			_bindlessRegistry.ErrorTextureHandle.Value,
			_bindlessRegistry.ErrorTextureHandle.Value,
			_bindlessRegistry.ErrorTextureHandle.Value,
			_bindlessRegistry.ErrorSamplerHandle.Value);
			var fallbackTerrainMaterialData = new GpuTerrainMaterialData(
				_bindlessRegistry.ErrorTextureHandle.Value,
				_bindlessRegistry.ErrorTextureHandle.Value,
				_bindlessRegistry.ErrorTextureHandle.Value,
				0,
				_terrainHeightSampler.Value,
				_terrainLayerSampler.Value,
			_terrainControlSampler.Value,
			0,
			1,
			4.0f,
			64.0f,
			12.0f);
		var fallbackTerrainLayerData = new GpuTerrainLayerData(
			_bindlessRegistry.ErrorTextureHandle.Value,
			_bindlessRegistry.ErrorTextureHandle.Value,
			_bindlessRegistry.ErrorTextureHandle.Value,
			_bindlessRegistry.ErrorTextureHandle.Value,
			0,
			1.0f,
			0,
			0,
			0.0f,
			0,
			0);
		WriteBufferElement(_gpuDrawResources.MeshBuffer!, fallbackMeshData, 0, "MeshBuffer");
		WriteBufferElement(_gpuDrawResources.MaterialBuffer!, fallbackMaterialData, 0, "MaterialBuffer");
		WriteBufferElement(_gpuDrawResources.TerrainMaterialBuffer!, fallbackTerrainMaterialData, 0, "TerrainMaterialBuffer");
		WriteBufferElement(_gpuDrawResources.TerrainLayerBuffer!, fallbackTerrainLayerData, 0, "TerrainLayerBuffer");
	}

	/// <summary>
	/// Falling back is correct but costs the command processor a walk over every draw slot in the scene
	/// per lane, so it is worth saying out loud rather than leaving as a silent performance cliff.
	/// </summary>
	private void LogCompactionUnavailableOnce(DrawPassParticipation participation)
	{
		if (_loggedCompactionUnavailable)
		{
			return;
		}

		_loggedCompactionUnavailable = true;
		Console.WriteLine(
			$"GpuDraw: indirect command compaction is unavailable for '{participation}'; falling back to " +
			"executing the full command range, which does not scale with scene size.");
	}

	private void LogUnsupportedDrawKindOnce(GpuDrawKind drawKind)
	{
		if (_loggedUnsupportedDrawKind)
		{
			return;
		}

		_loggedUnsupportedDrawKind = true;
		Console.WriteLine(
			$"GpuDraw: shared draw kind '{drawKind}' is not supported by the current shared draw path; draw will be skipped.");
	}

	private void LogCapacityExceededOnce(in GpuDrawUpdate update)
	{
		if (_loggedCapacityExceeded)
		{
			return;
		}

		_loggedCapacityExceeded = true;
		_hardeningStats.IncrementPackedCapacityFailures();
		Console.WriteLine(
			$"GpuDraw capacity exceeded; some renderables are skipped. drawHandle={update.DrawHandle.Value}, instanceHandle={update.InstanceHandle.Value}, meshHandle={update.MeshHandle.Value}, materialHandle={update.MaterialHandle.Value}. Limits: draw<{GpuDrawResources.MaxDrawCount}, instance<{GpuDrawResources.MaxInstanceCount}, mesh<{GpuDrawResources.MaxMeshCount}, material<{GpuDrawResources.MaxMaterialCount}.");
	}

	private static void WriteBufferElement<T>(IGfxBuffer buffer, in T data, ulong elementOffset, string bufferName)
		where T : unmanaged
	{
		if (buffer is not IWritableGpuBuffer writableBuffer)
		{
			throw new NotImplementedException(
				$"Buffer '{bufferName}' does not support CPU writes on this backend.");
		}

		Span<T> tmp = stackalloc T[1];
		tmp[0] = data;
		writableBuffer.Write<T>(tmp, elementOffset);
	}
}

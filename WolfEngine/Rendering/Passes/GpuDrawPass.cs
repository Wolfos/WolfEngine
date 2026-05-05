#nullable enable

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using WolfEngine.Profiling;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

public sealed class GpuDrawPass
{
	private readonly IShaderCompiler _shaderCompiler;
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
	private GraphicsBackendKind? _computeReflectionBackendKind;
	private ReadOnlyMemory<byte> _instanceUpdateShaderBytecode;
	private ReadOnlyMemory<byte> _meshUpdateShaderBytecode;
	private ReadOnlyMemory<byte> _materialUpdateShaderBytecode;
	private ReadOnlyMemory<byte> _terrainMaterialUpdateShaderBytecode;
	private ReadOnlyMemory<byte> _terrainLayerUpdateShaderBytecode;
	private ReadOnlyMemory<byte> _cullShaderBytecode;
	private ComputeThreadGroupSize? _instanceUpdateThreadGroupSize;
	private ComputeThreadGroupSize? _meshUpdateThreadGroupSize;
	private ComputeThreadGroupSize? _materialUpdateThreadGroupSize;
	private ComputeThreadGroupSize? _terrainMaterialUpdateThreadGroupSize;
	private ComputeThreadGroupSize? _terrainLayerUpdateThreadGroupSize;
	private ComputeThreadGroupSize? _cullThreadGroupSize;
	private ComputeResourceBindings? _terrainMaterialUpdateBindings;
	private ComputeResourceBindings? _terrainLayerUpdateBindings;
	private ShaderPropertyWriter? _instanceUpdateParamsWriter;
	private ShaderPropertyWriter? _meshUpdateParamsWriter;
	private ShaderPropertyWriter? _materialUpdateParamsWriter;
	private ShaderPropertyWriter? _terrainMaterialUpdateParamsWriter;
	private ShaderPropertyWriter? _terrainLayerUpdateParamsWriter;
	private ShaderPropertyWriter? _cullParamsWriter;
	private readonly List<GpuDrawUpdate> _updates = new();
	private readonly List<GpuDrawInstanceUpdateData> _instanceUpdateData = new();
	private readonly List<GpuDrawMeshUpdateData> _meshUpdateData = new();
	private readonly List<GpuDrawMaterialUpdateData> _materialUpdateData = new();
	private readonly List<GpuTerrainMaterialUpdateData> _terrainMaterialUpdateData = new();
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
	private readonly SharedDrawIndirectCommandSet _gbufferIndirectCommandSet = new();
	private readonly Dictionary<uint, MaterialDrawState> _materialDrawStates = new();
	private readonly Dictionary<uint, TerrainDrawSurface> _terrainMaterialStates = new();
	private readonly Dictionary<uint, TerrainMaterialAllocation> _terrainMaterialAllocations = new();
	private int _nextTerrainLayerSlot = 1;
	private int _activeIndirectSlot = -1;
	private ulong _latestStructuralVersion;
	private ulong _nextStructuralVersion = 1;
	private uint _bindlessEpoch = 1;
	private bool _supportsIndirectStructuralUpdates;
	private bool _loggedCapacityExceeded;
	private bool _loggedUpdateOverflowRecovery;
	private bool _loggedUnsupportedDrawKind;
	private bool _gpuStateBootstrapPending = true;
	private bool _loggedMetalTerrainReflectionDiagnostics;
	private bool _loggedMetalTerrainGraphicsBindingDiagnostics;
	private bool _loggedMetalTerrainPayloadDiagnostics;
	private bool _loggedMetalTerrainLayerPayloadDiagnostics;
	private bool _loggedMetalTerrainReadbackDiagnostics;
	private uint? _pendingMetalTerrainReadbackIndex;
	private uint? _pendingMetalTerrainLayerReadbackStart;
	private uint _pendingMetalTerrainLayerReadbackCount;

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

	public GpuDrawPass(IShaderCompiler shaderCompiler,
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
			AppendFullGpuStateRefreshUpdates(drawDatabase, _updates);
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
		_instanceUpdateData.Clear();
		_meshUpdateData.Clear();
		_materialUpdateData.Clear();
		_terrainMaterialUpdateData.Clear();
		_terrainLayerUpdateData.Clear();

		var forceGpuRefresh = _gpuStateBootstrapPending || requireFullGpuStateRefresh;
		if (forceGpuRefresh)
		{
			_terrainMaterialStates.Clear();
			_terrainMaterialAllocations.Clear();
			_nextTerrainLayerSlot = 1;
		}

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
			var terrainHeightScale = 0.0f;
			var baseColor = ColorRGBA.White;
			var metallicRoughness = Vector4.One;
			var emissiveFactorIntensity = Vector4.Zero;
			var bucketId = GpuDrawBucketId.Opaque;
			var executionLaneIndex = 0;
			uint drawFlags = update.Type == GpuDrawUpdateType.Remove ? 0u : CreateDrawFlags(executionLaneIndex);
			var alphaCutoff = 0.0f;
			var materialReady = drawKind switch
			{
				GpuDrawKind.Mesh => material?.HasGpuResources ?? false,
				GpuDrawKind.DebugPrimitive => material is not null,
				GpuDrawKind.Terrain => material is not null,
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
				metallicRoughness = new Vector4(material.MetallicFactor, material.RoughnessFactor, alphaCutoff, 0.0f);
				emissiveFactorIntensity = new Vector4(material.EmissiveFactor, material.EmissiveIntensity);
			}
			else if (material is not null && GpuDrawClassification.SupportsUnlitTintMaterialInterpretation(drawKind))
			{
				baseColor = material.Color;
				metallicRoughness = Vector4.Zero;
				emissiveFactorIntensity = Vector4.Zero;
			}
			else if (GpuDrawClassification.SupportsTerrainMaterialInterpretation(drawKind) &&
			         update.TerrainSurface.HasValue)
			{
				var terrainSurface = update.TerrainSurface.Value;
				baseColor = ColorRGBA.White;
				metallicRoughness = Vector4.Zero;
				emissiveFactorIntensity = Vector4.Zero;
				layerCount = (uint)Math.Max(terrainSurface.LayerCount, 1);
				heightBlendSharpness = terrainSurface.HeightBlendSharpness;
				terrainHeightScale = terrainSurface.HeightScale;
				if (terrainSurface.Heightmap is { } heightmap)
				{
					heightmapHandle = RegisterTerrainTexture(heightmap);
				}

				if (terrainSurface.LayerIndexMap is { } layerIndexMap)
				{
					layerIndexMapHandle = RegisterTerrainTexture(layerIndexMap);
				}

				if (terrainSurface.LayerWeightMap is { } layerWeightMap)
				{
					layerWeightMapHandle = RegisterTerrainTexture(layerWeightMap);
				}

				if (terrainSurface.LayerIndexMap is not null && terrainSurface.LayerWeightMap is not null)
				{
					hasLayerMaps = 1;
				}
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
					baseVertex));
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
					var allocation = EnsureTerrainMaterialAllocation(update.MaterialHandle.Value, layerCount, forceGpuRefresh);
					layerStart = allocation.LayerStart;
					if (layerStart == 0)
					{
						layerCount = 1;
					}
					_terrainMaterialUpdateData.Add(new GpuTerrainMaterialUpdateData(
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
						terrainHeightScale));

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
		RecordCullForView(context, sceneData.ViewProjection, sceneData.CameraOrigin, useShadowBuffers: false);
	}

	public void RecordCullForView(RenderGraphContext context, Matrix4x4 viewProjection, Vector3 cameraOrigin,
		bool useShadowBuffers = false)
	{
		var device = _renderer.GetGfxDevice();
		_bindlessRegistry.EnsureInitialized(device);
		_gpuDrawResources.EnsureCreated(device);
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

		Span<uint> resetCounts = stackalloc uint[executionLaneCount];
		resetCounts.Clear();
		WriteBuffer<uint>(drawCountPerBucketBuffer!, resetCounts, "DrawCountPerBucketBuffer");

		Span<uint> resetRanges = stackalloc uint[executionLaneCount * 2];
		for (var i = 0; i < executionLaneCount; i++)
		{
			resetRanges[(i * 2) + 0] = 0;
			resetRanges[(i * 2) + 1] = 0;
		}

		WriteBuffer<uint>(drawExecutionRangePerBucketBuffer!, resetRanges, "DrawExecutionRangePerBucketBuffer");

		var pipeline = EnsureCullPipeline(device);
		var commandList = context.CommandList;
		using (FrameProfiler.Instance.Measure("GpuDraw.Cull"))
		{
			commandList.BindPipeline(pipeline);

			Span<Vector4> planes = stackalloc Vector4[6];
			ExtractFrustumPlanes(viewProjection, planes);
			var cullParamsWriter = _cullParamsWriter
			                       ?? throw new InvalidOperationException(
				                       "GpuDraw cull reflection writer was not initialized.");
			cullParamsWriter.Clear();
			for (var i = 0; i < planes.Length; i++)
			{
				cullParamsWriter.SetVector4($"planes[{i}]", planes[i]);
			}

			cullParamsWriter.SetVector4(
				"cameraPositionAndMaxDrawCount",
				new Vector4(
					cameraOrigin,
					_gpuDrawResources.ActiveDrawCommandUpperBound));
			cullParamsWriter.SetUInt("bucketCount", (uint)executionLaneCount);
			cullParamsWriter.SetUInt("maxVisiblePerBucket", GpuDrawResources.MaxDrawCount);
			cullParamsWriter.SetUInt("fallbackMeshHandle", context.GpuDrawDatabase.FallbackMeshHandle.Value);
			commandList.SetComputeConstants(cullParamsWriter.RegisterIndex, cullParamsWriter.AsBytes());

			commandList.SetComputeBuffer(0, _gpuDrawResources.DrawCommandBuffer!);
			commandList.SetComputeBuffer(1, _gpuDrawResources.InstanceBuffer!);
			commandList.SetComputeBuffer(2, _gpuDrawResources.MeshBuffer!);
			commandList.SetComputeBuffer(3, _gpuDrawResources.DrawArgsBuffer!);
			commandList.SetComputeBuffer(4, drawCountPerBucketBuffer!);
			commandList.SetComputeBuffer(5, _gpuDrawResources.VisibleDrawIdsPerExecutionLaneBuffer!);
			commandList.SetComputeBuffer(6, drawExecutionRangePerBucketBuffer!);
			commandList.SetComputeBuffer(7, _gpuDrawResources.DrawGenerationBuffer!);
			commandList.SetComputeBuffer(8, _gpuDrawResources.InstanceGenerationBuffer!);
			commandList.SetComputeBuffer(9, _gpuDrawResources.MeshGenerationBuffer!);
			commandList.SetComputeBuffer(10, _gpuDrawResources.MaterialGenerationBuffer!);
			commandList.SetComputeBuffer(11, _gpuDrawResources.DiagnosticsCounterBuffer!);

			var threadGroupSize = _cullThreadGroupSize
			                      ?? throw new InvalidOperationException(
				                      "GpuDraw cull threadgroup size was not initialized.");
			var (groupCountX, groupCountY, groupCountZ) = threadGroupSize.GetDispatchGroupCount(
				_gpuDrawResources.ActiveDrawCommandUpperBound);
			commandList.Dispatch(groupCountX, groupCountY, groupCountZ);
		}
	}

	private TerrainMaterialAllocation EnsureTerrainMaterialAllocation(uint materialHandle, uint requiredLayerCount, bool forceReallocate)
	{
		requiredLayerCount = Math.Max(requiredLayerCount, 1u);
		if (!forceReallocate &&
		    _terrainMaterialAllocations.TryGetValue(materialHandle, out var existingAllocation) &&
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
			float scale = 1.0f;
			PopulateTerrainLayerHandles(
				currentLayer,
				ref albedoHandle,
				ref normalHandle,
				ref ormHandle,
				ref heightHandle,
				ref hasHeight,
				ref scale);
			_terrainLayerUpdateData.Add(new GpuTerrainLayerUpdateData(
				materialHandle,
				layerStart,
				(uint)layerIndex,
				albedoHandle,
				normalHandle,
				ormHandle,
				heightHandle,
				hasHeight,
				scale));
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
		       Math.Abs(left.Scale - right.Scale) <= 0.0001f;
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
		_pendingMetalTerrainReadbackIndex = GetPackedHandleIndex(_terrainMaterialUpdateData[0].MaterialHandle);
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
		if (_terrainMaterialUpdateData.Count > 0)
		{
			_pendingMetalTerrainLayerReadbackStart = _terrainMaterialUpdateData[0].LayerStart;
			_pendingMetalTerrainLayerReadbackCount = _terrainMaterialUpdateData[0].LayerCount;
		}
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
			"gpu_draw_instance_update.compute.slang",
			"CSUpdateInstance",
			backendKind);
		_instanceUpdateShaderBytecode = instanceUpdateCompiled.Bytecode;
		_instanceUpdateThreadGroupSize = instanceUpdateCompiled.ThreadGroupSize;
		_instanceUpdateParamsWriter =
			new ShaderPropertyWriter(instanceUpdateCompiled.ReflectionLayout.GetConstantBuffer("UpdateParams"));

		var meshUpdateCompiled = _shaderCompiler.GetComputeShaderWithReflection(
			"gpu_draw_mesh_update.compute.slang",
			"CSUpdateMesh",
			backendKind);
		_meshUpdateShaderBytecode = meshUpdateCompiled.Bytecode;
		_meshUpdateThreadGroupSize = meshUpdateCompiled.ThreadGroupSize;
		_meshUpdateParamsWriter =
			new ShaderPropertyWriter(meshUpdateCompiled.ReflectionLayout.GetConstantBuffer("UpdateParams"));

		var materialUpdateCompiled = _shaderCompiler.GetComputeShaderWithReflection(
			"gpu_draw_material_update.compute.slang",
			"CSUpdateMaterial",
			backendKind);
		_materialUpdateShaderBytecode = materialUpdateCompiled.Bytecode;
		_materialUpdateThreadGroupSize = materialUpdateCompiled.ThreadGroupSize;
		_materialUpdateParamsWriter =
			new ShaderPropertyWriter(materialUpdateCompiled.ReflectionLayout.GetConstantBuffer("UpdateParams"));

		var terrainMaterialUpdateCompiled = _shaderCompiler.GetComputeShaderWithReflection(
			"gpu_draw_terrain_material_update.compute.slang",
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
			"gpu_draw_terrain_layer_update.compute.slang",
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
			"gpu_draw_cull.compute.slang",
			"CSCull",
			backendKind);
		_cullShaderBytecode = cullCompiled.Bytecode;
		_cullThreadGroupSize = cullCompiled.ThreadGroupSize;
		_cullParamsWriter = new ShaderPropertyWriter(cullCompiled.ReflectionLayout.GetConstantBuffer("CullParams"));

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
					TextureFormat.Rgba8Unorm,
					TextureFormat.Rgba16Float
				}),
				depthStencil: new DepthStencilFormat(TextureFormat.D32Float),
				renderState: renderState,
				layout: GraphicsLayoutKind.Material,
				shaderVariant: $"GBuffer:{lane.ShaderVariant}");
			_gbufferPipelines[lane.ExecutionIndex] = device.GetOrCreatePipeline(pipelineKey, shaderSet);
			var bindings = SharedDrawGraphicsBufferBindings.FromGBufferReflection(compiled.ReflectionLayout);
			_gbufferBufferBindings[lane.ExecutionIndex] = bindings;
		}
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

	public void EnsureGBufferIndirectCommands(RenderGraphContext context)
	{
		ArgumentNullException.ThrowIfNull(context);
		var device = _renderer.GetGfxDevice();
		EnsureGBufferPipelines(device);
		EnsureIndirectCommandsForPass(
			context.GpuDrawDatabase,
			_gbufferIndirectCommandSet,
			DrawPassParticipation.GBuffer,
			SharedDrawIndirectEncodeResources.FromGpuDrawResources(_gpuDrawResources, _gpuDrawResources.CameraBuffer),
			static _ => true,
			lane => _gbufferBufferBindings[lane.ExecutionIndex]);
	}

	public void EnsureIndirectCommandsForPass(
		GpuDrawDatabase drawDatabase,
		SharedDrawIndirectCommandSet commandSet,
		DrawPassParticipation participation,
		in SharedDrawIndirectEncodeResources resources,
		Func<GpuDrawExecutionLaneDefinition, bool> laneAvailable,
		Func<GpuDrawExecutionLaneDefinition, SharedDrawGraphicsBufferBindings?> bindingResolver)
	{
		ArgumentNullException.ThrowIfNull(drawDatabase);
		ArgumentNullException.ThrowIfNull(commandSet);
		ArgumentNullException.ThrowIfNull(laneAvailable);
		ArgumentNullException.ThrowIfNull(bindingResolver);

		if (_supportsIndirectStructuralUpdates == false)
		{
			return;
		}

		var device = _renderer.GetGfxDevice();
		RegisterIndirectCommandSet(commandSet);
		commandSet.EnsureCreated(device);
		var activeSlot = _gpuDrawResources.ActiveIndirectCommandSlot;
		var frameSlot = _gpuDrawResources.ActiveFrameSlot;
		var commands = commandSet.GetSlotCommands(activeSlot);
		if (commandSet.RequiresFullReencode(activeSlot, frameSlot, _bindlessEpoch))
		{
			using (FrameProfiler.Instance.Measure("GpuDraw.FullSlotReencode"))
			{
				ReencodeAllIndirectCommands(
					drawDatabase,
					commands,
					participation,
					resources,
					laneAvailable,
					bindingResolver);
			}

			commandSet.MarkSlotEncoded(activeSlot, frameSlot, _bindlessEpoch, _latestStructuralVersion);
			CompactStructuralReplayRecords();
			return;
		}

		using (FrameProfiler.Instance.Measure("GpuDraw.StructuralReplay"))
		{
			ReplayPendingStructuralRecords(
				drawDatabase,
				commandSet,
				activeSlot,
				commands,
				participation,
				resources,
				laneAvailable,
				bindingResolver);
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
			if (pipeline is null || bufferBindings is null)
			{
				continue;
			}

			buckets.Add(new GBufferExecutionBucket(
				laneDefinition.DrawKind,
				laneDefinition.BucketId,
				laneDefinition.ExecutionIndex,
				laneDefinition.DebugName,
				bufferBindings.Value,
				pipeline,
				_gbufferIndirectCommandSet.GetCommandBuffer(activeIndirectSlot, laneDefinition)));
		}

		return buckets;
	}

	private void ReplayPendingStructuralRecords(
		GpuDrawDatabase drawDatabase,
		SharedDrawIndirectCommandSet commandSet,
		int slotIndex,
		ReadOnlySpan<IGfxIndirectCommandBuffer> indirectCommands,
		DrawPassParticipation participation,
		in SharedDrawIndirectEncodeResources resources,
		Func<GpuDrawExecutionLaneDefinition, bool> laneAvailable,
		Func<GpuDrawExecutionLaneDefinition, SharedDrawGraphicsBufferBindings?> bindingResolver)
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
				indirectCommands,
				participation,
				resources,
				laneAvailable,
				bindingResolver);
		}

		commandSet.SetAppliedStructuralVersion(slotIndex, _latestStructuralVersion);
	}

	private void ApplyStructuralRecord(
		GpuDrawDatabase drawDatabase,
		in StructuralCommandRecord record,
		ReadOnlySpan<IGfxIndirectCommandBuffer> indirectCommands,
		DrawPassParticipation participation,
		in SharedDrawIndirectEncodeResources resources,
		Func<GpuDrawExecutionLaneDefinition, bool> laneAvailable,
		Func<GpuDrawExecutionLaneDefinition, SharedDrawGraphicsBufferBindings?> bindingResolver)
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
			ResetCommandAcrossBuckets(commandIndex, indirectCommands);
			return;
		}

		_renderer.EnsureMeshResources(record.Mesh);
		EncodeCommandForPassLane(
			commandIndex,
			record.DrawKind,
			record.BucketId,
			record.Mesh,
			indirectCommands,
			participation,
			resources,
			laneAvailable,
			bindingResolver);
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
		ReadOnlySpan<IGfxIndirectCommandBuffer> indirectCommands,
		DrawPassParticipation participation,
		in SharedDrawIndirectEncodeResources resources,
		Func<GpuDrawExecutionLaneDefinition, bool> laneAvailable,
		Func<GpuDrawExecutionLaneDefinition, SharedDrawGraphicsBufferBindings?> bindingResolver)
	{
		for (var bucketIndex = 0; bucketIndex < indirectCommands.Length; bucketIndex++)
		{
			for (var i = 1u; i < GpuDrawResources.MaxDrawCount; i++)
			{
				_backendBridge.ResetCommand(indirectCommands[bucketIndex], i);
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
				indirectCommands,
				participation,
				resources,
				laneAvailable,
				bindingResolver);
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

	private void ResetCommandAcrossBuckets(uint commandIndex, ReadOnlySpan<IGfxIndirectCommandBuffer> indirectCommands)
	{
		for (var i = 0; i < indirectCommands.Length; i++)
		{
			_backendBridge.ResetCommand(indirectCommands[i], commandIndex);
		}
	}

	private void EncodeCommandForPassLane(
		uint commandIndex,
		GpuDrawKind drawKind,
		GpuDrawBucketId bucketId,
		Mesh mesh,
		ReadOnlySpan<IGfxIndirectCommandBuffer> indirectCommands,
		DrawPassParticipation participation,
		in SharedDrawIndirectEncodeResources resources,
		Func<GpuDrawExecutionLaneDefinition, bool> laneAvailable,
		Func<GpuDrawExecutionLaneDefinition, SharedDrawGraphicsBufferBindings?> bindingResolver)
	{
		if (GpuDrawExecutionLanes.TryGetDefinition(drawKind, bucketId, out var targetLane) == false ||
		    targetLane.SupportsPass(participation) == false ||
		    laneAvailable(targetLane) == false)
		{
			ResetCommandAcrossBuckets(commandIndex, indirectCommands);
			return;
		}

		for (var i = 0; i < indirectCommands.Length; i++)
		{
			if (i == targetLane.ExecutionIndex)
			{
				var bindings = bindingResolver(targetLane)
				               ?? throw new InvalidOperationException(
					               $"Missing reflected shared-draw buffer bindings for execution lane '{targetLane.DebugName}'.");
				if (_backendBridge.TryEncodeIndexedDrawCommand(indirectCommands[i], commandIndex, mesh, resources, bindings) == false)
				{
					_backendBridge.ResetCommand(indirectCommands[i], commandIndex);
				}
				continue;
			}

			_backendBridge.ResetCommand(indirectCommands[i], commandIndex);
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
		ref float scale)
	{
		albedoHandle = RegisterTerrainTexture(layer.Albedo);
		normalHandle = RegisterTerrainTexture(layer.Normal);
		ormHandle = RegisterTerrainTexture(layer.Orm);
		heightHandle = RegisterTerrainTexture(layer.Height);
		hasHeight = layer.Height is null ? 0u : 1u;
		scale = Math.Max(layer.Scale, 0.001f);
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

	private static string GetGBufferShaderPath(GpuDrawKind drawKind) => drawKind switch
	{
		GpuDrawKind.Mesh => "gbuffer.slang",
		GpuDrawKind.DebugPrimitive => "debug_primitive_gbuffer.slang",
		GpuDrawKind.Terrain => "terrain_shared_gbuffer.slang",
		_ => throw new NotSupportedException($"G-buffer shared draw kind '{drawKind}' does not define a shader.")
	};

	private void UploadFallbackTableEntries()
	{
		var fallbackMeshData = new GpuMeshData(
			_bindlessRegistry.ErrorBufferHandle.Value,
			_bindlessRegistry.ErrorBufferHandle.Value,
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
			64.0f);
		var fallbackTerrainLayerData = new GpuTerrainLayerData(
			_bindlessRegistry.ErrorTextureHandle.Value,
			_bindlessRegistry.ErrorTextureHandle.Value,
			_bindlessRegistry.ErrorTextureHandle.Value,
			_bindlessRegistry.ErrorTextureHandle.Value,
			0,
			1.0f);
		WriteBufferElement(_gpuDrawResources.MeshBuffer!, fallbackMeshData, 0, "MeshBuffer");
		WriteBufferElement(_gpuDrawResources.MaterialBuffer!, fallbackMaterialData, 0, "MaterialBuffer");
		WriteBufferElement(_gpuDrawResources.TerrainMaterialBuffer!, fallbackTerrainMaterialData, 0, "TerrainMaterialBuffer");
		WriteBufferElement(_gpuDrawResources.TerrainLayerBuffer!, fallbackTerrainLayerData, 0, "TerrainLayerBuffer");
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

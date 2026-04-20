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
	private IGfxPipeline? _updatePipeline;
	private IGfxPipeline? _cullPipeline;
	private GraphicsBackendKind? _computeReflectionBackendKind;
	private ReadOnlyMemory<byte> _updateShaderBytecode;
	private ReadOnlyMemory<byte> _cullShaderBytecode;
	private ComputeThreadGroupSize? _updateThreadGroupSize;
	private ComputeThreadGroupSize? _cullThreadGroupSize;
	private ShaderPropertyWriter? _updateParamsWriter;
	private ShaderPropertyWriter? _cullParamsWriter;
	private readonly List<GpuDrawUpdate> _updates = new();
	private readonly List<GpuDrawUpdateData> _updateData = new();
	private readonly List<GpuDrawEntry> _drawEntries = new();
	private readonly List<uint> _drawGenerations = new();
	private readonly List<uint> _instanceGenerations = new();
	private readonly List<uint> _meshGenerations = new();
	private readonly List<uint> _materialGenerations = new();
	private readonly uint[] _lastGpuDiagnosticCounters = new uint[GpuDrawResources.HardeningCounterCount];
	private readonly List<StructuralCommandRecord> _structuralReplayRecords = new();
	private readonly ulong[] _slotAppliedVersions = new ulong[GpuDrawResources.IndirectCommandBufferSlotCount];
	private readonly uint[] _slotBindlessEpochs = new uint[GpuDrawResources.IndirectCommandBufferSlotCount];
	private readonly int[] _slotFrameBindings = new int[GpuDrawResources.IndirectCommandBufferSlotCount];
	private readonly Dictionary<uint, MaterialDrawState> _materialDrawStates = new();
	private int _activeIndirectSlot = -1;
	private ulong _latestStructuralVersion;
	private ulong _nextStructuralVersion = 1;
	private uint _bindlessEpoch = 1;
	private bool _loggedCapacityExceeded;
	private bool _loggedUpdateOverflowRecovery;
	private bool _gpuStateBootstrapPending = true;

	private const uint DrawFlagActive = GpuDrawFlags.Active;
	private const int DrawFlagBucketShift = GpuDrawFlags.BucketShift;
	private const uint DrawFlagBucketMask = GpuDrawFlags.BucketMask;

	private readonly struct StructuralCommandRecord
	{
		public StructuralCommandRecord(ulong version, GpuDrawUpdateType type, GpuDrawHandle drawHandle, GpuDrawBucketId bucketId, Mesh? mesh)
		{
			Version = version;
			Type = type;
			DrawHandle = drawHandle;
			BucketId = bucketId;
			Mesh = mesh;
		}

		public ulong Version { get; }
		public GpuDrawUpdateType Type { get; }
		public GpuDrawHandle DrawHandle { get; }
		public GpuDrawBucketId BucketId { get; }
		public int ExecutionIndex => GBufferDrawBuckets.GetExecutionIndex(BucketId);
		public Mesh? Mesh { get; }
	}

	private readonly record struct MaterialDrawState(GpuDrawBucketId BucketId, int ExecutionIndex, uint DrawFlags);
	
	public GpuDrawPass(IShaderCompiler shaderCompiler,
		BindlessResourceRegistry bindlessRegistry, GpuDrawResources gpuDrawResources, GpuDrawHardeningStats hardeningStats, IRenderer renderer,
		IGpuDrawBackendBridge backendBridge)
	{
		_shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
		_gpuDrawResources = gpuDrawResources ?? throw new ArgumentNullException(nameof(gpuDrawResources));
		_hardeningStats = hardeningStats ?? throw new ArgumentNullException(nameof(hardeningStats));
		_renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
		_backendBridge = backendBridge ?? throw new ArgumentNullException(nameof(backendBridge));
		Array.Fill(_slotFrameBindings, -1);
	}

	public void RecordUpdate(RenderGraphContext context)
	{
		var drawDatabase = context.GpuDrawDatabase;
		var device = _renderer.GetGfxDevice();
		_bindlessRegistry.EnsureInitialized(device);
		_gpuDrawResources.EnsureCreated(device);
		SampleVisibilityDiagnostics();
		_hardeningStats.ResetSubmissionDiagnostics();
		EnsureGBufferPipelines(device);
		var primaryGBufferPipeline = GetPrimaryGBufferPipeline();
		var backendSignals = _backendBridge.PrepareFrame(device, _renderer, _gpuDrawResources, primaryGBufferPipeline);
		if (backendSignals.RequiresFullSlotReencode)
		{
			_bindlessEpoch++;
		}

		var activeSlot = AdvanceActiveIndirectSlot();
		_gpuDrawResources.ActiveIndirectCommandSlot = activeSlot;
		_gpuDrawResources.ActiveFrameSlot = activeSlot;
		IGfxIndirectCommandBuffer[]? activeIndirectCommands = null;
		var requireFullGpuStateRefresh = false;
		if (backendSignals.SupportsIndirectStructuralUpdates &&
		    _backendBridge.TryGetSlotIndirectCommands(_gpuDrawResources, activeSlot, out var slotCommands))
		{
			activeIndirectCommands = slotCommands;
			if (activeIndirectCommands.Length > 0)
			{
				var slotFrameMismatch = _slotFrameBindings[activeSlot] != _gpuDrawResources.ActiveFrameSlot;
				if (_slotBindlessEpochs[activeSlot] != _bindlessEpoch || slotFrameMismatch)
				{
					using (FrameProfiler.Instance.Measure("GpuDraw.FullSlotReencode"))
					{
						ReencodeAllIndirectCommands(drawDatabase, activeIndirectCommands);
					}
					requireFullGpuStateRefresh = true;

					_slotBindlessEpochs[activeSlot] = _bindlessEpoch;
					_slotAppliedVersions[activeSlot] = _latestStructuralVersion;
					_slotFrameBindings[activeSlot] = _gpuDrawResources.ActiveFrameSlot;
					CompactStructuralReplayRecords();
				}
				else
				{
					using (FrameProfiler.Instance.Measure("GpuDraw.StructuralReplay"))
					{
						ReplayPendingStructuralRecords(drawDatabase, activeSlot, activeIndirectCommands);
					}
				}
			}
		}

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
		_updateData.Clear();

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
				var instanceIdInRange = update.InstanceIndex > 0 && update.InstanceIndex < GpuDrawResources.MaxInstanceCount;
				var meshIdInRange = update.MeshIndex > 0 && update.MeshIndex < GpuDrawResources.MaxMeshCount;
				var materialIdInRange = update.MaterialIndex > 0 && update.MaterialIndex < GpuDrawResources.MaxMaterialCount;
				if (instanceIdInRange == false || meshIdInRange == false || materialIdInRange == false)
				{
					LogCapacityExceededOnce(in update);
					update = GpuDrawUpdate.CreateRemove(update.DrawHandle, update.InstanceHandle);
				}
			}

			var mesh = update.Mesh;
			var material = update.Material;

			if (mesh is not null)
			{
				_renderer.EnsureMeshResources(mesh);
			}

			uint vertexHandle = _bindlessRegistry.ErrorBufferHandle.Value;
			uint indexHandle = _bindlessRegistry.ErrorBufferHandle.Value;
			uint indexCount = 0;
			uint indexFormat = 0;
			int baseVertex = 0;

			if (mesh?.VertexBuffer is not null && mesh.IndexBuffer is not null)
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
			uint mrHandle = _bindlessRegistry.ErrorTextureHandle.Value;
			uint normalHandle = _bindlessRegistry.ErrorTextureHandle.Value;
			uint occlusionHandle = _bindlessRegistry.ErrorTextureHandle.Value;
			uint emissiveHandle = _bindlessRegistry.ErrorTextureHandle.Value;
			uint samplerHandle = _bindlessRegistry.ErrorSamplerHandle.Value;
			var baseColor = ColorRGBA.White;
			var metallicRoughness = Vector4.One;
			var emissiveFactorIntensity = Vector4.Zero;
			var bucketId = GpuDrawBucketId.Opaque;
			var bucketExecutionIndex = GBufferDrawBuckets.GetExecutionIndex(bucketId);
			uint drawFlags = update.Type == GpuDrawUpdateType.Remove ? 0u : CreateDrawFlags(bucketExecutionIndex);
			var alphaCutoff = 0.0f;
			var materialReady = material?.HasGpuResources ?? false;

			var materialResources = material?.Resources;
			if (material is not null)
			{
				bucketId = GBufferDrawBuckets.ResolveBucketId(material.AlphaMode);
				bucketExecutionIndex = GBufferDrawBuckets.GetExecutionIndex(bucketId);
				switch (material.AlphaMode)
				{
					case AlphaMode.AlphaTest:
						alphaCutoff = Math.Clamp(material.AlphaCutoff, 0.0f, 1.0f);
						break;
				}

				var desiredFlags = update.Type == GpuDrawUpdateType.Remove ? 0u : CreateDrawFlags(bucketExecutionIndex);
				drawFlags = materialReady ? desiredFlags : 0u;
				_materialDrawStates[update.MaterialHandle.Value] = new MaterialDrawState(bucketId, bucketExecutionIndex, drawFlags);
				if (materialReady == false && update.Type != GpuDrawUpdateType.Remove)
				{
					_hardeningStats.AddMaterialFallbackIncident(bucketId);
				}
			}
			else if (update.Type != GpuDrawUpdateType.Remove &&
			         _materialDrawStates.TryGetValue(update.MaterialHandle.Value, out var cachedState))
			{
				drawFlags = cachedState.DrawFlags;
				bucketId = cachedState.BucketId;
				bucketExecutionIndex = cachedState.ExecutionIndex;
			}

			if (backendSignals.SupportsIndirectStructuralUpdates &&
			    activeIndirectCommands is not null &&
			    IsStructuralUpdateType(update.Type) &&
			    ApplyStructuralUpdate(drawDatabase, update, mesh, activeIndirectCommands, bucketId))
			{
				AppendStructuralRecord(update, mesh, bucketId);
			}

			if (materialResources is not null)
			{
				albedoHandle = materialResources.AlbedoTexture.IsValid
					? materialResources.AlbedoTexture.Value
					: _bindlessRegistry.ErrorTextureHandle.Value;
				mrHandle = materialResources.MetallicRoughnessTexture.IsValid
					? materialResources.MetallicRoughnessTexture.Value
					: _bindlessRegistry.ErrorTextureHandle.Value;
				normalHandle = materialResources.NormalTexture.IsValid
					? materialResources.NormalTexture.Value
					: _bindlessRegistry.ErrorTextureHandle.Value;
				occlusionHandle = materialResources.OcclusionTexture.IsValid
					? materialResources.OcclusionTexture.Value
					: _bindlessRegistry.ErrorTextureHandle.Value;
				emissiveHandle = materialResources.EmissiveTexture.IsValid
					? materialResources.EmissiveTexture.Value
					: _bindlessRegistry.ErrorTextureHandle.Value;
				samplerHandle = materialResources.Sampler.IsValid
					? materialResources.Sampler.Value
					: _bindlessRegistry.ErrorSamplerHandle.Value;
				if (albedoHandle == _bindlessRegistry.ErrorTextureHandle.Value ||
				    mrHandle == _bindlessRegistry.ErrorTextureHandle.Value ||
				    normalHandle == _bindlessRegistry.ErrorTextureHandle.Value ||
				    occlusionHandle == _bindlessRegistry.ErrorTextureHandle.Value ||
				    emissiveHandle == _bindlessRegistry.ErrorTextureHandle.Value ||
				    samplerHandle == _bindlessRegistry.ErrorSamplerHandle.Value)
				{
					_hardeningStats.IncrementFallbackProxySubstitutions();
				}
				baseColor = material!.Color;
				metallicRoughness = new Vector4(material.MetallicFactor, material.RoughnessFactor, alphaCutoff, 0.0f);
				emissiveFactorIntensity = new Vector4(material.EmissiveFactor, material.EmissiveIntensity);
			}

				_updateData.Add(new GpuDrawUpdateData(
					update.PreviousWorld,
					update.World,
					update.BoundsCenterRadius,
					baseColor,
				metallicRoughness,
				emissiveFactorIntensity,
				(uint)update.Type,
				update.DrawHandle.Value,
					update.InstanceHandle.Value,
					update.MeshHandle.Value,
					update.MaterialHandle.Value,
					drawFlags,
					vertexHandle,
					indexHandle,
					indexCount,
				indexFormat,
				baseVertex,
				albedoHandle,
				mrHandle,
				normalHandle,
					occlusionHandle,
				emissiveHandle,
					samplerHandle));
			}

		if (activeIndirectCommands is not null && activeIndirectCommands.Length > 0)
		{
			_slotAppliedVersions[activeSlot] = _latestStructuralVersion;
			CompactStructuralReplayRecords();
		}

		if (_updateData.Count == 0)
		{
			return;
		}

		WriteBuffer<GpuDrawUpdateData>(_gpuDrawResources.UpdateBuffer!, CollectionsMarshal.AsSpan(_updateData), "UpdateBuffer");

		var pipeline = EnsureUpdatePipeline(device);
		var commandList = context.CommandList;
		using (FrameProfiler.Instance.Measure("GpuDraw.Update"))
		{
			commandList.BindPipeline(pipeline);

			var updateParamsWriter = _updateParamsWriter
				?? throw new InvalidOperationException("GpuDraw update reflection writer was not initialized.");
			updateParamsWriter.Clear();
			updateParamsWriter.SetUInt("updateCount", (uint)_updateData.Count);
			commandList.SetComputeConstants(updateParamsWriter.RegisterIndex, updateParamsWriter.AsBytes());

			commandList.SetComputeBuffer(0, _gpuDrawResources.UpdateBuffer!);
			commandList.SetComputeBuffer(1, _gpuDrawResources.InstanceBuffer!);
			commandList.SetComputeBuffer(2, _gpuDrawResources.MaterialBuffer!);
			commandList.SetComputeBuffer(3, _gpuDrawResources.MeshBuffer!);
			commandList.SetComputeBuffer(4, _gpuDrawResources.DrawCommandBuffer!);
			commandList.SetComputeBuffer(5, _gpuDrawResources.DrawGenerationBuffer!);
			commandList.SetComputeBuffer(6, _gpuDrawResources.InstanceGenerationBuffer!);
			commandList.SetComputeBuffer(7, _gpuDrawResources.MeshGenerationBuffer!);
			commandList.SetComputeBuffer(8, _gpuDrawResources.MaterialGenerationBuffer!);
			commandList.SetComputeBuffer(9, _gpuDrawResources.DiagnosticsCounterBuffer!);

			var threadGroupSize = _updateThreadGroupSize
				?? throw new InvalidOperationException("GpuDraw update threadgroup size was not initialized.");
			var (groupCountX, groupCountY, groupCountZ) = threadGroupSize.GetDispatchGroupCount((uint)_updateData.Count);
			commandList.Dispatch(groupCountX, groupCountY, groupCountZ);
		}

		PublishSubmittedBucketDiagnostics(drawDatabase);
	}

	public void RecordCull(RenderGraphContext context, SceneDrawData sceneData)
	{
		ArgumentNullException.ThrowIfNull(sceneData);
		RecordCullForView(context, sceneData.ViewProjection, sceneData.CameraOrigin, useShadowBuffers: false);
	}

	public void RecordCullForView(RenderGraphContext context, Matrix4x4 viewProjection, Vector3 cameraOrigin, bool useShadowBuffers = false)
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

		var bucketCount = GBufferDrawBuckets.BucketCount;
		if ((uint)bucketCount > (DrawFlagBucketMask + 1))
		{
			throw new InvalidOperationException(
				$"Configured bucket count {bucketCount} exceeds encoded bucket capacity {DrawFlagBucketMask + 1}.");
		}
		Span<uint> resetCounts = stackalloc uint[bucketCount];
		resetCounts.Clear();
		WriteBuffer<uint>(drawCountPerBucketBuffer!, resetCounts, "DrawCountPerBucketBuffer");

		Span<uint> resetRanges = stackalloc uint[bucketCount * 2];
		for (var i = 0; i < bucketCount; i++)
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
				?? throw new InvalidOperationException("GpuDraw cull reflection writer was not initialized.");
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
			cullParamsWriter.SetUInt("bucketCount", (uint)bucketCount);
			cullParamsWriter.SetUInt("maxVisiblePerBucket", GpuDrawResources.MaxDrawCount);
			cullParamsWriter.SetUInt("fallbackMeshHandle", context.GpuDrawDatabase.FallbackMeshHandle.Value);
			commandList.SetComputeConstants(cullParamsWriter.RegisterIndex, cullParamsWriter.AsBytes());

			commandList.SetComputeBuffer(0, _gpuDrawResources.DrawCommandBuffer!);
			commandList.SetComputeBuffer(1, _gpuDrawResources.InstanceBuffer!);
			commandList.SetComputeBuffer(2, _gpuDrawResources.MeshBuffer!);
			commandList.SetComputeBuffer(3, _gpuDrawResources.DrawArgsBuffer!);
			commandList.SetComputeBuffer(4, drawCountPerBucketBuffer!);
			commandList.SetComputeBuffer(5, _gpuDrawResources.VisibleDrawIdsPerBucketBuffer!);
			commandList.SetComputeBuffer(6, drawExecutionRangePerBucketBuffer!);
			commandList.SetComputeBuffer(7, _gpuDrawResources.DrawGenerationBuffer!);
			commandList.SetComputeBuffer(8, _gpuDrawResources.InstanceGenerationBuffer!);
			commandList.SetComputeBuffer(9, _gpuDrawResources.MeshGenerationBuffer!);
			commandList.SetComputeBuffer(10, _gpuDrawResources.MaterialGenerationBuffer!);
			commandList.SetComputeBuffer(11, _gpuDrawResources.DiagnosticsCounterBuffer!);

			var threadGroupSize = _cullThreadGroupSize
				?? throw new InvalidOperationException("GpuDraw cull threadgroup size was not initialized.");
				var (groupCountX, groupCountY, groupCountZ) = threadGroupSize.GetDispatchGroupCount(
					_gpuDrawResources.ActiveDrawCommandUpperBound);
			commandList.Dispatch(groupCountX, groupCountY, groupCountZ);
		}

	}

	private IGfxPipeline EnsureUpdatePipeline(IGfxDevice device)
	{
		if (_updatePipeline is not null)
		{
			return _updatePipeline;
		}

		EnsureComputeReflectionResources(device.BackendKind);
		var pipelineKey = new PipelineKey(PassKind.Compute, null, null, "CSUpdate", default, default, default);
		_updatePipeline = device.GetOrCreatePipeline(
			pipelineKey,
			new ShaderBytecodeSet(compute: _updateShaderBytecode, computeThreadGroupSize: _updateThreadGroupSize));
		return _updatePipeline;
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
		    _updateParamsWriter is not null &&
		    _cullParamsWriter is not null &&
		    _updateThreadGroupSize.HasValue &&
		    _cullThreadGroupSize.HasValue &&
		    _updateShaderBytecode.IsEmpty == false &&
		    _cullShaderBytecode.IsEmpty == false)
		{
			return;
		}

		var updateCompiled = _shaderCompiler.GetComputeShaderWithReflection(
			"gpu_draw_update.compute.slang",
			"CSUpdate",
			backendKind);
		_updateShaderBytecode = updateCompiled.Bytecode;
		_updateThreadGroupSize = updateCompiled.ThreadGroupSize;
		_updateParamsWriter = new ShaderPropertyWriter(updateCompiled.ReflectionLayout.GetConstantBuffer("UpdateParams"));

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
		var bucketDefinitions = GBufferDrawBuckets.GetDefinitionsForPass(DrawPassParticipation.GBuffer);
		var allPipelinesReady = true;
		for (var i = 0; i < bucketDefinitions.Length; i++)
		{
			var bucket = bucketDefinitions[i];
			if (_gpuDrawResources.GetGBufferPipeline(bucket.BucketId) is null)
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
		for (var i = 0; i < bucketDefinitions.Length; i++)
		{
			var bucket = bucketDefinitions[i];
			if (_gpuDrawResources.GetGBufferPipeline(bucket.BucketId) is not null)
			{
				continue;
			}

			var shaderSet = GraphicsShaderCompiler.Compile(
				_shaderCompiler,
				device.BackendKind,
				"gbuffer.slang",
				"vertexShader",
				"fragmentShader",
				bucket.PreprocessorDefine);
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
				shaderVariant: $"GBuffer:{bucket.ShaderVariant}");
			_gpuDrawResources.SetGBufferPipeline(
				bucket.BucketId,
				device.GetOrCreatePipeline(pipelineKey, shaderSet));
		}
	}

	private IGfxPipeline? GetPrimaryGBufferPipeline()
	{
		var bucketDefinitions = GBufferDrawBuckets.GetDefinitionsForPass(DrawPassParticipation.GBuffer);
		for (var i = 0; i < bucketDefinitions.Length; i++)
		{
			var pipeline = _gpuDrawResources.GetGBufferPipeline(bucketDefinitions[i].BucketId);
			if (pipeline is not null)
			{
				return pipeline;
			}
		}

		return null;
	}

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

	private bool ApplyStructuralUpdate(
		GpuDrawDatabase drawDatabase,
		in GpuDrawUpdate update,
		Mesh? mesh,
		IReadOnlyList<IGfxIndirectCommandBuffer> indirectCommands,
		GpuDrawBucketId bucketId)
	{
		if (IsStructuralUpdateType(update.Type) == false)
		{
			return false;
		}

		if (drawDatabase.IsCurrentDrawHandle(update.DrawHandle) == false)
		{
			return false;
		}

		if (update.DrawIndex <= 0 || update.DrawIndex >= GpuDrawResources.MaxDrawCount)
		{
			return false;
		}

		var commandIndex = (uint)update.DrawIndex;
		if (update.Type == GpuDrawUpdateType.Remove)
		{
			ResetCommandAcrossBuckets(commandIndex, indirectCommands);
			return true;
		}

		if (mesh is null)
		{
			ResetCommandAcrossBuckets(commandIndex, indirectCommands);
			return true;
		}

		var bucketExecutionIndex = GBufferDrawBuckets.GetExecutionIndex(bucketId);

		_renderer.EnsureMeshResources(mesh);
		for (var i = 0; i < indirectCommands.Count; i++)
		{
			if (i == bucketExecutionIndex)
			{
				if (TryEncodeIndirectCommand(commandIndex, mesh, indirectCommands[i]) == false)
				{
					_backendBridge.ResetCommand(indirectCommands[i], commandIndex);
				}
			}
			else
			{
				_backendBridge.ResetCommand(indirectCommands[i], commandIndex);
			}
		}

		return true;
	}

	private void ReplayPendingStructuralRecords(
		GpuDrawDatabase drawDatabase,
		int slotIndex,
		IReadOnlyList<IGfxIndirectCommandBuffer> indirectCommands)
	{
		var appliedVersion = _slotAppliedVersions[slotIndex];
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

			ApplyStructuralRecord(drawDatabase, record, indirectCommands);
		}

		_slotAppliedVersions[slotIndex] = _latestStructuralVersion;
	}

	private void ApplyStructuralRecord(
		GpuDrawDatabase drawDatabase,
		in StructuralCommandRecord record,
		IReadOnlyList<IGfxIndirectCommandBuffer> indirectCommands)
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
		if (record.Type == GpuDrawUpdateType.Remove)
		{
			ResetCommandAcrossBuckets(commandIndex, indirectCommands);
			return;
		}

		if (record.Mesh is null)
		{
			ResetCommandAcrossBuckets(commandIndex, indirectCommands);
			return;
		}

		var bucketExecutionIndex = record.ExecutionIndex;

		_renderer.EnsureMeshResources(record.Mesh);
		for (var i = 0; i < indirectCommands.Count; i++)
		{
			if (i == bucketExecutionIndex)
			{
				if (TryEncodeIndirectCommand(commandIndex, record.Mesh, indirectCommands[i]) == false)
				{
					_backendBridge.ResetCommand(indirectCommands[i], commandIndex);
				}
			}
			else
			{
				_backendBridge.ResetCommand(indirectCommands[i], commandIndex);
			}
		}
	}

	private void AppendStructuralRecord(in GpuDrawUpdate update, Mesh? mesh, GpuDrawBucketId bucketId)
	{
		var version = _nextStructuralVersion++;
		var type = update.Type == GpuDrawUpdateType.Remove ? GpuDrawUpdateType.Remove : update.Type;
		_structuralReplayRecords.Add(new StructuralCommandRecord(version, type, update.DrawHandle, bucketId, mesh));
		_latestStructuralVersion = version;
	}

	private void CompactStructuralReplayRecords()
	{
		var minAppliedVersion = ulong.MaxValue;
		for (var i = 0; i < _slotAppliedVersions.Length; i++)
		{
			if (_slotAppliedVersions[i] < minAppliedVersion)
			{
				minAppliedVersion = _slotAppliedVersions[i];
			}
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

	private void ReencodeAllIndirectCommands(GpuDrawDatabase drawDatabase, IReadOnlyList<IGfxIndirectCommandBuffer> indirectCommands)
	{
		for (var bucketIndex = 0; bucketIndex < indirectCommands.Count; bucketIndex++)
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

			_renderer.EnsureMeshResources(entry.Mesh);
			var bucketIndex = GBufferDrawBuckets.GetExecutionIndex(GBufferDrawBuckets.ResolveBucketId(entry.Material.AlphaMode));

			TryEncodeIndirectCommand((uint)entry.DrawIndex, entry.Mesh, indirectCommands[bucketIndex]);
		}
	}

	private void ResetCommandAcrossBuckets(uint commandIndex, IReadOnlyList<IGfxIndirectCommandBuffer> indirectCommands)
	{
		for (var i = 0; i < indirectCommands.Count; i++)
		{
			_backendBridge.ResetCommand(indirectCommands[i], commandIndex);
		}
	}

	private bool TryEncodeIndirectCommand(
		uint commandIndex,
		Mesh mesh,
		IGfxIndirectCommandBuffer indirectCommands)
	{
		var bucketDefinitions = GBufferDrawBuckets.Definitions;
		if (bucketDefinitions.Length == 0 ||
		    HasAnyGBufferPipeline() == false)
		{
			return false;
		}

		return _backendBridge.TryEncodeIndexedDrawCommand(indirectCommands, commandIndex, mesh, _gpuDrawResources);
	}

	private bool HasAnyGBufferPipeline()
	{
		var bucketDefinitions = GBufferDrawBuckets.GetDefinitionsForPass(DrawPassParticipation.GBuffer);
		for (var i = 0; i < bucketDefinitions.Length; i++)
		{
			if (_gpuDrawResources.GetGBufferPipeline(bucketDefinitions[i].BucketId) is not null)
			{
				return true;
			}
		}

		return false;
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
				entry.DrawHandle,
				entry.InstanceHandle,
				entry.MeshHandle,
				entry.MaterialHandle,
				entry.PreviousWorld,
				entry.World,
				entry.BoundsCenterRadius,
				entry.Mesh,
				entry.Material));
		}

		return destination.Count - initialCount;
	}

	private void AppendClearAllDraws(List<GpuDrawUpdate> destination)
	{
		for (var drawIndex = 1; drawIndex < GpuDrawResources.MaxDrawCount; drawIndex++)
		{
			var drawHandle = GpuDrawHandle.Create(drawIndex, 1);
			destination.Add(GpuDrawUpdate.CreateRemove(drawHandle, GpuDrawHandle.Invalid));
		}
	}

	private void UploadGenerationTables(GpuDrawDatabase drawDatabase)
	{
		drawDatabase.CopyGenerationTables(
			_drawGenerations,
			_instanceGenerations,
			_meshGenerations,
			_materialGenerations);

		WriteBuffer<uint>(_gpuDrawResources.DrawGenerationBuffer!, CollectionsMarshal.AsSpan(_drawGenerations), "DrawGenerationBuffer");
		WriteBuffer<uint>(_gpuDrawResources.InstanceGenerationBuffer!, CollectionsMarshal.AsSpan(_instanceGenerations), "InstanceGenerationBuffer");
		WriteBuffer<uint>(_gpuDrawResources.MeshGenerationBuffer!, CollectionsMarshal.AsSpan(_meshGenerations), "MeshGenerationBuffer");
		WriteBuffer<uint>(_gpuDrawResources.MaterialGenerationBuffer!, CollectionsMarshal.AsSpan(_materialGenerations), "MaterialGenerationBuffer");

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
			var bucketId = GBufferDrawBuckets.ResolveBucketId(entry.Material.AlphaMode);
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
			_bindlessRegistry.ErrorTextureHandle.Value,
			_bindlessRegistry.ErrorSamplerHandle.Value);
		WriteBufferElement(_gpuDrawResources.MeshBuffer!, fallbackMeshData, 0, "MeshBuffer");
		WriteBufferElement(_gpuDrawResources.MaterialBuffer!, fallbackMaterialData, 0, "MaterialBuffer");
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

	private static void WriteBufferElement<T>(IGfxBuffer buffer, in T data, ulong elementOffset, string bufferName) where T : unmanaged
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

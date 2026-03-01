#nullable enable

using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using WolfEngine;
using WolfEngine.Profiling;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Backend.Metal;

namespace WolfEngine.Rendering.Passes;

public sealed class GpuDrawPass
{
	private readonly IShaderCompiler _shaderCompiler;
	private readonly GpuDrawDatabase _drawDatabase;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private readonly GpuDrawResources _gpuDrawResources;
	private readonly GpuDrawHardeningStats _hardeningStats;
	private readonly IRenderer _renderer;
	private IGfxPipeline? _updatePipeline;
	private IGfxPipeline? _cullPipeline;
	private readonly List<GpuDrawUpdate> _updates = new();
	private readonly List<GpuDrawUpdateData> _updateData = new();
	private readonly List<GpuDrawEntry> _drawEntries = new();
	private readonly List<uint> _drawGenerations = new();
	private readonly List<uint> _instanceGenerations = new();
	private readonly List<uint> _meshGenerations = new();
	private readonly List<uint> _materialGenerations = new();
	private readonly uint[] _lastGpuDiagnosticCounters = new uint[GpuDrawResources.HardeningCounterCount];
	private readonly List<StructuralCommandRecord> _structuralReplayRecords = new();
	private nint _lastBindlessCountBufferPtr;
	private nint _lastBindlessTextureBufferPtr;
	private nint _lastBindlessRwTextureBufferPtr;
	private nint _lastBindlessSamplerBufferPtr;
	private readonly ulong[] _slotAppliedVersions = new ulong[GpuDrawResources.IndirectCommandBufferSlotCount];
	private readonly uint[] _slotBindlessEpochs = new uint[GpuDrawResources.IndirectCommandBufferSlotCount];
	private readonly int[] _slotFrameBindings = new int[GpuDrawResources.IndirectCommandBufferSlotCount];
	private readonly Dictionary<uint, uint> _materialDrawFlags = new();
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
		public StructuralCommandRecord(ulong version, GpuDrawUpdateType type, GpuDrawHandle drawHandle, int bucketIndex, Mesh? mesh)
		{
			Version = version;
			Type = type;
			DrawHandle = drawHandle;
			BucketIndex = bucketIndex;
			Mesh = mesh;
		}

		public ulong Version { get; }
		public GpuDrawUpdateType Type { get; }
		public GpuDrawHandle DrawHandle { get; }
		public int BucketIndex { get; }
		public Mesh? Mesh { get; }
	}
	
	public GpuDrawPass(IShaderCompiler shaderCompiler, GpuDrawDatabase drawDatabase,
		BindlessResourceRegistry bindlessRegistry, GpuDrawResources gpuDrawResources, GpuDrawHardeningStats hardeningStats, IRenderer renderer)
	{
		_shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
		_drawDatabase = drawDatabase ?? throw new ArgumentNullException(nameof(drawDatabase));
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
		_gpuDrawResources = gpuDrawResources ?? throw new ArgumentNullException(nameof(gpuDrawResources));
		_hardeningStats = hardeningStats ?? throw new ArgumentNullException(nameof(hardeningStats));
		_renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
		Array.Fill(_slotFrameBindings, -1);
	}

	public void RecordUpdate(RenderGraphContext context)
	{
		var isMacOS = OperatingSystem.IsMacOS();
		var device = _renderer.GetGfxDevice();
		_bindlessRegistry.EnsureInitialized(device);
		_gpuDrawResources.EnsureCreated(device);
		EnsureGBufferPipelines(device);
		if (isMacOS)
		{
			EnsureBindlessArgumentBuffersForGBuffer(device);
		}

		var activeSlot = AdvanceActiveIndirectSlot();
		_gpuDrawResources.ActiveIndirectCommandSlot = activeSlot;
		_gpuDrawResources.ActiveFrameSlot = activeSlot;
		MetalDescriptorTable? metalTable = null;
		MetalIndirectCommandBuffer[]? activeIndirectCommands = null;
		var requireFullGpuStateRefresh = false;
		if (isMacOS && device is MetalDevice && device.GlobalTable is MetalDescriptorTable table)
		{
			metalTable = table;
			if (TryGetSlotIndirectCommands(activeSlot, out var slotCommands))
			{
				activeIndirectCommands = slotCommands;
			}
			if (_renderer is WolfRendererMetal metalRenderer &&
			    metalRenderer.ConsumePackedGeometryRefresh())
			{
				_bindlessEpoch++;
			}

			if (BindlessPointersChanged(table))
			{
				_bindlessEpoch++;
				CacheBindlessPointers(table);
			}

				if (activeIndirectCommands is not null && activeIndirectCommands.Length > 0)
				{
					var slotFrameMismatch = _slotFrameBindings[activeSlot] != _gpuDrawResources.ActiveFrameSlot;
					if (_slotBindlessEpochs[activeSlot] != _bindlessEpoch || slotFrameMismatch)
						{
							using (FrameProfiler.Instance.Measure("GpuDraw.FullSlotReencode"))
							{
								ReencodeAllIndirectCommands(table, activeIndirectCommands);
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
						ReplayPendingStructuralRecords(activeSlot, table, activeIndirectCommands);
					}
				}
			}
		}

		_drawDatabase.ConsumeUpdates(_updates);
			UploadGenerationTables();
			if (isMacOS)
			{
				SampleGpuDiagnosticCounters();
			}
		if (_gpuStateBootstrapPending)
		{
			var appended = AppendFullGpuStateRefreshUpdates(_updates);
			if (appended > 0)
			{
				_gpuStateBootstrapPending = false;
			}
		}
		if (requireFullGpuStateRefresh)
		{
			AppendFullGpuStateRefreshUpdates(_updates);
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

		_gpuDrawResources.ActiveDrawCommandUpperBound = _drawDatabase.GetActiveDrawCommandUpperBound();
		_updateData.Clear();

		var updateCount = Math.Min(_updates.Count, GpuDrawResources.MaxDrawCount);
		var reencodeAfterUpdates = false;

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
			var baseColor = Vector4.One;
			var metallicRoughness = Vector4.One;
			var bucketIndex = 0;
			uint drawFlags = update.Type == GpuDrawUpdateType.Remove ? 0u : CreateDrawFlags(bucketIndex);
			var alphaCutoff = 0.0f;

			var materialResources = material?.Resources;
			if (material is not null)
			{
				bucketIndex = GetMaterialBucketIndex(material.AlphaMode);
				switch (material.AlphaMode)
				{
					case AlphaMode.AlphaTest:
						alphaCutoff = Math.Clamp(material.AlphaCutoff, 0.0f, 1.0f);
						break;
				}

				drawFlags = update.Type == GpuDrawUpdateType.Remove ? 0u : CreateDrawFlags(bucketIndex);
				_materialDrawFlags[update.MaterialHandle.Value] = drawFlags;
			}
			else if (update.Type != GpuDrawUpdateType.Remove &&
			         _materialDrawFlags.TryGetValue(update.MaterialHandle.Value, out var cachedFlags))
			{
				drawFlags = cachedFlags;
				bucketIndex = (int)((cachedFlags >> DrawFlagBucketShift) & DrawFlagBucketMask);
			}

			if (isMacOS &&
			    activeIndirectCommands is not null &&
			    metalTable is not null &&
			    IsStructuralUpdateType(update.Type) &&
			    ApplyStructuralUpdate(update, mesh, metalTable, activeIndirectCommands, bucketIndex))
			{
				AppendStructuralRecord(update, mesh, bucketIndex);
			}
			else if (update.Type == GpuDrawUpdateType.UpdateMaterial &&
			         activeIndirectCommands is not null &&
			         metalTable is not null)
			{
				reencodeAfterUpdates = true;
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
			}

				_updateData.Add(new GpuDrawUpdateData(
					update.World,
					update.BoundsCenterRadius,
					baseColor,
				metallicRoughness,
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

		if (isMacOS &&
		    reencodeAfterUpdates &&
		    activeIndirectCommands is not null &&
		    activeIndirectCommands.Length > 0 &&
		    metalTable is not null)
		{
			ReencodeAllIndirectCommands(metalTable, activeIndirectCommands);
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

			Span<uint> updateParams = stackalloc uint[4];
			updateParams[0] = (uint)_updateData.Count;
			updateParams[1] = 0;
			updateParams[2] = 0;
			updateParams[3] = 0;
				commandList.SetComputeConstants(11, MemoryMarshal.AsBytes(updateParams));

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

			var groupCount = (uint)((_updateData.Count + 63) / 64);
			commandList.Dispatch(groupCount, 1, 1);
		}
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

		var bucketCount = GBufferDrawBuckets.Definitions.Length;
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

			var cullParams = new CullParams
			{
				Plane0 = planes[0],
				Plane1 = planes[1],
				Plane2 = planes[2],
				Plane3 = planes[3],
				Plane4 = planes[4],
				Plane5 = planes[5],
				CameraPositionAndMaxDrawCount = new Vector4(
					cameraOrigin,
					GpuDrawResources.MaxDrawCount),
				BucketCount = (uint)bucketCount,
				MaxVisiblePerBucket = GpuDrawResources.MaxDrawCount,
				FallbackMeshHandle = _drawDatabase.FallbackMeshHandle.Value
			};

				commandList.SetComputeConstants(12, MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref cullParams, 1)));

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

			var groupCount = (uint)((GpuDrawResources.MaxDrawCount + 63) / 64);
			commandList.Dispatch(groupCount, 1, 1);
		}

	}

	private IGfxPipeline EnsureUpdatePipeline(IGfxDevice device)
	{
		if (_updatePipeline is not null)
		{
			return _updatePipeline;
		}

		var bytes = _shaderCompiler.GetMetalComputeLibrary("gpu_draw_update.compute.slang", "CSUpdate");
		var pipelineKey = new PipelineKey(PassKind.Compute, null, null, "CSUpdate", default, default, default);
		_updatePipeline = device.GetOrCreatePipeline(pipelineKey, new ShaderBytecodeSet(compute: bytes));
		return _updatePipeline;
	}

	private IGfxPipeline EnsureCullPipeline(IGfxDevice device)
	{
		if (_cullPipeline is not null)
		{
			return _cullPipeline;
		}

		var bytes = _shaderCompiler.GetMetalComputeLibrary("gpu_draw_cull.compute.slang", "CSCull");
		var pipelineKey = new PipelineKey(PassKind.Compute, null, null, "CSCull", default, default, default);
		_cullPipeline = device.GetOrCreatePipeline(pipelineKey, new ShaderBytecodeSet(compute: bytes));
		return _cullPipeline;
	}

	private void EnsureGBufferPipelines(IGfxDevice device)
	{
		var bucketDefinitions = GBufferDrawBuckets.Definitions;
		var allPipelinesReady = true;
		for (var i = 0; i < bucketDefinitions.Length; i++)
		{
			var bucket = bucketDefinitions[i];
			if (bucket.SupportsPass(DrawPassParticipation.GBuffer) == false)
			{
				continue;
			}

			if (_gpuDrawResources.GetGBufferPipeline(i) is null)
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
			if (bucket.SupportsPass(DrawPassParticipation.GBuffer) == false)
			{
				continue;
			}

			if (_gpuDrawResources.GetGBufferPipeline(i) is not null)
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
					TextureFormat.Rgba8Unorm
				}),
				depthStencil: new DepthStencilFormat(TextureFormat.D32Float),
				renderState: renderState,
				layout: GraphicsLayoutKind.Material,
				shaderVariant: $"GBuffer:{bucket.ShaderVariant}");
			_gpuDrawResources.SetGBufferPipeline(
				i,
				device.GetOrCreatePipeline(pipelineKey, shaderSet));
		}
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
		type is GpuDrawUpdateType.Add or GpuDrawUpdateType.Remove or GpuDrawUpdateType.UpdateMesh;

	[SupportedOSPlatform("macos")]
	private bool ApplyStructuralUpdate(
		in GpuDrawUpdate update,
		Mesh? mesh,
		MetalDescriptorTable table,
		IReadOnlyList<MetalIndirectCommandBuffer> indirectCommands,
		int bucketIndex)
	{
		if (IsStructuralUpdateType(update.Type) == false)
		{
			return false;
		}

		if (_drawDatabase.IsCurrentDrawHandle(update.DrawHandle) == false)
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

		if (bucketIndex < 0 || bucketIndex >= indirectCommands.Count)
		{
			bucketIndex = 0;
		}

		_renderer.EnsureMeshResources(mesh);
		for (var i = 0; i < indirectCommands.Count; i++)
		{
			if (i == bucketIndex)
			{
				if (TryEncodeIndirectCommand(commandIndex, mesh, table, indirectCommands[i]) == false)
				{
					indirectCommands[i].ResetCommand(commandIndex);
				}
			}
			else
			{
				indirectCommands[i].ResetCommand(commandIndex);
			}
		}

		return true;
	}

	[SupportedOSPlatform("macos")]
	private void ReplayPendingStructuralRecords(
		int slotIndex,
		MetalDescriptorTable table,
		IReadOnlyList<MetalIndirectCommandBuffer> indirectCommands)
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

			ApplyStructuralRecord(record, table, indirectCommands);
		}

		_slotAppliedVersions[slotIndex] = _latestStructuralVersion;
	}

	[SupportedOSPlatform("macos")]
	private void ApplyStructuralRecord(
		in StructuralCommandRecord record,
		MetalDescriptorTable table,
		IReadOnlyList<MetalIndirectCommandBuffer> indirectCommands)
	{
		if (_drawDatabase.IsCurrentDrawHandle(record.DrawHandle) == false)
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

		var bucketIndex = record.BucketIndex;
		if (bucketIndex < 0 || bucketIndex >= indirectCommands.Count)
		{
			bucketIndex = 0;
		}

		_renderer.EnsureMeshResources(record.Mesh);
		for (var i = 0; i < indirectCommands.Count; i++)
		{
			if (i == bucketIndex)
			{
				if (TryEncodeIndirectCommand(commandIndex, record.Mesh, table, indirectCommands[i]) == false)
				{
					indirectCommands[i].ResetCommand(commandIndex);
				}
			}
			else
			{
				indirectCommands[i].ResetCommand(commandIndex);
			}
		}
	}

	private void AppendStructuralRecord(in GpuDrawUpdate update, Mesh? mesh, int bucketIndex)
	{
		var version = _nextStructuralVersion++;
		var type = update.Type == GpuDrawUpdateType.Remove ? GpuDrawUpdateType.Remove : update.Type;
		_structuralReplayRecords.Add(new StructuralCommandRecord(version, type, update.DrawHandle, bucketIndex, mesh));
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

	[SupportedOSPlatform("macos")]
	private void EnsureBindlessArgumentBuffersForGBuffer(IGfxDevice device)
	{
		var bucketDefinitions = GBufferDrawBuckets.Definitions;
		if (bucketDefinitions.Length == 0)
		{
			return;
		}

		var primaryPipeline = default(IGfxPipeline);
		for (var i = 0; i < bucketDefinitions.Length; i++)
		{
			if (bucketDefinitions[i].SupportsPass(DrawPassParticipation.GBuffer) == false)
			{
				continue;
			}

			primaryPipeline = _gpuDrawResources.GetGBufferPipeline(i);
			if (primaryPipeline is not null)
			{
				break;
			}
		}

		if (primaryPipeline is not MetalPipeline metalPipeline ||
		    device.GlobalTable is not MetalDescriptorTable metalTable)
		{
			return;
		}

		metalTable.SetArgumentEncoders(
			metalPipeline.TextureEncoder,
			metalPipeline.RWTextureEncoder,
			metalPipeline.SamplerEncoder);
	}

	[SupportedOSPlatform("macos")]
	private bool BindlessPointersChanged(MetalDescriptorTable table)
	{
		return _lastBindlessCountBufferPtr != table.CountBuffer.NativePtr ||
		       _lastBindlessTextureBufferPtr != table.TextureArgumentBuffer.NativePtr ||
		       _lastBindlessRwTextureBufferPtr != table.RWTextureArgumentBuffer.NativePtr ||
		       _lastBindlessSamplerBufferPtr != table.SamplerArgumentBuffer.NativePtr;
	}

	[SupportedOSPlatform("macos")]
	private void CacheBindlessPointers(MetalDescriptorTable table)
	{
		_lastBindlessCountBufferPtr = table.CountBuffer.NativePtr;
		_lastBindlessTextureBufferPtr = table.TextureArgumentBuffer.NativePtr;
		_lastBindlessRwTextureBufferPtr = table.RWTextureArgumentBuffer.NativePtr;
		_lastBindlessSamplerBufferPtr = table.SamplerArgumentBuffer.NativePtr;
	}

	[SupportedOSPlatform("macos")]
	private void ReencodeAllIndirectCommands(MetalDescriptorTable table, IReadOnlyList<MetalIndirectCommandBuffer> indirectCommands)
	{
		for (var bucketIndex = 0; bucketIndex < indirectCommands.Count; bucketIndex++)
		{
			for (var i = 1u; i < GpuDrawResources.MaxDrawCount; i++)
			{
				indirectCommands[bucketIndex].ResetCommand(i);
			}
		}

		_drawDatabase.CollectDrawEntries(_drawEntries);
		for (var i = 0; i < _drawEntries.Count; i++)
		{
			var entry = _drawEntries[i];
			if (entry.DrawIndex <= 0 || entry.DrawIndex >= GpuDrawResources.MaxDrawCount)
			{
				continue;
			}

			_renderer.EnsureMeshResources(entry.Mesh);
			var bucketIndex = GetMaterialBucketIndex(entry.Material.AlphaMode);
			if (bucketIndex < 0 || bucketIndex >= indirectCommands.Count)
			{
				bucketIndex = 0;
			}

			TryEncodeIndirectCommand((uint)entry.DrawIndex, entry.Mesh, table, indirectCommands[bucketIndex]);
		}
	}

	[SupportedOSPlatform("macos")]
	private bool TryGetSlotIndirectCommands(int slotIndex, out MetalIndirectCommandBuffer[] commandBuffers)
	{
		commandBuffers = Array.Empty<MetalIndirectCommandBuffer>();
		var bucketCount = GBufferDrawBuckets.BucketCount;
		if (bucketCount <= 0)
		{
			return false;
		}

		var resolved = new MetalIndirectCommandBuffer[bucketCount];
		for (var i = 0; i < bucketCount; i++)
		{
			if (_gpuDrawResources.GetIndirectCommandBufferSlot(slotIndex, i) is not MetalIndirectCommandBuffer commandBuffer)
			{
				return false;
			}

			resolved[i] = commandBuffer;
		}

		commandBuffers = resolved;
		return true;
	}

	[SupportedOSPlatform("macos")]
	private static void ResetCommandAcrossBuckets(uint commandIndex, IReadOnlyList<MetalIndirectCommandBuffer> indirectCommands)
	{
		for (var i = 0; i < indirectCommands.Count; i++)
		{
			indirectCommands[i].ResetCommand(commandIndex);
		}
	}

	[SupportedOSPlatform("macos")]
	private bool TryEncodeIndirectCommand(
		uint commandIndex,
		Mesh mesh,
		MetalDescriptorTable table,
		MetalIndirectCommandBuffer indirectCommands)
	{
		var bucketDefinitions = GBufferDrawBuckets.Definitions;
		if (bucketDefinitions.Length == 0 ||
		    HasAnyGBufferPipeline() == false ||
		    mesh.VertexBuffer is not MetalBuffer metalVertexBuffer ||
		    mesh.IndexBuffer is not MetalBuffer metalIndexBuffer ||
		    _gpuDrawResources.CameraBuffer is not MetalBuffer cameraBuffer ||
		    _gpuDrawResources.ShadowCameraBuffer is not MetalBuffer shadowCameraBuffer ||
		    _gpuDrawResources.TransparentEnvironmentBuffer is not MetalBuffer transparentEnvironmentBuffer ||
		    _gpuDrawResources.TransparentLightingBuffer is not MetalBuffer transparentLightingBuffer ||
		    _gpuDrawResources.InstanceBuffer is not MetalBuffer instanceBuffer ||
		    _gpuDrawResources.MaterialBuffer is not MetalBuffer materialBuffer ||
		    _gpuDrawResources.DrawArgsBuffer is not MetalBuffer drawArgsBuffer)
		{
			return false;
		}

		if (table.CountBuffer.NativePtr == IntPtr.Zero ||
		    table.TextureArgumentBuffer.NativePtr == IntPtr.Zero ||
		    table.SamplerArgumentBuffer.NativePtr == IntPtr.Zero ||
		    _gpuDrawResources.MaterialGenerationBuffer is not MetalBuffer materialGenerationBuffer)
		{
			return false;
		}

		indirectCommands.EncodeIndexedDrawCommand(
			commandIndex,
			metalVertexBuffer,
			mesh.PackedVertexOffsetBytes,
			metalIndexBuffer,
			IndexFormat.UInt32,
			mesh.IndexCount,
			mesh.PackedIndexOffsetBytes,
			0,
			commandIndex * (ulong)Marshal.SizeOf<GpuDrawArgs>(),
			cameraBuffer,
			shadowCameraBuffer,
			transparentEnvironmentBuffer,
			transparentLightingBuffer,
			instanceBuffer,
			materialBuffer,
			materialGenerationBuffer,
			drawArgsBuffer,
			table.CountBuffer,
			table.TextureArgumentBuffer,
			table.RWTextureArgumentBuffer,
			table.SamplerArgumentBuffer);
		return true;
	}

	private bool HasAnyGBufferPipeline()
	{
		var bucketDefinitions = GBufferDrawBuckets.Definitions;
		for (var i = 0; i < bucketDefinitions.Length; i++)
		{
			if (bucketDefinitions[i].SupportsPass(DrawPassParticipation.GBuffer) == false)
			{
				continue;
			}

			if (_gpuDrawResources.GetGBufferPipeline(i) is not null)
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

	private static int GetMaterialBucketIndex(AlphaMode alphaMode)
	{
		var bucketDefinitions = GBufferDrawBuckets.Definitions;
		if (bucketDefinitions.Length == 0)
		{
			return 0;
		}

		for (var i = 0; i < bucketDefinitions.Length; i++)
		{
			var supportedModes = bucketDefinitions[i].SupportedAlphaModes;
			for (var modeIndex = 0; modeIndex < supportedModes.Length; modeIndex++)
			{
				if (supportedModes[modeIndex] == alphaMode)
				{
					return i;
				}
			}
		}

		return 0;
	}

	private int AppendFullGpuStateRefreshUpdates(List<GpuDrawUpdate> destination)
	{
		var initialCount = destination.Count;
		_drawDatabase.CollectDrawEntries(_drawEntries);
		for (var i = 0; i < _drawEntries.Count; i++)
		{
			var entry = _drawEntries[i];
			destination.Add(GpuDrawUpdate.CreateAdd(
				entry.DrawHandle,
				entry.InstanceHandle,
				entry.MeshHandle,
				entry.MaterialHandle,
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

	private void UploadGenerationTables()
	{
		_drawDatabase.CopyGenerationTables(
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

	[SupportedOSPlatform("macos")]
	private unsafe void SampleGpuDiagnosticCounters()
	{
		if (_gpuDrawResources.DiagnosticsCounterBuffer is not MetalBuffer diagnosticsBuffer ||
		    diagnosticsBuffer.Buffer.NativePtr == IntPtr.Zero)
		{
			return;
		}

		var counters = new ReadOnlySpan<uint>(
			(void*)diagnosticsBuffer.Buffer.Contents.ToPointer(),
			GpuDrawResources.HardeningCounterCount);
		for (var i = 0; i < counters.Length; i++)
		{
			var current = counters[i];
			var previous = _lastGpuDiagnosticCounters[i];
			if (current < previous)
			{
				_lastGpuDiagnosticCounters[i] = current;
				continue;
			}

			var delta = current - previous;
			if (delta == 0)
			{
				continue;
			}

			_lastGpuDiagnosticCounters[i] = current;
			switch (i)
			{
				case 0:
					_hardeningStats.AddStaleHandleRejects(delta);
					break;
				case 1:
					_hardeningStats.AddFallbackProxySubstitutions(delta);
					break;
					case 4:
						_hardeningStats.AddVisibleListClampHits(delta);
						break;
					case 5:
						_hardeningStats.AddMaterialFallbackDrawHits(delta);
						break;
				}
			}
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
			new Vector4(1.0f, 0.0f, 1.0f, 1.0f),
			Vector4.One,
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

	[StructLayout(LayoutKind.Sequential)]
	private struct CullParams
	{
		public Vector4 Plane0;
		public Vector4 Plane1;
		public Vector4 Plane2;
		public Vector4 Plane3;
		public Vector4 Plane4;
		public Vector4 Plane5;
		public Vector4 CameraPositionAndMaxDrawCount;
		public uint BucketCount;
		public uint MaxVisiblePerBucket;
		public uint FallbackMeshHandle;
		public uint Pad0;
	}
}

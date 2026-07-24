using System.Numerics;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

public sealed class DdgiPass
{
	private const int FirstProbeRelocationDiagnosticFloatCount =
		36 * DdgiUtilities.RelocationIterationCount;
	private const int FirstProbeRelocationDiagnosticBufferSize =
		FirstProbeRelocationDiagnosticFloatCount * sizeof(float);

	private readonly IShaderProvider _shaderCompiler;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private IGfxPipeline? _classifyPipeline;
	private IGfxPipeline? _tracePipeline;
	private IGfxPipeline? _relocationTracePipeline;
	private IGfxPipeline? _relocatePipeline;
	private IGfxPipeline? _irradianceIntegratePipeline;
	private IGfxPipeline? _visibilityIntegratePipeline;
	private ReadOnlyMemory<byte> _classifyShader;
	private ReadOnlyMemory<byte> _traceShader;
	private ReadOnlyMemory<byte> _relocationTraceShader;
	private ReadOnlyMemory<byte> _relocateShader;
	private ReadOnlyMemory<byte> _irradianceIntegrateShader;
	private ReadOnlyMemory<byte> _visibilityIntegrateShader;
	private ComputeThreadGroupSize? _classifyThreadGroupSize;
	private ComputeThreadGroupSize? _traceThreadGroupSize;
	private ComputeThreadGroupSize? _relocationTraceThreadGroupSize;
	private ComputeThreadGroupSize? _relocateThreadGroupSize;
	private ComputeThreadGroupSize? _irradianceIntegrateThreadGroupSize;
	private ComputeThreadGroupSize? _visibilityIntegrateThreadGroupSize;
	private GraphicsBackendKind? _compiledBackendKind;
	private ShaderPropertyWriter? _classifyBindlessWriter;
	private ShaderPropertyWriter? _classifySettingsWriter;
	private ShaderPropertyWriter? _traceBindlessWriter;
	private ShaderPropertyWriter? _traceSettingsWriter;
	private ShaderPropertyWriter? _relocationTraceBindlessWriter;
	private ShaderPropertyWriter? _relocationTraceSettingsWriter;
	private ShaderPropertyWriter? _relocateBindlessWriter;
	private ShaderPropertyWriter? _relocateSettingsWriter;
	private ShaderPropertyWriter? _irradianceIntegrateBindlessWriter;
	private ShaderPropertyWriter? _irradianceIntegrateSettingsWriter;
	private ShaderPropertyWriter? _visibilityIntegrateBindlessWriter;
	private ShaderPropertyWriter? _visibilityIntegrateSettingsWriter;
	private DescriptorHandle _linearSampler = DescriptorHandle.Invalid;
	private IGfxDevice? _firstProbeRelocationDiagnosticDevice;
	private IGfxBuffer? _firstProbeRelocationDiagnosticBuffer;
	private IGfxBuffer? _firstProbeRelocationReadbackBuffer;
	private bool _firstProbeRelocationReadbackPending;
	private uint _frameIndex;

	public DdgiPassStats LastStats { get; private set; }
	public DdgiFirstProbeRelocationDiagnostic? LastFirstProbeRelocationDiagnostic { get; private set; }
	public IReadOnlyList<DdgiFirstProbeRelocationDiagnostic> LastFirstProbeRelocationDiagnostics { get; private set; } =
		Array.Empty<DdgiFirstProbeRelocationDiagnostic>();
	public DdgiFirstProbeRelocationDiagnostic? LastProbeRelocationDiagnostic =>
		LastFirstProbeRelocationDiagnostic;
	public IReadOnlyList<DdgiFirstProbeRelocationDiagnostic> LastProbeRelocationDiagnostics =>
		LastFirstProbeRelocationDiagnostics;

	public DdgiPass(IShaderProvider shaderCompiler, BindlessResourceRegistry bindlessRegistry)
	{
		_shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
	}

	public DdgiPassConfig BuildConfig(
		RenderGraphContext context,
		RenderGraphFrameResources resources,
		IGfxDevice device,
		IRenderer renderer,
		GpuDrawResources gpuDrawResources,
		IRayTracingSceneResources rayTracingSceneResources,
		SceneDrawData sceneData,
		bool historyValid)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(device);
		ArgumentNullException.ThrowIfNull(renderer);
		ArgumentNullException.ThrowIfNull(gpuDrawResources);
		ArgumentNullException.ThrowIfNull(rayTracingSceneResources);
		ArgumentNullException.ThrowIfNull(sceneData);

		if (device.SupportsRayTracing == false)
		{
			throw new NotSupportedException("Ray traced DDGI requires a ray-tracing capable graphics device.");
		}

		if (rayTracingSceneResources.TopLevelAccelerationStructure is null)
		{
			throw new InvalidOperationException("Ray traced DDGI requires a valid top-level acceleration structure.");
		}

		if (rayTracingSceneResources.InstanceIndexToInstanceHandleBuffer is null)
		{
			throw new InvalidOperationException("Ray traced DDGI requires RTAS instance sidecar resources.");
		}
		if (rayTracingSceneResources.InstanceIndexToTerrainRayTracingResolutionBuffer is null)
		{
			throw new InvalidOperationException("Ray traced DDGI requires RTAS terrain sidecar resources.");
		}

		EnsurePipelines(device);
		_bindlessRegistry.EnsureInitialized(device);
		if (_linearSampler.IsValid == false)
		{
			_linearSampler = _bindlessRegistry.GetSamplerHandle(new SamplerDescriptor(
				FilterMode.Bilinear,
				AddressMode.Clamp,
				AddressMode.Clamp,
				AddressMode.Clamp));
		}

		var config = resources.Config.DiffuseGlobalIllumination;
		ReadPendingFirstProbeRelocationDiagnostic(device);
		EnsureFirstProbeRelocationDiagnosticResources(device);
		var gridShape = DdgiUtilities.GetGridShape(config);
		var directLight = GetPrimaryDirectionalLight(sceneData);
		var frameIndex = _frameIndex++;
		var probeUpdateFrames = DdgiUtilities.GetProbeUpdateFrames(config);
		var probeUpdateFrameIndex = DdgiUtilities.GetProbeUpdateFrameIndex(frameIndex, probeUpdateFrames);
		var forceFullProbeUpdate = historyValid == false;
		var compactProbeClassificationDispatch =
			historyValid &&
			resources.DdgiScrollDelta.X == 0 &&
			resources.DdgiScrollDelta.Y == 0 &&
			resources.DdgiScrollDelta.Z == 0;
		var collectProbeClassificationStats = config.DebugProbeClassificationStats;
		var newlyExposedProbeCount = collectProbeClassificationStats
			? DdgiUtilities.GetNewlyExposedProbeCount(resources.DdgiScrollDelta, gridShape, historyValid)
			: 0;
		var activeProbeCount = collectProbeClassificationStats
			? DdgiUtilities.GetActiveProbeCount(
				gridShape,
				probeUpdateFrames,
				probeUpdateFrameIndex,
				forceFullProbeUpdate,
				resources.DdgiScrollDelta,
				historyValid)
			: 0;
		var raysPerProbe = Math.Clamp(config.RaysPerProbe, 1, DdgiUtilities.MaxRaySamplesPerProbe);
		var traceInvocationsPerProbe = DdgiUtilities.GetProbeTraceInvocationCount(raysPerProbe);
		var relocationProbeCount = config.ProbeRelocationEnabled ? activeProbeCount : 0;
		LastStats = new DdgiPassStats(
			probeUpdateFrames,
			activeProbeCount,
			gridShape.ProbeCount,
			raysPerProbe,
			activeProbeCount * traceInvocationsPerProbe,
			forceFullProbeUpdate,
			newlyExposedProbeCount,
			relocationProbeCount,
			relocationProbeCount * DdgiUtilities.RelocationRayCount,
			relocationProbeCount * DdgiUtilities.RelocationRayCount * DdgiUtilities.RelocationIterationCount);
		return new DdgiPassConfig
		{
			ClassifyPipeline = _classifyPipeline!,
			TracePipeline = _tracePipeline!,
			RelocationTracePipeline = _relocationTracePipeline!,
			RelocatePipeline = _relocatePipeline!,
			IrradianceIntegratePipeline = _irradianceIntegratePipeline!,
			VisibilityIntegratePipeline = _visibilityIntegratePipeline!,
			TopLevelAccelerationStructure = rayTracingSceneResources.TopLevelAccelerationStructure,
			TraceIrradianceHandle = _bindlessRegistry.RegisterRwTexture(context.GetTexture(resources.DdgiTraceIrradiance)),
			TraceVisibilityHandle = _bindlessRegistry.RegisterRwTexture(context.GetTexture(resources.DdgiTraceVisibility)),
			TraceIrradianceReadHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(resources.DdgiTraceIrradiance)),
			TraceVisibilityReadHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(resources.DdgiTraceVisibility)),
			IrradianceL0HistoryReadHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(resources.DdgiIrradianceL0HistoryRead)),
			IrradianceLyHistoryReadHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(resources.DdgiIrradianceLyHistoryRead)),
			IrradianceLzHistoryReadHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(resources.DdgiIrradianceLzHistoryRead)),
			IrradianceLxHistoryReadHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(resources.DdgiIrradianceLxHistoryRead)),
			VisibilityHistoryReadHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(resources.DdgiVisibilityHistoryRead)),
			IrradianceL0HistoryWriteHandle = _bindlessRegistry.RegisterRwTexture(context.GetTexture(resources.DdgiIrradianceL0HistoryWrite)),
			IrradianceLyHistoryWriteHandle = _bindlessRegistry.RegisterRwTexture(context.GetTexture(resources.DdgiIrradianceLyHistoryWrite)),
			IrradianceLzHistoryWriteHandle = _bindlessRegistry.RegisterRwTexture(context.GetTexture(resources.DdgiIrradianceLzHistoryWrite)),
			IrradianceLxHistoryWriteHandle = _bindlessRegistry.RegisterRwTexture(context.GetTexture(resources.DdgiIrradianceLxHistoryWrite)),
			VisibilityHistoryWriteHandle = _bindlessRegistry.RegisterRwTexture(context.GetTexture(resources.DdgiVisibilityHistoryWrite)),
			ProbeStateReadHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(resources.DdgiProbeStateRead)),
			ProbeStateCurrentHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(resources.DdgiProbeStateWrite)),
			ProbeStateWriteHandle = _bindlessRegistry.RegisterRwTexture(context.GetTexture(resources.DdgiProbeStateWrite)),
			ProbeActivityReadHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(resources.DdgiProbeActivity)),
			ProbeActivityWriteHandle = _bindlessRegistry.RegisterRwTexture(context.GetTexture(resources.DdgiProbeActivity)),
			ProbeRelocationDecisionHandle = _bindlessRegistry.RegisterRwTexture(context.GetTexture(resources.DdgiProbeRelocationDecision)),
			EnvironmentHandle = resources.SkyboxEnvironment.IsValid
				? _bindlessRegistry.GetTextureHandle(context.GetTexture(resources.SkyboxEnvironment))
				: DescriptorHandle.Invalid,
				SamplerHandle = _linearSampler,
				ErrorTextureHandle = _bindlessRegistry.ErrorTextureHandle,
				InstanceBuffer = gpuDrawResources.InstanceBuffer ?? throw new InvalidOperationException("GpuDraw instance buffer missing."),
				DrawCommandBuffer = gpuDrawResources.DrawCommandBuffer ?? throw new InvalidOperationException("GpuDraw draw-command buffer missing."),
				MaterialBuffer = gpuDrawResources.MaterialBuffer ?? throw new InvalidOperationException("GpuDraw material buffer missing."),
				MeshBuffer = gpuDrawResources.MeshBuffer ?? throw new InvalidOperationException("GpuDraw mesh buffer missing."),
				InstanceIndexToInstanceHandleBuffer = rayTracingSceneResources.InstanceIndexToInstanceHandleBuffer,
				InstanceIndexToTerrainRayTracingResolutionBuffer = rayTracingSceneResources.InstanceIndexToTerrainRayTracingResolutionBuffer,
				PackedMeshVertexBuffer = renderer.GetPackedMeshVertexBuffer() ?? throw new InvalidOperationException("Packed mesh vertex buffer missing."),
				PackedMeshIndexBuffer = renderer.GetPackedMeshIndexBuffer() ?? throw new InvalidOperationException("Packed mesh index buffer missing."),
				TerrainMaterialBuffer = gpuDrawResources.TerrainMaterialBuffer ?? throw new InvalidOperationException("GpuDraw terrain material buffer missing."),
				TerrainLayerBuffer = gpuDrawResources.TerrainLayerBuffer ?? throw new InvalidOperationException("GpuDraw terrain layer buffer missing."),
				IrradianceEstimatorBuffer = context.GetBuffer(resources.DdgiIrradianceEstimator),
				FirstProbeRelocationDiagnosticBuffer = _firstProbeRelocationDiagnosticBuffer!,
				FirstProbeRelocationReadbackBuffer = _firstProbeRelocationReadbackBuffer!,
			IrradianceAtlasSize = DdgiUtilities.GetAtlasSize(gridShape, DdgiUtilities.IrradianceTileInteriorSize),
			VisibilityAtlasSize = DdgiUtilities.GetAtlasSize(gridShape, DdgiUtilities.VisibilityTileInteriorSize),
			GridShape = gridShape,
			Origin = resources.DdgiRuntimeOrigin,
			StorageOffset = resources.DdgiStorageOffset,
			ScrollDelta = resources.DdgiScrollDelta,
			ProbeSpacing = Math.Max(config.ProbeSpacing, 0.001f),
			RaysPerProbe = raysPerProbe,
			ProbeUpdateFrames = probeUpdateFrames,
			ProbeUpdateFrameIndex = probeUpdateFrameIndex,
			ActiveProbeCount = activeProbeCount,
			ActiveDrawCommandUpperBound = gpuDrawResources.ActiveDrawCommandUpperBound,
			MaxRayDistance = DdgiUtilities.GetMaxRayDistance(config),
			NormalBias = Math.Max(config.NormalBias, 0.0f),
			ViewBias = Math.Max(config.ViewBias, 0.0f),
			IrradianceTemporalBlendSpeed = Math.Clamp(config.IrradianceTemporalBlendSpeed, 1.0f / 256.0f, 1.0f),
			Hysteresis = Math.Clamp(config.Hysteresis, 0.0f, 0.9999f),
				RecursiveBounceEnergy = DdgiUtilities.GetRecursiveBounceEnergy(config),
				ProbeRelocationEnabled = config.ProbeRelocationEnabled,
				DebugFirstProbeRelocationReadback = config.DebugFirstProbeRelocationReadback,
				DebugProbeRelocationReadbackIndex = Math.Clamp(
					config.DebugProbeRelocationReadbackIndex,
					0,
					Math.Max(gridShape.ProbeCount - 1, 0)),
				ProbeMinFrontfaceDistance = Math.Max(config.ProbeMinFrontfaceDistance, 0.0f),
			ProbeBackfaceThreshold = Math.Clamp(config.ProbeBackfaceThreshold, 0.0f, 1.0f),
			ProbeMaxRelocationDistance = DdgiUtilities.GetProbeMaxRelocationDistance(config),
			DirectLightDirection = directLight.Direction,
			DirectLightColorIntensity = directLight.ColorIntensity,
			FrameIndex = frameIndex,
			HistoryValid = historyValid,
			ForceFullProbeUpdate = forceFullProbeUpdate,
			CompactProbeClassificationDispatch = compactProbeClassificationDispatch,
			SidecarHitShadingAvailable = rayTracingSceneResources.LastStats.SidecarHitShadingAvailable
		};
	}

	public void RecordClassify(RenderGraphContext context, in DdgiPassConfig config)
	{
		var commandList = context.CommandList;
		commandList.BindPipeline(config.ClassifyPipeline);
		WriteBindlessConstants(_classifyBindlessWriter, commandList, config);
		WriteSettingsConstants(_classifySettingsWriter, commandList, config);
		commandList.SetComputeReadOnlyBuffer(2, config.DrawCommandBuffer);
		commandList.SetComputeReadOnlyBuffer(3, config.InstanceBuffer);
		var threadGroupSize = _classifyThreadGroupSize ?? throw new InvalidOperationException("DDGI classify threadgroup size was not initialized.");
		var classifyProbeCount = config.CompactProbeClassificationDispatch
			? DdgiUtilities.GetActiveProbeCount(
				config.GridShape.ProbeCount,
				config.ProbeUpdateFrames,
				config.ProbeUpdateFrameIndex,
				forceFullUpdate: false)
			: config.GridShape.ProbeCount;
		var (dispatchX, dispatchY, dispatchZ) = threadGroupSize.GetDispatchGroupCount(
			(uint)classifyProbeCount,
			1u);
		commandList.Dispatch(dispatchX, dispatchY, dispatchZ);
	}

	public void RecordTrace(RenderGraphContext context, in DdgiPassConfig config)
	{
		var commandList = context.CommandList;
		commandList.BindPipeline(config.TracePipeline);
		WriteBindlessConstants(_traceBindlessWriter, commandList, config);
		WriteSettingsConstants(_traceSettingsWriter, commandList, config);
		BindTraceResources(commandList, config);
		commandList.Dispatch((uint)config.GridShape.ProbeCount, 1, 1);
	}

	public void RecordRelocationTrace(RenderGraphContext context, in DdgiPassConfig config, int iteration)
	{
		var commandList = context.CommandList;
		commandList.BindPipeline(config.RelocationTracePipeline);
		WriteBindlessConstants(
			_relocationTraceBindlessWriter,
			commandList,
			config,
			config.ProbeStateReadHandle);
		WriteSettingsConstants(_relocationTraceSettingsWriter, commandList, config, iteration);
		BindTraceResources(commandList, config);
		commandList.Dispatch((uint)config.GridShape.ProbeCount, 1, 1);
	}

	private static void BindTraceResources(IGfxCommandList commandList, in DdgiPassConfig config)
	{
		commandList.SynchronizeAccelerationStructureBuildForComputeRead(config.TopLevelAccelerationStructure);
		commandList.SetComputeAccelerationStructure(3, config.TopLevelAccelerationStructure);
		commandList.SetComputeReadOnlyBuffer(4, config.InstanceBuffer);
		commandList.SetComputeReadOnlyBuffer(5, config.MaterialBuffer);
		commandList.SetComputeReadOnlyBuffer(6, config.InstanceIndexToInstanceHandleBuffer);
		commandList.SetComputeReadOnlyBuffer(7, config.MeshBuffer);
		commandList.SetComputeReadOnlyBuffer(8, config.PackedMeshVertexBuffer);
		commandList.SetComputeReadOnlyBuffer(9, config.PackedMeshIndexBuffer);
		commandList.SetComputeReadOnlyBuffer(10, config.TerrainMaterialBuffer);
		commandList.SetComputeReadOnlyBuffer(11, config.TerrainLayerBuffer);
		commandList.SetComputeReadOnlyBuffer(12, config.InstanceIndexToTerrainRayTracingResolutionBuffer);
	}

	public void RecordIrradianceIntegrate(RenderGraphContext context, in DdgiPassConfig config)
	{
		var commandList = context.CommandList;
		commandList.BindPipeline(config.IrradianceIntegratePipeline);
		WriteBindlessConstants(_irradianceIntegrateBindlessWriter, commandList, config);
		WriteSettingsConstants(_irradianceIntegrateSettingsWriter, commandList, config);
		commandList.SetComputeBuffer(2, config.IrradianceEstimatorBuffer);
		commandList.Dispatch((uint)config.GridShape.ProbeCount, 1, 1);
	}

	public void RecordVisibilityIntegrate(RenderGraphContext context, in DdgiPassConfig config)
	{
		var commandList = context.CommandList;
		commandList.BindPipeline(config.VisibilityIntegratePipeline);
		WriteBindlessConstants(_visibilityIntegrateBindlessWriter, commandList, config);
		WriteSettingsConstants(_visibilityIntegrateSettingsWriter, commandList, config);
		commandList.Dispatch((uint)config.GridShape.ProbeCount, 1, 1);
	}

	public void RecordRelocate(RenderGraphContext context, in DdgiPassConfig config, int iteration)
	{
		var commandList = context.CommandList;
		commandList.BindPipeline(config.RelocatePipeline);
		WriteBindlessConstants(
			_relocateBindlessWriter,
			commandList,
			config,
			config.ProbeStateReadHandle,
			config.ProbeStateWriteHandle);
		WriteSettingsConstants(_relocateSettingsWriter, commandList, config, iteration);
		commandList.SetComputeBuffer(2, config.FirstProbeRelocationDiagnosticBuffer);
		var threadGroupSize = _relocateThreadGroupSize ?? throw new InvalidOperationException("DDGI relocation threadgroup size was not initialized.");
		var (dispatchX, dispatchY, dispatchZ) = threadGroupSize.GetDispatchGroupCount((uint)config.GridShape.AtlasColumns, (uint)config.GridShape.AtlasRows);
		commandList.Dispatch(dispatchX, dispatchY, dispatchZ);
		if (config.DebugFirstProbeRelocationReadback &&
		    iteration == DdgiUtilities.RelocationIterationCount - 1)
		{
			commandList.CopyBuffer(
				config.FirstProbeRelocationDiagnosticBuffer,
				0,
				config.FirstProbeRelocationReadbackBuffer,
				0,
				(ulong)FirstProbeRelocationDiagnosticBufferSize);
			_firstProbeRelocationReadbackPending = true;
		}
	}

	private static void WriteBindlessConstants(ShaderPropertyWriter? writer, IGfxCommandList commandList, in DdgiPassConfig config)
	{
		WriteBindlessConstants(writer, commandList, config, config.ProbeStateReadHandle, config.ProbeStateWriteHandle);
	}

	private static void WriteBindlessConstants(
		ShaderPropertyWriter? writer,
		IGfxCommandList commandList,
		in DdgiPassConfig config,
		DescriptorHandle relocationStateReadHandle,
		DescriptorHandle? stateWriteHandle = null)
	{
		var bindlessWriter = writer ?? throw new InvalidOperationException("DDGI bindless reflection writer was not initialized.");
		bindlessWriter.Clear();
		bindlessWriter.SetUInt("traceIrradianceHandle", config.TraceIrradianceHandle.Value);
		bindlessWriter.SetUInt("traceVisibilityHandle", config.TraceVisibilityHandle.Value);
		bindlessWriter.SetUInt("traceIrradianceReadHandle", config.TraceIrradianceReadHandle.Value);
		bindlessWriter.SetUInt("traceVisibilityReadHandle", config.TraceVisibilityReadHandle.Value);
		bindlessWriter.SetUInt("irradianceL0HistoryReadHandle", config.IrradianceL0HistoryReadHandle.Value);
		bindlessWriter.SetUInt("irradianceLyHistoryReadHandle", config.IrradianceLyHistoryReadHandle.Value);
		bindlessWriter.SetUInt("irradianceLzHistoryReadHandle", config.IrradianceLzHistoryReadHandle.Value);
		bindlessWriter.SetUInt("irradianceLxHistoryReadHandle", config.IrradianceLxHistoryReadHandle.Value);
		bindlessWriter.SetUInt("visibilityHistoryReadHandle", config.VisibilityHistoryReadHandle.Value);
		bindlessWriter.SetUInt("irradianceL0HistoryWriteHandle", config.IrradianceL0HistoryWriteHandle.Value);
		bindlessWriter.SetUInt("irradianceLyHistoryWriteHandle", config.IrradianceLyHistoryWriteHandle.Value);
		bindlessWriter.SetUInt("irradianceLzHistoryWriteHandle", config.IrradianceLzHistoryWriteHandle.Value);
		bindlessWriter.SetUInt("irradianceLxHistoryWriteHandle", config.IrradianceLxHistoryWriteHandle.Value);
		bindlessWriter.SetUInt("visibilityHistoryWriteHandle", config.VisibilityHistoryWriteHandle.Value);
		bindlessWriter.SetUInt("probeStateReadHandle", config.ProbeStateReadHandle.Value);
		bindlessWriter.SetUInt("probeStateCurrentHandle", config.ProbeStateCurrentHandle.Value);
		bindlessWriter.SetUInt("probeRelocationStateReadHandle", relocationStateReadHandle.Value);
		bindlessWriter.SetUInt("probeStateWriteHandle", (stateWriteHandle ?? config.ProbeStateWriteHandle).Value);
		bindlessWriter.SetUInt("probeActivityReadHandle", config.ProbeActivityReadHandle.Value);
		bindlessWriter.SetUInt("probeActivityWriteHandle", config.ProbeActivityWriteHandle.Value);
		bindlessWriter.SetUInt("probeRelocationDecisionHandle", config.ProbeRelocationDecisionHandle.Value);
		bindlessWriter.SetUInt("environmentHandle", config.EnvironmentHandle.Value);
		bindlessWriter.SetUInt("samplerHandle", config.SamplerHandle.Value);
		bindlessWriter.SetUInt("errorTextureHandle", config.ErrorTextureHandle.Value);
		commandList.SetComputeConstants(bindlessWriter.RegisterIndex, bindlessWriter.AsBytes());
	}

	private static void WriteSettingsConstants(
		ShaderPropertyWriter? writer,
		IGfxCommandList commandList,
		in DdgiPassConfig config,
		int relocationIteration = 0)
	{
		var settingsWriter = writer ?? throw new InvalidOperationException("DDGI settings reflection writer was not initialized.");
		settingsWriter.Clear();
		settingsWriter.SetVector3("origin", config.Origin);
		settingsWriter.SetFloat("probeSpacing", config.ProbeSpacing);
		settingsWriter.SetInt("storageOffsetX", config.StorageOffset.X);
		settingsWriter.SetInt("storageOffsetY", config.StorageOffset.Y);
		settingsWriter.SetInt("storageOffsetZ", config.StorageOffset.Z);
		settingsWriter.SetInt("scrollDeltaX", config.ScrollDelta.X);
		settingsWriter.SetInt("scrollDeltaY", config.ScrollDelta.Y);
		settingsWriter.SetInt("scrollDeltaZ", config.ScrollDelta.Z);
		settingsWriter.SetUInt("probeCountX", (uint)config.GridShape.CountX);
		settingsWriter.SetUInt("probeCountY", (uint)config.GridShape.CountY);
		settingsWriter.SetUInt("probeCountZ", (uint)config.GridShape.CountZ);
		settingsWriter.SetUInt("probeCount", (uint)config.GridShape.ProbeCount);
		settingsWriter.SetUInt("atlasColumns", (uint)config.GridShape.AtlasColumns);
		settingsWriter.SetUInt("atlasRows", (uint)config.GridShape.AtlasRows);
		settingsWriter.SetUInt("irradianceAtlasWidth", (uint)config.IrradianceAtlasSize.X);
		settingsWriter.SetUInt("irradianceAtlasHeight", (uint)config.IrradianceAtlasSize.Y);
		settingsWriter.SetUInt("visibilityAtlasWidth", (uint)config.VisibilityAtlasSize.X);
		settingsWriter.SetUInt("visibilityAtlasHeight", (uint)config.VisibilityAtlasSize.Y);
		settingsWriter.SetUInt("irradianceTileInteriorSize", (uint)DdgiUtilities.IrradianceTileInteriorSize);
		settingsWriter.SetUInt("visibilityTileInteriorSize", (uint)DdgiUtilities.VisibilityTileInteriorSize);
		settingsWriter.SetUInt("tileBorderSize", (uint)DdgiUtilities.TileBorderSize);
		settingsWriter.SetUInt("raysPerProbe", (uint)config.RaysPerProbe);
		settingsWriter.SetUInt("probeUpdateFrames", (uint)config.ProbeUpdateFrames);
		settingsWriter.SetUInt("probeUpdateFrameIndex", (uint)config.ProbeUpdateFrameIndex);
		settingsWriter.SetUInt("forceFullProbeUpdate", config.ForceFullProbeUpdate ? 1u : 0u);
		settingsWriter.SetUInt("classifyCompactDispatch", config.CompactProbeClassificationDispatch ? 1u : 0u);
		settingsWriter.SetFloat("maxRayDistance", config.MaxRayDistance);
		settingsWriter.SetFloat("normalBias", config.NormalBias);
		settingsWriter.SetFloat("viewBias", config.ViewBias);
		settingsWriter.SetFloat("irradianceTemporalBlendSpeed", config.IrradianceTemporalBlendSpeed);
		settingsWriter.SetFloat("hysteresis", config.Hysteresis);
		settingsWriter.SetFloat("recursiveBounceEnergy", config.RecursiveBounceEnergy);
		settingsWriter.SetUInt("probeRelocationEnabled", config.ProbeRelocationEnabled ? 1u : 0u);
		settingsWriter.SetFloat("probeMinFrontfaceDistance", config.ProbeMinFrontfaceDistance);
		settingsWriter.SetFloat("probeBackfaceThreshold", config.ProbeBackfaceThreshold);
		settingsWriter.SetFloat("probeMaxRelocationDistance", config.ProbeMaxRelocationDistance);
		settingsWriter.SetVector3("directLightDirection", config.DirectLightDirection);
		settingsWriter.SetVector3("directLightColorIntensity", config.DirectLightColorIntensity);
		settingsWriter.SetUInt("frameIndex", config.FrameIndex);
		settingsWriter.SetUInt("historyValid", config.HistoryValid ? 1u : 0u);
		settingsWriter.SetUInt("sidecarHitShadingAvailable", config.SidecarHitShadingAvailable ? 1u : 0u);
		settingsWriter.SetUInt("activeDrawCommandUpperBound", config.ActiveDrawCommandUpperBound);
		settingsWriter.SetUInt(
			"debugFirstProbeRelocationReadback",
			config.DebugFirstProbeRelocationReadback ? 1u : 0u);
		settingsWriter.SetUInt(
			"debugProbeRelocationReadbackIndex",
			(uint)config.DebugProbeRelocationReadbackIndex);
		settingsWriter.SetUInt("relocationIteration", (uint)relocationIteration);
		commandList.SetComputeConstants(settingsWriter.RegisterIndex, settingsWriter.AsBytes());
	}

	private void EnsureFirstProbeRelocationDiagnosticResources(IGfxDevice device)
	{
		if (_firstProbeRelocationDiagnosticBuffer is not null &&
		    _firstProbeRelocationReadbackBuffer is not null &&
		    ReferenceEquals(_firstProbeRelocationDiagnosticDevice, device))
		{
			return;
		}

		(_firstProbeRelocationDiagnosticBuffer as IDisposable)?.Dispose();
		(_firstProbeRelocationReadbackBuffer as IDisposable)?.Dispose();
		_firstProbeRelocationDiagnosticBuffer = device.CreateBuffer(new BufferDescriptor(
			(ulong)FirstProbeRelocationDiagnosticBufferSize,
			BufferUsage.Structured,
			BufferFlags.AllowUnorderedAccess));
		_firstProbeRelocationReadbackBuffer = device.CreateBuffer(new BufferDescriptor(
			(ulong)FirstProbeRelocationDiagnosticBufferSize,
			BufferUsage.Staging));
		_firstProbeRelocationDiagnosticDevice = device;
		_firstProbeRelocationReadbackPending = false;
		LastFirstProbeRelocationDiagnostic = null;
		LastFirstProbeRelocationDiagnostics = Array.Empty<DdgiFirstProbeRelocationDiagnostic>();
	}

	private void ReadPendingFirstProbeRelocationDiagnostic(IGfxDevice device)
	{
		if (!_firstProbeRelocationReadbackPending ||
		    !ReferenceEquals(_firstProbeRelocationDiagnosticDevice, device) ||
		    _firstProbeRelocationReadbackBuffer is not IReadableGpuBuffer readableBuffer)
		{
			return;
		}

		device.WaitForIdle();
		var bytes = new byte[FirstProbeRelocationDiagnosticBufferSize];
		readableBuffer.Read(bytes);
		var values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes);
		var diagnostics = new DdgiFirstProbeRelocationDiagnostic[DdgiUtilities.RelocationIterationCount];
		for (var iteration = 0; iteration < diagnostics.Length; iteration++)
		{
			var offset = iteration * 36;
			var sourceState = (DdgiProbeState)(uint)values[offset + 33];
			diagnostics[iteration] = new DdgiFirstProbeRelocationDiagnostic(
				PreviousOffset: ReadVector3(values, offset),
				LogicalProbeIndex: (uint)values[offset + 3],
				BasePosition: ReadVector3(values, offset + 4),
				PhysicalProbeIndex: (uint)values[offset + 7],
				ClosestBackfaceDirection: ReadVector3(values, offset + 8),
				ClosestBackfaceDistance: values[offset + 11],
				FrontfacePush: ReadVector3(values, offset + 12),
				BackfaceHitCount: (uint)values[offset + 32],
				TargetOffset: ReadVector3(values, offset + 16),
				Decision: (DdgiProbeRelocationDecision)(uint)values[offset + 19],
				SmoothedOffset: ReadVector3(values, offset + 20),
				FrameIndex: (uint)values[offset + 23],
				TraceOrigin: ReadVector3(values, offset + 24),
				HasUsableHistory: sourceState == DdgiProbeState.Stable,
				StatePixelX: (uint)values[offset + 28],
				StatePixelY: (uint)values[offset + 29],
				Enabled: values[offset + 30] > 0.5f,
				HasFrontfacePush: ReadVector3(values, offset + 12).LengthSquared() > 0.0f,
				Iteration: (uint)iteration,
				ClosestFrontfaceDistance: values[offset + 15],
				SourceState: sourceState,
				ResultState: (DdgiProbeState)(uint)values[offset + 31]);
		}
		LastFirstProbeRelocationDiagnostics = diagnostics;
		LastFirstProbeRelocationDiagnostic = diagnostics[^1];
		_firstProbeRelocationReadbackPending = false;
	}

	private static Vector3 ReadVector3(ReadOnlySpan<float> values, int offset)
	{
		return new Vector3(values[offset], values[offset + 1], values[offset + 2]);
	}

	private static (Vector3 Direction, Vector3 ColorIntensity) GetPrimaryDirectionalLight(SceneDrawData sceneData)
	{
		for (var i = 0; i < sceneData.Lights.Count; i++)
		{
			var packet = sceneData.Lights[i];
			var light = packet.Light;
			if (light.Type != LightType.Directional)
			{
				continue;
			}

			var forward = Vector3.TransformNormal(Vector3.UnitZ, packet.Transform);
			if (forward == Vector3.Zero)
			{
				forward = new Vector3(0.0f, -1.0f, 0.0f);
			}

			forward = Vector3.Normalize(forward);
			var intensityScale = DirectionalLightUtility.GetIntensityScale(light, forward);
			return (forward, new Vector3(light.Color.R, light.Color.G, light.Color.B) * light.Intensity * intensityScale);
		}

		return (new Vector3(0.0f, -1.0f, 0.0f), Vector3.Zero);
	}

	private void EnsurePipelines(IGfxDevice device)
	{
		if (_classifyPipeline is not null &&
		    _tracePipeline is not null &&
		    _relocationTracePipeline is not null &&
		    _relocatePipeline is not null &&
		    _irradianceIntegratePipeline is not null &&
		    _visibilityIntegratePipeline is not null)
		{
			if (_compiledBackendKind.HasValue && _compiledBackendKind.Value != device.BackendKind)
			{
				throw new InvalidOperationException($"DdgiPass is already compiled for backend '{_compiledBackendKind.Value}', but was requested for '{device.BackendKind}'.");
			}

			return;
		}

		if (device.SupportsRayTracing == false)
		{
			throw new NotSupportedException("Ray traced DDGI requires a ray-tracing capable graphics device.");
		}

		var classify = _shaderCompiler.GetComputeShaderWithReflection(EngineShaderPrograms.DdgiClassify, "DdgiProbeClassifyCS", device.BackendKind);
		var trace = _shaderCompiler.GetComputeShaderWithReflection(EngineShaderPrograms.DdgiTrace, "DdgiProbeTraceCS", device.BackendKind);
		var relocationTrace = _shaderCompiler.GetComputeShaderWithReflection(EngineShaderPrograms.DdgiTrace, "DdgiRelocationTraceCS", device.BackendKind);
		var relocate = _shaderCompiler.GetComputeShaderWithReflection(EngineShaderPrograms.DdgiRelocate, "DdgiRelocationSolveCS", device.BackendKind);
		var irradianceIntegrate = _shaderCompiler.GetComputeShaderWithReflection(EngineShaderPrograms.DdgiIrradianceIntegrate, "DdgiIrradianceIntegrateCS", device.BackendKind);
		var visibilityIntegrate = _shaderCompiler.GetComputeShaderWithReflection(EngineShaderPrograms.DdgiIntegrate, "DdgiVisibilityIntegrateCS", device.BackendKind);
		_classifyShader = classify.Bytecode;
		_traceShader = trace.Bytecode;
		_relocationTraceShader = relocationTrace.Bytecode;
		_relocateShader = relocate.Bytecode;
		_irradianceIntegrateShader = irradianceIntegrate.Bytecode;
		_visibilityIntegrateShader = visibilityIntegrate.Bytecode;
		_classifyThreadGroupSize = classify.ThreadGroupSize;
		_traceThreadGroupSize = trace.ThreadGroupSize;
		_relocationTraceThreadGroupSize = relocationTrace.ThreadGroupSize;
		_relocateThreadGroupSize = relocate.ThreadGroupSize;
		_irradianceIntegrateThreadGroupSize = irradianceIntegrate.ThreadGroupSize;
		_visibilityIntegrateThreadGroupSize = visibilityIntegrate.ThreadGroupSize;
		_classifyBindlessWriter = new ShaderPropertyWriter(classify.ReflectionLayout.GetConstantBuffer("BindlessHandles"));
		_classifySettingsWriter = new ShaderPropertyWriter(classify.ReflectionLayout.GetConstantBuffer("DdgiSettings"));
		_traceBindlessWriter = new ShaderPropertyWriter(trace.ReflectionLayout.GetConstantBuffer("BindlessHandles"));
		_traceSettingsWriter = new ShaderPropertyWriter(trace.ReflectionLayout.GetConstantBuffer("DdgiSettings"));
		_relocationTraceBindlessWriter = new ShaderPropertyWriter(relocationTrace.ReflectionLayout.GetConstantBuffer("BindlessHandles"));
		_relocationTraceSettingsWriter = new ShaderPropertyWriter(relocationTrace.ReflectionLayout.GetConstantBuffer("DdgiSettings"));
		_relocateBindlessWriter = new ShaderPropertyWriter(relocate.ReflectionLayout.GetConstantBuffer("BindlessHandles"));
		_relocateSettingsWriter = new ShaderPropertyWriter(relocate.ReflectionLayout.GetConstantBuffer("DdgiSettings"));
		_irradianceIntegrateBindlessWriter = new ShaderPropertyWriter(irradianceIntegrate.ReflectionLayout.GetConstantBuffer("BindlessHandles"));
		_irradianceIntegrateSettingsWriter = new ShaderPropertyWriter(irradianceIntegrate.ReflectionLayout.GetConstantBuffer("DdgiSettings"));
		_visibilityIntegrateBindlessWriter = new ShaderPropertyWriter(visibilityIntegrate.ReflectionLayout.GetConstantBuffer("BindlessHandles"));
		_visibilityIntegrateSettingsWriter = new ShaderPropertyWriter(visibilityIntegrate.ReflectionLayout.GetConstantBuffer("DdgiSettings"));
		_classifyPipeline = CreatePipeline(
			device,
			"ddgi_classify.compute.slang",
			_classifyShader,
			_classifyThreadGroupSize,
			"DdgiProbeClassifyCS");
		_tracePipeline = CreatePipeline(
			device,
			"ddgi_trace.compute.slang",
			_traceShader,
			_traceThreadGroupSize,
			"DdgiProbeTraceCS");
		_relocationTracePipeline = CreatePipeline(
			device,
			"ddgi_trace.compute.slang:relocation",
			_relocationTraceShader,
			_relocationTraceThreadGroupSize,
			"DdgiRelocationTraceCS");
		_relocatePipeline = CreatePipeline(
			device,
			"ddgi_relocate.compute.slang",
			_relocateShader,
			_relocateThreadGroupSize,
			"DdgiRelocationSolveCS");
		_irradianceIntegratePipeline = CreatePipeline(
			device,
			"ddgi_irradiance_integrate.compute.slang",
			_irradianceIntegrateShader,
			_irradianceIntegrateThreadGroupSize,
			"DdgiIrradianceIntegrateCS");
		_visibilityIntegratePipeline = CreatePipeline(
			device,
			"ddgi_integrate.compute.slang",
			_visibilityIntegrateShader,
			_visibilityIntegrateThreadGroupSize,
			"DdgiVisibilityIntegrateCS");
		_compiledBackendKind = device.BackendKind;
	}

	private static IGfxPipeline CreatePipeline(
		IGfxDevice device,
		string shaderVariant,
		ReadOnlyMemory<byte> shader,
		ComputeThreadGroupSize? threadGroupSize,
		string entryPoint)
	{
		var pipelineKey = new PipelineKey(
			PassKind.Compute,
			vertexEntryPoint: null,
			pixelEntryPoint: null,
			computeEntryPoint: entryPoint,
			renderTargets: new RenderTargetFormats(Array.Empty<TextureFormat>()),
			depthStencil: new DepthStencilFormat(TextureFormat.Unknown),
			renderState: default,
			shaderVariant: shaderVariant);
		return device.GetOrCreatePipeline(
			pipelineKey,
			new ShaderBytecodeSet(compute: shader, computeThreadGroupSize: threadGroupSize));
	}
}

public readonly record struct DdgiPassStats(
	int ProbeUpdateFrames,
	int ActiveProbeCount,
	int TotalProbeCount,
	int RaysPerActiveProbe,
	int EstimatedProbeRaysThisFrame,
	bool ForceFullProbeUpdate,
	int NewlyExposedProbeCount,
	int RelocationProbeCount,
	int EstimatedRelocationRaysThisFrame,
	int MaximumRelocationRaysThisFrame);

public enum DdgiProbeRelocationDecision : uint
{
	None,
	BackfaceEscape,
	FrontfaceSeparation,
	ReturnToLattice
}

public readonly record struct DdgiFirstProbeRelocationDiagnostic(
	Vector3 PreviousOffset,
	uint LogicalProbeIndex,
	Vector3 BasePosition,
	uint PhysicalProbeIndex,
	Vector3 ClosestBackfaceDirection,
	float ClosestBackfaceDistance,
	Vector3 FrontfacePush,
	uint BackfaceHitCount,
	Vector3 TargetOffset,
	DdgiProbeRelocationDecision Decision,
	Vector3 SmoothedOffset,
	uint FrameIndex,
	Vector3 TraceOrigin,
	bool HasUsableHistory,
	uint StatePixelX,
	uint StatePixelY,
	bool Enabled,
	bool HasFrontfacePush,
	uint Iteration,
	float ClosestFrontfaceDistance,
	DdgiProbeState SourceState,
	DdgiProbeState ResultState);

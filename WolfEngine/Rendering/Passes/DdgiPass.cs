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

	private readonly ShaderSource _shaderSource;
	private readonly ComputeKernel _classify;
	private readonly ComputeKernel _classifyClear;
	private readonly ComputeKernel _classifyScatter;
	private readonly ComputeKernel _trace;
	private readonly ComputeKernel _relocationTrace;
	private readonly ComputeKernel _relocate;
	private readonly ComputeKernel _irradianceIntegrate;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private readonly Dictionary<int, ComputeKernel> _visibilityIntegrateKernels = new();
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
	private readonly IRenderer _renderer;
	private readonly GpuDrawResources _gpuDrawResources;
	private readonly RayTracingScene _rayTracingScene;
	private readonly Action<RenderGraphContext> _classifyExecute;
	private readonly Action<RenderGraphContext> _relocationTraceExecute;
	private readonly Action<RenderGraphContext> _relocateExecute;
	private readonly Action<RenderGraphContext> _traceExecute;
	private readonly Action<RenderGraphContext> _irradianceIntegrateExecute;
	private readonly Action<RenderGraphContext> _visibilityIntegrateExecute;

	internal DdgiPass(
		ShaderSource shaderSource,
		BindlessResourceRegistry bindlessRegistry,
		IRenderer renderer,
		GpuDrawResources gpuDrawResources,
		RayTracingScene rayTracingScene)
	{
		_shaderSource = shaderSource ?? throw new ArgumentNullException(nameof(shaderSource));
		_classify = new ComputeKernel(shaderSource, EngineShaderPrograms.DdgiClassify, "DdgiProbeClassifyCS");
		_classifyClear = new ComputeKernel(shaderSource, EngineShaderPrograms.DdgiClassify, "DdgiProbeActivityClearCS");
		_classifyScatter = new ComputeKernel(shaderSource, EngineShaderPrograms.DdgiClassify, "DdgiProbeActivityScatterCS");
		_trace = new ComputeKernel(shaderSource, EngineShaderPrograms.DdgiTrace, "DdgiProbeTraceCS");
		_relocationTrace = new ComputeKernel(shaderSource, EngineShaderPrograms.DdgiTrace, "DdgiRelocationTraceCS");
		_relocate = new ComputeKernel(shaderSource, EngineShaderPrograms.DdgiRelocate, "DdgiRelocationSolveCS");
		_irradianceIntegrate = new ComputeKernel(shaderSource, EngineShaderPrograms.DdgiIrradianceIntegrate, "DdgiIrradianceIntegrateCS");
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
		_renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
		_gpuDrawResources = gpuDrawResources ?? throw new ArgumentNullException(nameof(gpuDrawResources));
		_rayTracingScene = rayTracingScene ?? throw new ArgumentNullException(nameof(rayTracingScene));
		_classifyExecute = ExecuteClassify;
		_relocationTraceExecute = context => RecordRelocationTrace(context, in GetBuiltConfig(context, "relocation trace"), 0);
		_relocateExecute = context => RecordRelocate(context, in GetBuiltConfig(context, "relocation solve"), 0);
		_traceExecute = context => RecordTrace(context, in GetBuiltConfig(context, "trace"));
		_irradianceIntegrateExecute = context => RecordIrradianceIntegrate(context, in GetBuiltConfig(context, "irradiance integrate"));
		_visibilityIntegrateExecute = context => RecordVisibilityIntegrate(context, in GetBuiltConfig(context, "visibility integrate"));
	}

	/// <summary>
	/// The view's config for this frame. Classify builds it, and every later probe stage binds the resources it
	/// resolved.
	/// </summary>
	private sealed class ViewConfig
	{
		public DdgiPassConfig Config;
		public bool Valid;

		/// <summary>Frames completed since this view's probe volume was last seeded.</summary>
		public int FramesSinceSeed;
	}

	/// <summary>
	/// Adds the probe update's nodes: classify, the optional relocation trace, relocation solve, the probe trace,
	/// and the irradiance and visibility integration into this frame's history.
	/// </summary>
	internal void AddNodes(RenderGraph graph, RenderViewState view)
	{
		var resources = view.FrameResources;
		view.GetPassState<ViewConfig>().Valid = false;

		// Classify builds the config every later DDGI stage reuses, so it resolves, and declares, every
		// resource those stages bind: a read where the config holds a read handle, a write where it
		// registers a read-write one.
		var classifyBuilder = graph.AddPass("DDGI Probe Classify", PassKind.Compute)
			.ReadTexture(resources.DdgiIrradianceL0HistoryRead, ResourceState.ShaderResource)
			.ReadTexture(resources.DdgiIrradianceLyHistoryRead, ResourceState.ShaderResource)
			.ReadTexture(resources.DdgiIrradianceLzHistoryRead, ResourceState.ShaderResource)
			.ReadTexture(resources.DdgiIrradianceLxHistoryRead, ResourceState.ShaderResource)
			.ReadTexture(resources.DdgiVisibilityHistoryRead, ResourceState.ShaderResource)
			.ReadTexture(resources.DdgiProbeStateRead, ResourceState.ShaderResource)
			.WriteTexture(resources.DdgiTraceIrradiance, ResourceState.UnorderedAccess)
			.WriteTexture(resources.DdgiTraceVisibility, ResourceState.UnorderedAccess)
			.WriteTexture(resources.DdgiIrradianceL0HistoryWrite, ResourceState.UnorderedAccess)
			.WriteTexture(resources.DdgiIrradianceLyHistoryWrite, ResourceState.UnorderedAccess)
			.WriteTexture(resources.DdgiIrradianceLzHistoryWrite, ResourceState.UnorderedAccess)
			.WriteTexture(resources.DdgiIrradianceLxHistoryWrite, ResourceState.UnorderedAccess)
			.WriteTexture(resources.DdgiVisibilityHistoryWrite, ResourceState.UnorderedAccess)
			.WriteTexture(resources.DdgiProbeStateWrite, ResourceState.UnorderedAccess)
			.WriteTexture(resources.DdgiProbeRelocationDecision, ResourceState.UnorderedAccess)
			.WriteBuffer(resources.DdgiIrradianceEstimator, ResourceState.UnorderedAccess)
			.WriteTexture(resources.DdgiProbeActivityMarks, ResourceState.UnorderedAccess)
			.WriteTexture(resources.DdgiProbeActivity, ResourceState.UnorderedAccess);
		if (resources.SkyEnvironment.ViewEnvironment.IsValid)
		{
			classifyBuilder.ReadTexture(resources.SkyEnvironment.ViewEnvironment, ResourceState.ShaderResource);
		}
		classifyBuilder.SetExecute(_classifyExecute);

		if (DdgiUtilities.IsRelocationTraceEnabled(resources.Config))
		{
			graph.AddPass("DDGI Relocation Trace", PassKind.Compute)
				.ReadTexture(resources.DdgiProbeActivity, ResourceState.ShaderResource)
				.ReadTexture(resources.DdgiProbeStateRead, ResourceState.ShaderResource)
				.WriteTexture(resources.DdgiTraceVisibility, ResourceState.UnorderedAccess)
				.SetExecute(_relocationTraceExecute);
		}

		var relocationSolveBuilder = graph.AddPass("DDGI Relocation Solve", PassKind.Compute)
			.ReadTexture(resources.DdgiProbeActivity, ResourceState.ShaderResource)
			.ReadTexture(resources.DdgiProbeStateRead, ResourceState.ShaderResource)
			.WriteTexture(resources.DdgiProbeStateWrite, ResourceState.UnorderedAccess)
			.WriteTexture(resources.DdgiProbeRelocationDecision, ResourceState.UnorderedAccess);
		if (DdgiUtilities.IsRelocationTraceEnabled(resources.Config))
		{
			relocationSolveBuilder.ReadTexture(
				resources.DdgiTraceVisibility,
				ResourceState.ShaderResource);
		}
		relocationSolveBuilder.SetExecute(_relocateExecute);

		var ddgiTraceBuilder = graph.AddPass("DDGI Probe Trace", PassKind.Compute)
			.ReadTexture(resources.DdgiProbeActivity, ResourceState.ShaderResource)
			.ReadTexture(resources.DdgiProbeStateRead, ResourceState.ShaderResource)
			.ReadTexture(resources.DdgiProbeStateWrite, ResourceState.ShaderResource)
			.ReadTexture(resources.DdgiIrradianceL0HistoryRead, ResourceState.ShaderResource)
			.ReadTexture(resources.DdgiIrradianceLyHistoryRead, ResourceState.ShaderResource)
			.ReadTexture(resources.DdgiIrradianceLzHistoryRead, ResourceState.ShaderResource)
			.ReadTexture(resources.DdgiIrradianceLxHistoryRead, ResourceState.ShaderResource)
			.ReadTexture(resources.DdgiVisibilityHistoryRead, ResourceState.ShaderResource)
			.WriteTexture(resources.DdgiTraceIrradiance, ResourceState.UnorderedAccess)
			.WriteTexture(resources.DdgiTraceVisibility, ResourceState.UnorderedAccess);
		if (resources.SkyEnvironment.ViewEnvironment.IsValid)
		{
			ddgiTraceBuilder.ReadTexture(resources.SkyEnvironment.ViewEnvironment, ResourceState.ShaderResource);
		}
		ddgiTraceBuilder.SetExecute(_traceExecute);

		graph.AddPass("DDGI Irradiance Integrate", PassKind.Compute)
			.ReadTexture(resources.DdgiProbeActivity, ResourceState.ShaderResource)
			.ReadTexture(resources.DdgiProbeStateRead, ResourceState.ShaderResource)
			.ReadTexture(resources.DdgiProbeStateWrite, ResourceState.ShaderResource)
			.ReadTexture(resources.DdgiTraceIrradiance, ResourceState.ShaderResource)
			.ReadTexture(resources.DdgiIrradianceL0HistoryRead, ResourceState.ShaderResource)
			.ReadTexture(resources.DdgiIrradianceLyHistoryRead, ResourceState.ShaderResource)
			.ReadTexture(resources.DdgiIrradianceLzHistoryRead, ResourceState.ShaderResource)
			.ReadTexture(resources.DdgiIrradianceLxHistoryRead, ResourceState.ShaderResource)
			.WriteTexture(resources.DdgiIrradianceL0HistoryWrite, ResourceState.UnorderedAccess)
			.WriteTexture(resources.DdgiIrradianceLyHistoryWrite, ResourceState.UnorderedAccess)
			.WriteTexture(resources.DdgiIrradianceLzHistoryWrite, ResourceState.UnorderedAccess)
			.WriteTexture(resources.DdgiIrradianceLxHistoryWrite, ResourceState.UnorderedAccess)
			.WriteBuffer(resources.DdgiIrradianceEstimator, ResourceState.UnorderedAccess)
			.SetExecute(_irradianceIntegrateExecute);

		graph.AddPass("DDGI Visibility Integrate", PassKind.Compute)
			.ReadTexture(resources.DdgiProbeActivity, ResourceState.ShaderResource)
			.ReadTexture(resources.DdgiProbeStateRead, ResourceState.ShaderResource)
			.ReadTexture(resources.DdgiProbeStateWrite, ResourceState.ShaderResource)
			.ReadTexture(resources.DdgiTraceVisibility, ResourceState.ShaderResource)
			.ReadTexture(resources.DdgiVisibilityHistoryRead, ResourceState.ShaderResource)
			.WriteTexture(resources.DdgiVisibilityHistoryWrite, ResourceState.UnorderedAccess)
			.SetExecute(_visibilityIntegrateExecute);
	}

	private void ExecuteClassify(RenderGraphContext context)
	{
		var view = context.ViewState;
		var state = view.GetPassState<ViewConfig>();
		var rayTracingSceneResources = _rayTracingScene.Resources;
		var historyValid = DdgiUtilities.CanBlendProbeHistory(
			view.DdgiHistory.IsValid,
			state.FramesSinceSeed,
			DdgiUtilities.GetProbeUpdateFrames(view.FrameResources.Config.DiffuseGlobalIllumination),
			rayTracingSceneResources.LastStats.TopLevelRebuildReason);
		state.FramesSinceSeed = historyValid
			? state.FramesSinceSeed + 1
			: 0;

		state.Config = BuildConfig(
			context,
			view.FrameResources,
			context.Device,
			_renderer,
			_gpuDrawResources,
			rayTracingSceneResources,
			context.SceneData,
			historyValid);
		state.Valid = true;
		RecordClassify(context, in state.Config);
	}

	private static ref DdgiPassConfig GetBuiltConfig(RenderGraphContext context, string stage)
	{
		var state = context.ViewState.GetPassState<ViewConfig>();
		if (state.Valid == false)
		{
			throw new InvalidOperationException($"DDGI {stage} executed before DDGI probe classification config was built.");
		}

		return ref state.Config;
	}

	private DdgiPassConfig BuildConfig(
		RenderGraphContext context,
		RenderViewResources resources,
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

		if (device.SupportsRayTracing == false)
		{
			throw new NotSupportedException("Ray traced DDGI requires a ray-tracing capable graphics device.");
		}

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
		var visibilityTileInteriorSize = DdgiUtilities.GetVisibilityTileInteriorSize(config);
		var visibilityIntegratePipeline = GetVisibilityIntegrateKernel(visibilityTileInteriorSize).GetPipeline(device);
		var traceInvocationsPerProbe = DdgiUtilities.GetProbeTraceInvocationCount(raysPerProbe, visibilityTileInteriorSize);
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
			ClassifyPipeline = _classify.GetPipeline(device),
			ClassifyClearPipeline = _classifyClear.GetPipeline(device),
			ClassifyScatterPipeline = _classifyScatter.GetPipeline(device),
			TracePipeline = _trace.GetPipeline(device),
			RelocationTracePipeline = _relocationTrace.GetPipeline(device),
			RelocatePipeline = _relocate.GetPipeline(device),
			IrradianceIntegratePipeline = _irradianceIntegrate.GetPipeline(device),
			VisibilityIntegratePipeline = visibilityIntegratePipeline,
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
			ProbeActivityMarksHandle = _bindlessRegistry.RegisterRwTexture(context.GetTexture(resources.DdgiProbeActivityMarks)),
			ProbeActivityMarksTexture = context.GetTexture(resources.DdgiProbeActivityMarks),
			ProbeRelocationDecisionHandle = _bindlessRegistry.RegisterRwTexture(context.GetTexture(resources.DdgiProbeRelocationDecision)),
			SkyEnvironment = resources.SkyEnvironment.Resolve(context, _bindlessRegistry),
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
			VisibilityAtlasSize = DdgiUtilities.GetAtlasSize(gridShape, visibilityTileInteriorSize),
			VisibilityTileInteriorSize = visibilityTileInteriorSize,
			GridShape = gridShape,
			Origin = resources.DdgiRuntimeOrigin,
			StorageOffset = resources.DdgiStorageOffset,
			ScrollDelta = resources.DdgiScrollDelta,
			ProbeSpacing = Math.Max(config.ProbeSpacing, 0.001f),
			RaysPerProbe = raysPerProbe,
			ProbeUpdateFrames = probeUpdateFrames,
			ProbeUpdateFrameIndex = probeUpdateFrameIndex,
			ActiveProbeCount = activeProbeCount,
			ActiveInstanceUpperBound = gpuDrawResources.ActiveInstanceUpperBound,
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
		commandList.BindPipeline(config.ClassifyClearPipeline);
		WriteBindlessConstants(_classifyClear.Writer("BindlessHandles"), commandList, config);
		WriteSettingsConstants(_classifyClear.Writer("DdgiSettings"), commandList, config);
		var clearThreadGroupSize = _classifyClear.ThreadGroupSize;
		var (clearX, clearY, clearZ) = clearThreadGroupSize.GetDispatchGroupCount((uint)config.GridShape.ProbeCount, 1u);
		commandList.Dispatch(clearX, clearY, clearZ);
		commandList.SynchronizeUnorderedAccess(config.ProbeActivityMarksTexture);

		commandList.BindPipeline(config.ClassifyScatterPipeline);
		WriteBindlessConstants(_classifyScatter.Writer("BindlessHandles"), commandList, config);
		WriteSettingsConstants(_classifyScatter.Writer("DdgiSettings"), commandList, config);
		commandList.SetComputeReadOnlyBuffer(2, config.DrawCommandBuffer);
		commandList.SetComputeReadOnlyBuffer(3, config.InstanceBuffer);
		var (scatterX, scatterY, scatterZ) =
			DdgiUtilities.GetActivityScatterDispatchGroupCount(config.ActiveInstanceUpperBound);
		commandList.Dispatch(scatterX, scatterY, scatterZ);
		commandList.SynchronizeUnorderedAccess(config.ProbeActivityMarksTexture);

		commandList.BindPipeline(config.ClassifyPipeline);
		WriteBindlessConstants(_classify.Writer("BindlessHandles"), commandList, config);
		WriteSettingsConstants(_classify.Writer("DdgiSettings"), commandList, config);
		commandList.SetComputeReadOnlyBuffer(2, config.DrawCommandBuffer);
		commandList.SetComputeReadOnlyBuffer(3, config.InstanceBuffer);
		var threadGroupSize = _classify.ThreadGroupSize;
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
		WriteBindlessConstants(_trace.Writer("BindlessHandles"), commandList, config);
		WriteSettingsConstants(_trace.Writer("DdgiSettings"), commandList, config);
		BindTraceResources(commandList, config);
		commandList.Dispatch((uint)config.GridShape.ProbeCount, 1, 1);
	}

	public void RecordRelocationTrace(RenderGraphContext context, in DdgiPassConfig config, int iteration)
	{
		var commandList = context.CommandList;
		commandList.BindPipeline(config.RelocationTracePipeline);
		WriteBindlessConstants(
			_relocationTrace.Writer("BindlessHandles"),
			commandList,
			config,
			config.ProbeStateReadHandle);
		WriteSettingsConstants(_relocationTrace.Writer("DdgiSettings"), commandList, config, iteration);
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
		WriteBindlessConstants(_irradianceIntegrate.Writer("BindlessHandles"), commandList, config);
		WriteSettingsConstants(_irradianceIntegrate.Writer("DdgiSettings"), commandList, config);
		commandList.SetComputeBuffer(2, config.IrradianceEstimatorBuffer);
		commandList.Dispatch((uint)config.GridShape.ProbeCount, 1, 1);
	}

	public void RecordVisibilityIntegrate(RenderGraphContext context, in DdgiPassConfig config)
	{
		var commandList = context.CommandList;
		var kernel = GetVisibilityIntegrateKernel(config.VisibilityTileInteriorSize);
		commandList.BindPipeline(config.VisibilityIntegratePipeline);
		WriteBindlessConstants(kernel.Writer("BindlessHandles"), commandList, config);
		WriteSettingsConstants(kernel.Writer("DdgiSettings"), commandList, config);
		commandList.Dispatch((uint)config.GridShape.ProbeCount, 1, 1);
	}

	public void RecordRelocate(RenderGraphContext context, in DdgiPassConfig config, int iteration)
	{
		var commandList = context.CommandList;
		commandList.BindPipeline(config.RelocatePipeline);
		WriteBindlessConstants(
			_relocate.Writer("BindlessHandles"),
			commandList,
			config,
			config.ProbeStateReadHandle,
			config.ProbeStateWriteHandle);
		WriteSettingsConstants(_relocate.Writer("DdgiSettings"), commandList, config, iteration);
		commandList.SetComputeBuffer(2, config.FirstProbeRelocationDiagnosticBuffer);
		var threadGroupSize = _relocate.ThreadGroupSize;
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
		bindlessWriter.SetUInt("probeActivityMarksHandle", config.ProbeActivityMarksHandle.Value);
		bindlessWriter.SetUInt("probeRelocationDecisionHandle", config.ProbeRelocationDecisionHandle.Value);
		config.SkyEnvironment.Write(bindlessWriter);
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
		settingsWriter.SetUInt("visibilityTileInteriorSize", (uint)config.VisibilityTileInteriorSize);
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
		settingsWriter.SetUInt("activeInstanceUpperBound", config.ActiveInstanceUpperBound);
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

	internal static (Vector3 Direction, Vector3 ColorIntensity) GetPrimaryDirectionalLight(SceneDrawData sceneData)
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
			var colorScale = sceneData.SunLightAttenuation.GetColorScale(light, forward);
			return (forward, new Vector3(light.Color.R, light.Color.G, light.Color.B) * colorScale * light.Intensity);
		}

		return (new Vector3(0.0f, -1.0f, 0.0f), Vector3.Zero);
	}

	/// <summary>
	/// The visibility integrate kernel for a tile size. The tile size is compiled in, which fixes the kernel's thread
	/// group and groupshared arrays, so each tile size is its own kernel, created the first time a config asks for it.
	/// </summary>
	private ComputeKernel GetVisibilityIntegrateKernel(int tileInteriorSize)
	{
		if (_visibilityIntegrateKernels.TryGetValue(tileInteriorSize, out var kernel))
		{
			return kernel;
		}

		var define = DdgiUtilities.GetVisibilityIntegrateDefine(tileInteriorSize);
		kernel = define is null
			? new ComputeKernel(_shaderSource, EngineShaderPrograms.DdgiIntegrate, "DdgiVisibilityIntegrateCS")
			: new ComputeKernel(_shaderSource, EngineShaderPrograms.DdgiIntegrate, "DdgiVisibilityIntegrateCS", define);
		_visibilityIntegrateKernels[tileInteriorSize] = kernel;

		return kernel;
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

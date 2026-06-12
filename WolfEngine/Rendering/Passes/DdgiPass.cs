using System;
using System.Numerics;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

public sealed class DdgiPass
{
	private readonly IShaderCompiler _shaderCompiler;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private IGfxPipeline? _tracePipeline;
	private IGfxPipeline? _relocatePipeline;
	private IGfxPipeline? _irradianceIntegratePipeline;
	private IGfxPipeline? _visibilityIntegratePipeline;
	private IGfxPipeline? _borderUpdatePipeline;
	private ReadOnlyMemory<byte> _traceShader;
	private ReadOnlyMemory<byte> _relocateShader;
	private ReadOnlyMemory<byte> _irradianceIntegrateShader;
	private ReadOnlyMemory<byte> _visibilityIntegrateShader;
	private ReadOnlyMemory<byte> _borderUpdateShader;
	private ComputeThreadGroupSize? _traceThreadGroupSize;
	private ComputeThreadGroupSize? _relocateThreadGroupSize;
	private ComputeThreadGroupSize? _irradianceIntegrateThreadGroupSize;
	private ComputeThreadGroupSize? _visibilityIntegrateThreadGroupSize;
	private ComputeThreadGroupSize? _borderUpdateThreadGroupSize;
	private GraphicsBackendKind? _compiledBackendKind;
	private ShaderPropertyWriter? _traceBindlessWriter;
	private ShaderPropertyWriter? _traceSettingsWriter;
	private ShaderPropertyWriter? _relocateBindlessWriter;
	private ShaderPropertyWriter? _relocateSettingsWriter;
	private ShaderPropertyWriter? _irradianceIntegrateBindlessWriter;
	private ShaderPropertyWriter? _irradianceIntegrateSettingsWriter;
	private ShaderPropertyWriter? _visibilityIntegrateBindlessWriter;
	private ShaderPropertyWriter? _visibilityIntegrateSettingsWriter;
	private ShaderPropertyWriter? _borderBindlessWriter;
	private ShaderPropertyWriter? _borderSettingsWriter;
	private DescriptorHandle _linearSampler = DescriptorHandle.Invalid;
	private uint _frameIndex;

	public DdgiPassStats LastStats { get; private set; }

	public DdgiPass(IShaderCompiler shaderCompiler, BindlessResourceRegistry bindlessRegistry)
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

		if (device.BackendKind != GraphicsBackendKind.Metal)
		{
			throw new NotImplementedException("Ray traced DDGI is currently implemented for Metal only.");
		}

		if (rayTracingSceneResources.TopLevelAccelerationStructure is null)
		{
			throw new InvalidOperationException("Ray traced DDGI requires a valid top-level acceleration structure.");
		}

		if (rayTracingSceneResources.InstanceIndexToInstanceHandleBuffer is null)
		{
			throw new InvalidOperationException("Ray traced DDGI requires RTAS instance sidecar resources.");
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
		var gridShape = DdgiUtilities.GetGridShape(config);
		var directLight = GetPrimaryDirectionalLight(sceneData);
		var frameIndex = _frameIndex++;
		var probeUpdateFrames = DdgiUtilities.GetProbeUpdateFrames(config);
		var probeUpdateFrameIndex = DdgiUtilities.GetProbeUpdateFrameIndex(frameIndex, probeUpdateFrames);
		var forceFullProbeUpdate = historyValid == false;
		var newlyExposedProbeCount = DdgiUtilities.GetNewlyExposedProbeCount(
			resources.DdgiScrollDelta,
			gridShape,
			historyValid);
		var activeProbeCount = DdgiUtilities.GetActiveProbeCount(
			gridShape,
			probeUpdateFrames,
			probeUpdateFrameIndex,
			forceFullProbeUpdate,
			resources.DdgiScrollDelta,
			historyValid);
		var raysPerProbe = Math.Clamp(config.RaysPerProbe, 1, DdgiUtilities.MaxRaySamplesPerProbe);
		LastStats = new DdgiPassStats(
			probeUpdateFrames,
			activeProbeCount,
			gridShape.ProbeCount,
			raysPerProbe,
			activeProbeCount * raysPerProbe,
			forceFullProbeUpdate,
			newlyExposedProbeCount);
		return new DdgiPassConfig
		{
			TracePipeline = _tracePipeline!,
			RelocatePipeline = _relocatePipeline!,
			IrradianceIntegratePipeline = _irradianceIntegratePipeline!,
			VisibilityIntegratePipeline = _visibilityIntegratePipeline!,
			BorderUpdatePipeline = _borderUpdatePipeline!,
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
			ProbeStateWriteHandle = _bindlessRegistry.RegisterRwTexture(context.GetTexture(resources.DdgiProbeStateWrite)),
			EnvironmentHandle = resources.SkyboxEnvironment.IsValid
				? _bindlessRegistry.GetTextureHandle(context.GetTexture(resources.SkyboxEnvironment))
				: DescriptorHandle.Invalid,
			SamplerHandle = _linearSampler,
			InstanceBuffer = gpuDrawResources.InstanceBuffer ?? throw new InvalidOperationException("GpuDraw instance buffer missing."),
			MaterialBuffer = gpuDrawResources.MaterialBuffer ?? throw new InvalidOperationException("GpuDraw material buffer missing."),
			MeshBuffer = gpuDrawResources.MeshBuffer ?? throw new InvalidOperationException("GpuDraw mesh buffer missing."),
			InstanceIndexToInstanceHandleBuffer = rayTracingSceneResources.InstanceIndexToInstanceHandleBuffer,
			PackedMeshVertexBuffer = renderer.GetPackedMeshVertexBuffer() ?? throw new InvalidOperationException("Packed mesh vertex buffer missing."),
			PackedMeshIndexBuffer = renderer.GetPackedMeshIndexBuffer() ?? throw new InvalidOperationException("Packed mesh index buffer missing."),
			IrradianceEstimatorBuffer = context.GetBuffer(resources.DdgiIrradianceEstimator),
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
			MaxRayDistance = DdgiUtilities.GetMaxRayDistance(config),
			NormalBias = Math.Max(config.NormalBias, 0.0f),
			ViewBias = Math.Max(config.ViewBias, 0.0f),
			IrradianceTemporalBlendSpeed = Math.Clamp(config.IrradianceTemporalBlendSpeed, 1.0f / 256.0f, 1.0f),
			Hysteresis = Math.Clamp(config.Hysteresis, 0.0f, 0.9999f),
			ProbeRelocationEnabled = config.ProbeRelocationEnabled,
			ProbeMinFrontfaceDistance = Math.Max(config.ProbeMinFrontfaceDistance, 0.0f),
			ProbeBackfaceThreshold = Math.Clamp(config.ProbeBackfaceThreshold, 0.0f, 1.0f),
			ProbeMaxRelocationDistance = Math.Max(config.ProbeSpacing, 0.001f) * Math.Clamp(config.ProbeMaxRelocationDistanceFactor, 0.0f, 0.5f),
			DirectLightDirection = directLight.Direction,
			DirectLightColorIntensity = directLight.ColorIntensity,
			FrameIndex = frameIndex,
			HistoryValid = historyValid,
			ForceFullProbeUpdate = forceFullProbeUpdate,
			SidecarHitShadingAvailable = rayTracingSceneResources.LastStats.SidecarHitShadingAvailable
		};
	}

	public void RecordTrace(RenderGraphContext context, in DdgiPassConfig config)
	{
		var commandList = context.CommandList;
		commandList.BindPipeline(config.TracePipeline);
		WriteBindlessConstants(_traceBindlessWriter, commandList, config);
		WriteSettingsConstants(_traceSettingsWriter, commandList, config);
		commandList.SynchronizeAccelerationStructureBuildForComputeRead(config.TopLevelAccelerationStructure);
		commandList.SetComputeAccelerationStructure(3, config.TopLevelAccelerationStructure);
		commandList.SetComputeBuffer(4, config.InstanceBuffer);
		commandList.SetComputeBuffer(5, config.MaterialBuffer);
		commandList.SetComputeBuffer(6, config.InstanceIndexToInstanceHandleBuffer);
		commandList.SetComputeBuffer(7, config.MeshBuffer);
		commandList.SetComputeBuffer(8, config.PackedMeshVertexBuffer);
		commandList.SetComputeBuffer(9, config.PackedMeshIndexBuffer);
		var threadGroupSize = _traceThreadGroupSize ?? throw new InvalidOperationException("DDGI trace threadgroup size was not initialized.");
		var width = Math.Max(config.VisibilityAtlasSize.X, config.IrradianceAtlasSize.X);
		var height = Math.Max(config.VisibilityAtlasSize.Y, config.IrradianceAtlasSize.Y);
		var (dispatchX, dispatchY, dispatchZ) = threadGroupSize.GetDispatchGroupCount((uint)width, (uint)height);
		commandList.Dispatch(dispatchX, dispatchY, dispatchZ);
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
		var threadGroupSize = _visibilityIntegrateThreadGroupSize ?? throw new InvalidOperationException("DDGI visibility integrate threadgroup size was not initialized.");
		var (dispatchX, dispatchY, dispatchZ) = threadGroupSize.GetDispatchGroupCount(
			(uint)config.VisibilityAtlasSize.X,
			(uint)config.VisibilityAtlasSize.Y);
		commandList.Dispatch(dispatchX, dispatchY, dispatchZ);
	}

	public void RecordRelocate(RenderGraphContext context, in DdgiPassConfig config)
	{
		var commandList = context.CommandList;
		commandList.BindPipeline(config.RelocatePipeline);
		WriteBindlessConstants(_relocateBindlessWriter, commandList, config);
		WriteSettingsConstants(_relocateSettingsWriter, commandList, config);
		var threadGroupSize = _relocateThreadGroupSize ?? throw new InvalidOperationException("DDGI relocation threadgroup size was not initialized.");
		var (dispatchX, dispatchY, dispatchZ) = threadGroupSize.GetDispatchGroupCount((uint)config.GridShape.AtlasColumns, (uint)config.GridShape.AtlasRows);
		commandList.Dispatch(dispatchX, dispatchY, dispatchZ);
	}

	public void RecordBorderUpdate(RenderGraphContext context, in DdgiPassConfig config)
	{
		var commandList = context.CommandList;
		commandList.BindPipeline(config.BorderUpdatePipeline);
		WriteBindlessConstants(_borderBindlessWriter, commandList, config);
		WriteSettingsConstants(_borderSettingsWriter, commandList, config);
		var threadGroupSize = _borderUpdateThreadGroupSize ?? throw new InvalidOperationException("DDGI border-update threadgroup size was not initialized.");
		var (dispatchX, dispatchY, dispatchZ) = threadGroupSize.GetDispatchGroupCount(
			(uint)config.VisibilityAtlasSize.X,
			(uint)config.VisibilityAtlasSize.Y);
		commandList.Dispatch(dispatchX, dispatchY, dispatchZ);
	}

	private static void WriteBindlessConstants(ShaderPropertyWriter? writer, IGfxCommandList commandList, in DdgiPassConfig config)
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
		bindlessWriter.SetUInt("probeStateWriteHandle", config.ProbeStateWriteHandle.Value);
		bindlessWriter.SetUInt("environmentHandle", config.EnvironmentHandle.Value);
		bindlessWriter.SetUInt("samplerHandle", config.SamplerHandle.Value);
		commandList.SetComputeConstants(bindlessWriter.RegisterIndex, bindlessWriter.AsBytes());
	}

	private static void WriteSettingsConstants(ShaderPropertyWriter? writer, IGfxCommandList commandList, in DdgiPassConfig config)
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
		settingsWriter.SetFloat("maxRayDistance", config.MaxRayDistance);
		settingsWriter.SetFloat("normalBias", config.NormalBias);
		settingsWriter.SetFloat("viewBias", config.ViewBias);
		settingsWriter.SetFloat("irradianceTemporalBlendSpeed", config.IrradianceTemporalBlendSpeed);
		settingsWriter.SetFloat("hysteresis", config.Hysteresis);
		settingsWriter.SetUInt("probeRelocationEnabled", config.ProbeRelocationEnabled ? 1u : 0u);
		settingsWriter.SetFloat("probeMinFrontfaceDistance", config.ProbeMinFrontfaceDistance);
		settingsWriter.SetFloat("probeBackfaceThreshold", config.ProbeBackfaceThreshold);
		settingsWriter.SetFloat("probeMaxRelocationDistance", config.ProbeMaxRelocationDistance);
		settingsWriter.SetVector3("directLightDirection", config.DirectLightDirection);
		settingsWriter.SetVector3("directLightColorIntensity", config.DirectLightColorIntensity);
		settingsWriter.SetUInt("frameIndex", config.FrameIndex);
		settingsWriter.SetUInt("historyValid", config.HistoryValid ? 1u : 0u);
		settingsWriter.SetUInt("sidecarHitShadingAvailable", config.SidecarHitShadingAvailable ? 1u : 0u);
		commandList.SetComputeConstants(settingsWriter.RegisterIndex, settingsWriter.AsBytes());
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
		if (_tracePipeline is not null &&
		    _relocatePipeline is not null &&
		    _irradianceIntegratePipeline is not null &&
		    _visibilityIntegratePipeline is not null &&
		    _borderUpdatePipeline is not null)
		{
			if (_compiledBackendKind.HasValue && _compiledBackendKind.Value != device.BackendKind)
			{
				throw new InvalidOperationException($"DdgiPass is already compiled for backend '{_compiledBackendKind.Value}', but was requested for '{device.BackendKind}'.");
			}

			return;
		}

		if (device.BackendKind != GraphicsBackendKind.Metal)
		{
			throw new NotImplementedException("Ray traced DDGI is currently implemented for Metal only.");
		}

		var trace = _shaderCompiler.GetComputeShaderWithReflection("ddgi_trace.compute.slang", "CSMain", device.BackendKind);
		var relocate = _shaderCompiler.GetComputeShaderWithReflection("ddgi_relocate.compute.slang", "CSMain", device.BackendKind);
		var irradianceIntegrate = _shaderCompiler.GetComputeShaderWithReflection("ddgi_irradiance_integrate.compute.slang", "CSMain", device.BackendKind);
		var visibilityIntegrate = _shaderCompiler.GetComputeShaderWithReflection("ddgi_integrate.compute.slang", "CSMain", device.BackendKind);
		var border = _shaderCompiler.GetComputeShaderWithReflection("ddgi_border_update.compute.slang", "CSMain", device.BackendKind);
		_traceShader = trace.Bytecode;
		_relocateShader = relocate.Bytecode;
		_irradianceIntegrateShader = irradianceIntegrate.Bytecode;
		_visibilityIntegrateShader = visibilityIntegrate.Bytecode;
		_borderUpdateShader = border.Bytecode;
		_traceThreadGroupSize = trace.ThreadGroupSize;
		_relocateThreadGroupSize = relocate.ThreadGroupSize;
		_irradianceIntegrateThreadGroupSize = irradianceIntegrate.ThreadGroupSize;
		_visibilityIntegrateThreadGroupSize = visibilityIntegrate.ThreadGroupSize;
		_borderUpdateThreadGroupSize = border.ThreadGroupSize;
		_traceBindlessWriter = new ShaderPropertyWriter(trace.ReflectionLayout.GetConstantBuffer("BindlessHandles"));
		_traceSettingsWriter = new ShaderPropertyWriter(trace.ReflectionLayout.GetConstantBuffer("DdgiSettings"));
		_relocateBindlessWriter = new ShaderPropertyWriter(relocate.ReflectionLayout.GetConstantBuffer("BindlessHandles"));
		_relocateSettingsWriter = new ShaderPropertyWriter(relocate.ReflectionLayout.GetConstantBuffer("DdgiSettings"));
		_irradianceIntegrateBindlessWriter = new ShaderPropertyWriter(irradianceIntegrate.ReflectionLayout.GetConstantBuffer("BindlessHandles"));
		_irradianceIntegrateSettingsWriter = new ShaderPropertyWriter(irradianceIntegrate.ReflectionLayout.GetConstantBuffer("DdgiSettings"));
		_visibilityIntegrateBindlessWriter = new ShaderPropertyWriter(visibilityIntegrate.ReflectionLayout.GetConstantBuffer("BindlessHandles"));
		_visibilityIntegrateSettingsWriter = new ShaderPropertyWriter(visibilityIntegrate.ReflectionLayout.GetConstantBuffer("DdgiSettings"));
		_borderBindlessWriter = new ShaderPropertyWriter(border.ReflectionLayout.GetConstantBuffer("BindlessHandles"));
		_borderSettingsWriter = new ShaderPropertyWriter(border.ReflectionLayout.GetConstantBuffer("DdgiSettings"));
		_tracePipeline = CreatePipeline(device, "ddgi_trace.compute.slang", _traceShader, _traceThreadGroupSize);
		_relocatePipeline = CreatePipeline(device, "ddgi_relocate.compute.slang", _relocateShader, _relocateThreadGroupSize);
		_irradianceIntegratePipeline = CreatePipeline(device, "ddgi_irradiance_integrate.compute.slang", _irradianceIntegrateShader, _irradianceIntegrateThreadGroupSize);
		_visibilityIntegratePipeline = CreatePipeline(device, "ddgi_integrate.compute.slang", _visibilityIntegrateShader, _visibilityIntegrateThreadGroupSize);
		_borderUpdatePipeline = CreatePipeline(device, "ddgi_border_update.compute.slang", _borderUpdateShader, _borderUpdateThreadGroupSize);
		_compiledBackendKind = device.BackendKind;
	}

	private static IGfxPipeline CreatePipeline(
		IGfxDevice device,
		string shaderVariant,
		ReadOnlyMemory<byte> shader,
		ComputeThreadGroupSize? threadGroupSize)
	{
		var pipelineKey = new PipelineKey(
			PassKind.Compute,
			vertexEntryPoint: null,
			pixelEntryPoint: null,
			computeEntryPoint: "CSMain",
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
	int NewlyExposedProbeCount);

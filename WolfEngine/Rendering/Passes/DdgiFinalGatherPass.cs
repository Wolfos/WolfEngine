using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

/// <summary>
/// Higher quality DDGI shading for opaque surfaces. Traces each pixel's first diffuse bounce, shades the hit with the
/// primary directional light and the probe irradiance found there, and writes the result for deferred lighting to use
/// in place of probe interpolation at the visible surface. Runs after the probe update so the hits sample this frame's
/// probes.
/// </summary>
public sealed class DdgiFinalGatherPass
{
	private readonly IShaderProvider _shaderProvider;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private IGfxPipeline? _pipeline;
	private ReadOnlyMemory<byte> _computeShader;
	private ComputeThreadGroupSize? _threadGroupSize;
	private GraphicsBackendKind? _compiledBackendKind;
	private ShaderPropertyWriter? _bindlessWriter;
	private ShaderPropertyWriter? _cameraWriter;
	private ShaderPropertyWriter? _settingsWriter;
	private DescriptorHandle _linearSampler = DescriptorHandle.Invalid;
	private uint _frameIndex;

	public DdgiFinalGatherPass(IShaderProvider shaderProvider, BindlessResourceRegistry bindlessRegistry)
	{
		_shaderProvider = shaderProvider ?? throw new ArgumentNullException(nameof(shaderProvider));
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
	}

	public DdgiFinalGatherPassConfig BuildConfig(
		RenderGraphContext context,
		RenderViewResources resources,
		RenderFrameSharedResources sharedResources,
		IGfxDevice device,
		IRenderer renderer,
		GpuDrawResources gpuDrawResources,
		IRayTracingSceneResources rayTracingSceneResources,
		SceneDrawData sceneData)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(device);
		ArgumentNullException.ThrowIfNull(renderer);
		ArgumentNullException.ThrowIfNull(gpuDrawResources);
		ArgumentNullException.ThrowIfNull(rayTracingSceneResources);
		ArgumentNullException.ThrowIfNull(sceneData);

		var pipeline = EnsurePipeline(device);
		_bindlessRegistry.EnsureInitialized(device);
		if (_linearSampler.IsValid == false)
		{
			_linearSampler = _bindlessRegistry.GetSamplerHandle(
				new SamplerDescriptor(FilterMode.Bilinear, AddressMode.Clamp, AddressMode.Clamp, AddressMode.Clamp));
		}

		var ddgiConfig = resources.Config.DiffuseGlobalIllumination;
		var settings = ddgiConfig.FinalGather;
		var emissiveFallback = context.GetTexture(resources.GBufferEmissive);
		var environment = sharedResources.SkyboxEnvironment.IsValid
			? context.GetTexture(sharedResources.SkyboxEnvironment)
			: emissiveFallback;
		var irradiance = sharedResources.SkyboxIrradiance.IsValid
			? context.GetTexture(sharedResources.SkyboxIrradiance)
			: emissiveFallback;
		var outputTexture = context.GetTexture(resources.DdgiFinalGatherTrace);
		var directLight = DdgiPass.GetPrimaryDirectionalLight(sceneData);

		return new DdgiFinalGatherPassConfig
		{
			Pipeline = pipeline,
			Depth = _bindlessRegistry.RegisterDepthTexture(context.GetTexture(resources.GBufferDepth)),
			Normal = _bindlessRegistry.GetTextureHandle(context.GetTexture(resources.GBufferNormal)),
			Output = _bindlessRegistry.RegisterRwTexture(outputTexture),
			Environment = _bindlessRegistry.GetTextureHandle(environment),
			Irradiance = _bindlessRegistry.GetTextureHandle(irradiance),
			LinearSampler = _linearSampler,
			ErrorTexture = _bindlessRegistry.ErrorTextureHandle,
			DdgiIrradianceL0 = _bindlessRegistry.GetTextureHandle(context.GetTexture(resources.DdgiIrradianceL0HistoryWrite)),
			DdgiIrradianceLy = _bindlessRegistry.GetTextureHandle(context.GetTexture(resources.DdgiIrradianceLyHistoryWrite)),
			DdgiIrradianceLz = _bindlessRegistry.GetTextureHandle(context.GetTexture(resources.DdgiIrradianceLzHistoryWrite)),
			DdgiIrradianceLx = _bindlessRegistry.GetTextureHandle(context.GetTexture(resources.DdgiIrradianceLxHistoryWrite)),
			DdgiVisibility = _bindlessRegistry.GetTextureHandle(context.GetTexture(resources.DdgiVisibilityHistoryWrite)),
			DdgiProbeState = _bindlessRegistry.GetTextureHandle(context.GetTexture(resources.DdgiProbeStateWrite)),
			DdgiProbeActivity = _bindlessRegistry.GetTextureHandle(context.GetTexture(resources.DdgiProbeActivity)),
			DdgiProbeRelocationDecision = _bindlessRegistry.GetTextureHandle(context.GetTexture(resources.DdgiProbeRelocationDecision)),
			FramebufferSize = resources.SceneFramebufferSize,
			DispatchSize = new(outputTexture.Descriptor.Width, outputTexture.Descriptor.Height),
			FrameIndex = _frameIndex++,
			SidecarHitShadingAvailable = rayTracingSceneResources.LastStats.SidecarHitShadingAvailable,
			MaxRayDistance = Math.Max(settings.MaxRayDistance, 0.01f),
			Bias = Math.Max(settings.Bias, 0.0f),
			FireflyClamp = Math.Max(settings.FireflyClamp, 0.0f),
			DirectLightDirection = directLight.Direction,
			DirectLightColorIntensity = directLight.ColorIntensity,
			DdgiOrigin = resources.DdgiRuntimeOrigin,
			DdgiProbeSpacing = Math.Max(ddgiConfig.ProbeSpacing, 0.001f),
			DdgiStorageOffset = resources.DdgiStorageOffset,
			DdgiGridShape = DdgiUtilities.GetGridShape(ddgiConfig),
			DdgiVisibilityTileInteriorSize = DdgiUtilities.GetVisibilityTileInteriorSize(ddgiConfig),
			DdgiMaxRayDistance = DdgiUtilities.GetMaxRayDistance(ddgiConfig),
			DdgiViewBias = Math.Max(ddgiConfig.ViewBias, 0.0f),
			DdgiHorizontalBlendDistance = Math.Max(ddgiConfig.HorizontalBlendDistance, 0.001f),
			DdgiVerticalBlendDistance = Math.Max(ddgiConfig.VerticalBlendDistance, 0.001f),
			DdgiProbeRelocationEnabled = ddgiConfig.ProbeRelocationEnabled,
			TopLevelAccelerationStructure = rayTracingSceneResources.TopLevelAccelerationStructure
				?? throw new InvalidOperationException("DDGI final gather requires a valid top-level acceleration structure."),
			InstanceBuffer = gpuDrawResources.InstanceBuffer
				?? throw new InvalidOperationException("GpuDraw instance buffer missing."),
			MaterialBuffer = gpuDrawResources.MaterialBuffer
				?? throw new InvalidOperationException("GpuDraw material buffer missing."),
			InstanceIndexToInstanceHandleBuffer = rayTracingSceneResources.InstanceIndexToInstanceHandleBuffer
				?? throw new InvalidOperationException("DDGI final gather requires RTAS instance sidecar resources."),
			MeshBuffer = gpuDrawResources.MeshBuffer
				?? throw new InvalidOperationException("GpuDraw mesh buffer missing."),
			PackedMeshVertexBuffer = renderer.GetPackedMeshVertexBuffer()
				?? throw new InvalidOperationException("Packed mesh vertex buffer missing."),
			PackedMeshIndexBuffer = renderer.GetPackedMeshIndexBuffer()
				?? throw new InvalidOperationException("Packed mesh index buffer missing."),
			TerrainMaterialBuffer = gpuDrawResources.TerrainMaterialBuffer
				?? throw new InvalidOperationException("GpuDraw terrain material buffer missing."),
			TerrainLayerBuffer = gpuDrawResources.TerrainLayerBuffer
				?? throw new InvalidOperationException("GpuDraw terrain layer buffer missing."),
			InstanceIndexToTerrainRayTracingResolutionBuffer =
				rayTracingSceneResources.InstanceIndexToTerrainRayTracingResolutionBuffer
				?? throw new InvalidOperationException("DDGI final gather requires RTAS terrain sidecar resources.")
		};
	}

	public void Record(RenderGraphContext context, in DdgiFinalGatherPassConfig config, SceneDrawData sceneData)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(sceneData);

		var commandList = context.CommandList;
		commandList.BindPipeline(config.Pipeline);

		var bindlessWriter = _bindlessWriter
			?? throw new InvalidOperationException("DDGI final gather bindless writer was not initialized.");
		bindlessWriter.Clear();
		bindlessWriter.SetUInt("depthHandle", config.Depth.Value);
		bindlessWriter.SetUInt("normalHandle", config.Normal.Value);
		bindlessWriter.SetUInt("outputHandle", config.Output.Value);
		bindlessWriter.SetUInt("environmentHandle", config.Environment.Value);
		bindlessWriter.SetUInt("irradianceHandle", config.Irradiance.Value);
		bindlessWriter.SetUInt("samplerHandle", config.LinearSampler.Value);
		bindlessWriter.SetUInt("errorTextureHandle", config.ErrorTexture.Value);
		bindlessWriter.SetUInt("ddgiIrradianceL0Handle", config.DdgiIrradianceL0.Value);
		bindlessWriter.SetUInt("ddgiIrradianceLyHandle", config.DdgiIrradianceLy.Value);
		bindlessWriter.SetUInt("ddgiIrradianceLzHandle", config.DdgiIrradianceLz.Value);
		bindlessWriter.SetUInt("ddgiIrradianceLxHandle", config.DdgiIrradianceLx.Value);
		bindlessWriter.SetUInt("ddgiVisibilityHandle", config.DdgiVisibility.Value);
		bindlessWriter.SetUInt("ddgiProbeStateHandle", config.DdgiProbeState.Value);
		bindlessWriter.SetUInt("ddgiProbeActivityHandle", config.DdgiProbeActivity.Value);
		bindlessWriter.SetUInt("ddgiProbeRelocationDecisionHandle", config.DdgiProbeRelocationDecision.Value);
		commandList.SetComputeConstants(bindlessWriter.RegisterIndex, bindlessWriter.AsBytes());

		var cameraWriter = _cameraWriter
			?? throw new InvalidOperationException("DDGI final gather camera writer was not initialized.");
		cameraWriter.Clear();
		cameraWriter.SetMatrix4x4("camera.invProjection", sceneData.InverseProjection);
		cameraWriter.SetMatrix4x4("camera.invViewProjection", sceneData.InverseViewProjection);
		cameraWriter.SetVector3("camera.cameraOrigin", sceneData.CameraOrigin);
		cameraWriter.SetMatrix4x4("camera.viewMatrix", sceneData.ViewMatrix);
		cameraWriter.SetFloat("camera.nearPlane", sceneData.NearPlane);
		cameraWriter.SetFloat("camera.farPlane", sceneData.FarPlane);
		cameraWriter.SetUInt("camera.frameSizeX", (uint)Math.Max(sceneData.SceneFramebufferSize.X, 1));
		cameraWriter.SetUInt("camera.frameSizeY", (uint)Math.Max(sceneData.SceneFramebufferSize.Y, 1));
		commandList.SetComputeConstants(cameraWriter.RegisterIndex, cameraWriter.AsBytes());

		var settingsWriter = _settingsWriter
			?? throw new InvalidOperationException("DDGI final gather settings writer was not initialized.");
		settingsWriter.Clear();
		settingsWriter.SetUInt("framebufferSizeX", (uint)Math.Max(config.FramebufferSize.X, 1));
		settingsWriter.SetUInt("framebufferSizeY", (uint)Math.Max(config.FramebufferSize.Y, 1));
		settingsWriter.SetUInt("outputSizeX", (uint)Math.Max(config.DispatchSize.X, 1));
		settingsWriter.SetUInt("outputSizeY", (uint)Math.Max(config.DispatchSize.Y, 1));
		settingsWriter.SetUInt("frameIndex", config.FrameIndex);
		settingsWriter.SetUInt("sidecarHitShadingAvailable", config.SidecarHitShadingAvailable ? 1u : 0u);
		settingsWriter.SetFloat("maxRayDistance", config.MaxRayDistance);
		settingsWriter.SetFloat("bias", config.Bias);
		settingsWriter.SetFloat("fireflyClamp", config.FireflyClamp);
		settingsWriter.SetVector3("directLightDirection", config.DirectLightDirection);
		settingsWriter.SetVector3("directLightColorIntensity", config.DirectLightColorIntensity);
		settingsWriter.SetVector3("ddgiOrigin", config.DdgiOrigin);
		settingsWriter.SetFloat("ddgiProbeSpacing", config.DdgiProbeSpacing);
		settingsWriter.SetInt("ddgiStorageOffsetX", config.DdgiStorageOffset.X);
		settingsWriter.SetInt("ddgiStorageOffsetY", config.DdgiStorageOffset.Y);
		settingsWriter.SetInt("ddgiStorageOffsetZ", config.DdgiStorageOffset.Z);
		settingsWriter.SetUInt("ddgiProbeCountX", (uint)config.DdgiGridShape.CountX);
		settingsWriter.SetUInt("ddgiProbeCountY", (uint)config.DdgiGridShape.CountY);
		settingsWriter.SetUInt("ddgiProbeCountZ", (uint)config.DdgiGridShape.CountZ);
		settingsWriter.SetUInt("ddgiProbeCount", (uint)config.DdgiGridShape.ProbeCount);
		settingsWriter.SetUInt("ddgiAtlasColumns", (uint)config.DdgiGridShape.AtlasColumns);
		settingsWriter.SetUInt("ddgiAtlasRows", (uint)config.DdgiGridShape.AtlasRows);
		settingsWriter.SetUInt("ddgiVisibilityTileInteriorSize", (uint)config.DdgiVisibilityTileInteriorSize);
		settingsWriter.SetFloat("ddgiMaxRayDistance", config.DdgiMaxRayDistance);
		settingsWriter.SetFloat("ddgiViewBias", config.DdgiViewBias);
		settingsWriter.SetFloat("ddgiHorizontalBlendDistance", config.DdgiHorizontalBlendDistance);
		settingsWriter.SetFloat("ddgiVerticalBlendDistance", config.DdgiVerticalBlendDistance);
		settingsWriter.SetUInt("ddgiProbeRelocationEnabled", config.DdgiProbeRelocationEnabled ? 1u : 0u);
		commandList.SetComputeConstants(settingsWriter.RegisterIndex, settingsWriter.AsBytes());

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

		var threadGroupSize = _threadGroupSize
			?? throw new InvalidOperationException("DDGI final gather threadgroup size was not initialized.");
		var (dispatchX, dispatchY, dispatchZ) = threadGroupSize.GetDispatchGroupCount(
			(uint)Math.Max(config.DispatchSize.X, 1),
			(uint)Math.Max(config.DispatchSize.Y, 1));
		commandList.Dispatch(dispatchX, dispatchY, dispatchZ);
	}

	private IGfxPipeline EnsurePipeline(IGfxDevice device)
	{
		if (_pipeline is not null)
		{
			if (_compiledBackendKind.HasValue && _compiledBackendKind.Value != device.BackendKind)
			{
				throw new InvalidOperationException(
					$"DdgiFinalGatherPass is already compiled for backend '{_compiledBackendKind.Value}', " +
					$"but was requested for '{device.BackendKind}'.");
			}
			return _pipeline;
		}
		if (device.SupportsRayTracing == false)
		{
			throw new NotSupportedException("DDGI final gather requires a ray-tracing capable graphics device.");
		}

		var compiled = _shaderProvider.GetComputeShaderWithReflection(
			EngineShaderPrograms.DdgiFinalGather,
			"DdgiFinalGatherCS",
			device.BackendKind);
		_computeShader = compiled.Bytecode;
		_threadGroupSize = compiled.ThreadGroupSize;
		_bindlessWriter = new ShaderPropertyWriter(compiled.ReflectionLayout.GetConstantBuffer("BindlessHandles"));
		_cameraWriter = new ShaderPropertyWriter(compiled.ReflectionLayout.GetConstantBuffer("CameraParams"));
		_settingsWriter = new ShaderPropertyWriter(compiled.ReflectionLayout.GetConstantBuffer("FinalGatherSettings"));
		_compiledBackendKind = device.BackendKind;

		var key = new PipelineKey(
			PassKind.Compute,
			vertexEntryPoint: null,
			pixelEntryPoint: null,
			computeEntryPoint: "DdgiFinalGatherCS",
			renderTargets: new RenderTargetFormats(Array.Empty<TextureFormat>()),
			depthStencil: new DepthStencilFormat(TextureFormat.Unknown),
			renderState: default,
			shaderVariant: "ddgi_final_gather.compute.slang");
		_pipeline = device.GetOrCreatePipeline(
			key,
			new ShaderBytecodeSet(compute: _computeShader, computeThreadGroupSize: _threadGroupSize));
		return _pipeline;
	}
}

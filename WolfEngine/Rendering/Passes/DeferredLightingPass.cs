using System.Numerics;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

/// <summary>
/// API-agnostic deferred lighting compute pass that shades the G-buffer into the output texture.
/// </summary>
public sealed class DeferredLightingPass
{
	private readonly IShaderProvider _shaderCompiler;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private IGfxPipeline? _pipeline;
	private ReadOnlyMemory<byte> _computeShader;
	private ComputeThreadGroupSize? _threadGroupSize;
	private GraphicsBackendKind? _compiledBackendKind;
	private ShaderPropertyWriter? _bindlessWriter;
	private ShaderPropertyWriter? _cameraWriter;
	private ShaderPropertyWriter? _lightingWriter;
	private DescriptorHandle _linearSampler = DescriptorHandle.Invalid;
	private DescriptorHandle _shadowSampler = DescriptorHandle.Invalid;

	public DeferredLightingPass(IShaderProvider shaderCompiler, BindlessResourceRegistry bindlessRegistry)
	{
		_shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
	}

	public DeferredLightingPassConfig BuildConfig(
		RenderGraphContext context,
		RenderGraphFrameResources resources,
		IGfxDevice device,
		GpuDrawResources gpuDrawResources,
		ShadowFrameData shadowData,
		SceneDrawData sceneData)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(device);
		ArgumentNullException.ThrowIfNull(gpuDrawResources);
		ArgumentNullException.ThrowIfNull(sceneData);

		var pipeline = EnsurePipeline(device);
		_bindlessRegistry.EnsureInitialized(device);

		if (_linearSampler.IsValid == false)
		{
			var sampler = new SamplerDescriptor(FilterMode.Bilinear, AddressMode.Clamp, AddressMode.Clamp, AddressMode.Clamp);
			_linearSampler = _bindlessRegistry.GetSamplerHandle(sampler);
		}
		if (_shadowSampler.IsValid == false)
		{
			var shadowSampler = new SamplerDescriptor(FilterMode.Bilinear, AddressMode.Clamp, AddressMode.Clamp, AddressMode.Clamp);
			_shadowSampler = _bindlessRegistry.GetSamplerHandle(shadowSampler);
		}

		var albedo = context.GetTexture(resources.GBufferAlbedo);
		var normal = context.GetTexture(resources.GBufferNormal);
		var material = context.GetTexture(resources.GBufferMaterial);
		var emissive = context.GetTexture(resources.GBufferEmissive);
		var depth = context.GetTexture(resources.GBufferDepth);
		var ambientOcclusion = resources.AmbientOcclusionFinal.IsValid
			? context.GetTexture(resources.AmbientOcclusionFinal)
			: null;
		var reflections = resources.ReflectionsRadiance.IsValid
			? context.GetTexture(resources.ReflectionsRadiance)
			: null;
		var ddgiIrradianceL0 = resources.DdgiIrradianceL0HistoryWrite.IsValid
			? context.GetTexture(resources.DdgiIrradianceL0HistoryWrite)
			: null;
		var ddgiIrradianceLy = resources.DdgiIrradianceLyHistoryWrite.IsValid
			? context.GetTexture(resources.DdgiIrradianceLyHistoryWrite)
			: null;
		var ddgiIrradianceLz = resources.DdgiIrradianceLzHistoryWrite.IsValid
			? context.GetTexture(resources.DdgiIrradianceLzHistoryWrite)
			: null;
		var ddgiIrradianceLx = resources.DdgiIrradianceLxHistoryWrite.IsValid
			? context.GetTexture(resources.DdgiIrradianceLxHistoryWrite)
			: null;
		var ddgiVisibility = resources.DdgiVisibilityHistoryWrite.IsValid
			? context.GetTexture(resources.DdgiVisibilityHistoryWrite)
			: null;
		var ddgiProbeState = resources.DdgiProbeStateWrite.IsValid
			? context.GetTexture(resources.DdgiProbeStateWrite)
			: null;
		var ddgiProbeActivity = resources.DdgiProbeActivity.IsValid
			? context.GetTexture(resources.DdgiProbeActivity)
			: null;
		var ddgiProbeRelocationDecision = resources.DdgiProbeRelocationDecision.IsValid
			? context.GetTexture(resources.DdgiProbeRelocationDecision)
			: null;
		var ddgiFinalContribution = resources.DdgiFinalContribution.IsValid
			? context.GetTexture(resources.DdgiFinalContribution)
			: null;
		var ddgiProbeBaseWeightDebug = resources.DdgiProbeBaseWeightDebug.IsValid
			? context.GetTexture(resources.DdgiProbeBaseWeightDebug)
			: null;
		var ddgiWeightedVisibilityDebug = resources.DdgiWeightedVisibilityDebug.IsValid
			? context.GetTexture(resources.DdgiWeightedVisibilityDebug)
			: null;
		var ddgiDominantProbeDebug = resources.DdgiDominantProbeDebug.IsValid
			? context.GetTexture(resources.DdgiDominantProbeDebug)
			: null;
		var ddgiDominantProbeCoordDebug = resources.DdgiDominantProbeCoordDebug.IsValid
			? context.GetTexture(resources.DdgiDominantProbeCoordDebug)
			: null;
		var ddgiProbeRelocationDebug = resources.DdgiProbeRelocationDebug.IsValid
			? context.GetTexture(resources.DdgiProbeRelocationDebug)
			: null;
		var ddgiProbeRelocationDecisionDebug = resources.DdgiProbeRelocationDecisionDebug.IsValid
			? context.GetTexture(resources.DdgiProbeRelocationDecisionDebug)
			: null;
		var shadowMapDepth0 = context.GetTexture(resources.ShadowMapDepth0);
		var shadowMapDepth1 = context.GetTexture(resources.ShadowMapDepth1);
		var shadowMapDepth2 = context.GetTexture(resources.ShadowMapDepth2);

		var environment = resources.SkyboxEnvironment.IsValid
			? context.GetTexture(resources.SkyboxEnvironment)
			: emissive;
		var irradiance = resources.SkyboxIrradiance.IsValid
			? context.GetTexture(resources.SkyboxIrradiance)
			: emissive;
		var prefilter = resources.SkyboxPrefilter.IsValid
			? context.GetTexture(resources.SkyboxPrefilter)
			: emissive;
		var brdfLut = resources.SkyboxBrdfLut.IsValid
			? context.GetTexture(resources.SkyboxBrdfLut)
			: emissive;

		var lighting = context.GetTexture(resources.LightingBuffer);
		var shadowResolution = Math.Max(1, shadowData.MapResolution);
		var ambientOcclusionHandle = _bindlessRegistry.GetTextureHandle(ambientOcclusion);
		var ddgiEnabled = DdgiUtilities.IsRayTracedDdgiEnabled(resources.Config) &&
		                  ddgiIrradianceL0 is not null &&
		                  ddgiIrradianceLy is not null &&
		                  ddgiIrradianceLz is not null &&
		                  ddgiIrradianceLx is not null &&
		                  ddgiVisibility is not null &&
		                  ddgiProbeState is not null &&
		                  ddgiProbeActivity is not null &&
		                  ddgiProbeRelocationDecision is not null;
		var ddgiGridShape = DdgiUtilities.GetGridShape(resources.Config.DiffuseGlobalIllumination);

		return new DeferredLightingPassConfig
		{
			Pipeline = pipeline,
			GBufferAlbedo = _bindlessRegistry.GetTextureHandle(albedo),
			GBufferNormal = _bindlessRegistry.GetTextureHandle(normal),
			GBufferMaterial = _bindlessRegistry.GetTextureHandle(material),
			GBufferEmissive = _bindlessRegistry.GetTextureHandle(emissive),
			GBufferDepth = _bindlessRegistry.GetTextureHandle(depth),
			AmbientOcclusion = ambientOcclusionHandle,
			Reflections = _bindlessRegistry.GetTextureHandle(reflections),
			DdgiIrradianceL0 = _bindlessRegistry.GetTextureHandle(ddgiIrradianceL0),
			DdgiIrradianceLy = _bindlessRegistry.GetTextureHandle(ddgiIrradianceLy),
			DdgiIrradianceLz = _bindlessRegistry.GetTextureHandle(ddgiIrradianceLz),
			DdgiIrradianceLx = _bindlessRegistry.GetTextureHandle(ddgiIrradianceLx),
			DdgiVisibility = _bindlessRegistry.GetTextureHandle(ddgiVisibility),
			DdgiProbeState = _bindlessRegistry.GetTextureHandle(ddgiProbeState),
			DdgiProbeActivity = _bindlessRegistry.GetTextureHandle(ddgiProbeActivity),
			DdgiProbeRelocationDecision = _bindlessRegistry.GetTextureHandle(ddgiProbeRelocationDecision),
			DdgiFinalContribution = ddgiFinalContribution is not null
				? _bindlessRegistry.RegisterRwTexture(ddgiFinalContribution)
				: DescriptorHandle.Invalid,
			DdgiProbeBaseWeightDebug = ddgiProbeBaseWeightDebug is not null
				? _bindlessRegistry.RegisterRwTexture(ddgiProbeBaseWeightDebug)
				: DescriptorHandle.Invalid,
			DdgiWeightedVisibilityDebug = ddgiWeightedVisibilityDebug is not null
				? _bindlessRegistry.RegisterRwTexture(ddgiWeightedVisibilityDebug)
				: DescriptorHandle.Invalid,
			DdgiDominantProbeDebug = ddgiDominantProbeDebug is not null
				? _bindlessRegistry.RegisterRwTexture(ddgiDominantProbeDebug)
				: DescriptorHandle.Invalid,
			DdgiDominantProbeCoordDebug = ddgiDominantProbeCoordDebug is not null
				? _bindlessRegistry.RegisterRwTexture(ddgiDominantProbeCoordDebug)
				: DescriptorHandle.Invalid,
			DdgiProbeRelocationDebug = ddgiProbeRelocationDebug is not null
				? _bindlessRegistry.RegisterRwTexture(ddgiProbeRelocationDebug)
				: DescriptorHandle.Invalid,
			DdgiProbeRelocationDecisionDebug = ddgiProbeRelocationDecisionDebug is not null
				? _bindlessRegistry.RegisterRwTexture(ddgiProbeRelocationDecisionDebug)
				: DescriptorHandle.Invalid,
			DdgiFinalContributionDebugEnabled = resources.WriteDdgiFinalContributionDebug,
			DdgiProbeDebugEnabled = resources.WriteDdgiProbeDebug,
			ShadowMapDepth0 = _bindlessRegistry.RegisterDepthTexture(shadowMapDepth0),
			ShadowMapDepth1 = _bindlessRegistry.RegisterDepthTexture(shadowMapDepth1),
			ShadowMapDepth2 = _bindlessRegistry.RegisterDepthTexture(shadowMapDepth2),
			SkyboxEnvironment = _bindlessRegistry.GetTextureHandle(environment),
			SkyboxIrradiance = _bindlessRegistry.GetTextureHandle(irradiance),
			SkyboxPrefilter = _bindlessRegistry.GetTextureHandle(prefilter),
			SkyboxBrdfLut = _bindlessRegistry.GetTextureHandle(brdfLut),
			LightingOutput = _bindlessRegistry.RegisterRwTexture(lighting),
			LinearSampler = _linearSampler,
			ShadowSampler = _shadowSampler,
			PointLightBuffer = gpuDrawResources.ClusterPointLightBuffer ?? throw new InvalidOperationException("Cluster point-light buffer missing."),
			ClusterHeaderBuffer = gpuDrawResources.ClusterHeaderBuffer ?? throw new InvalidOperationException("Cluster header buffer missing."),
			ClusterLightIndexBuffer = gpuDrawResources.ClusterLightIndexBuffer ?? throw new InvalidOperationException("Cluster light-index buffer missing."),
			ShadowViewProjection0 = shadowData.CascadeViewProjection0,
			ShadowViewProjection1 = shadowData.CascadeViewProjection1,
			ShadowViewProjection2 = shadowData.CascadeViewProjection2,
			ShadowSplit0 = shadowData.CascadeSplit0,
			ShadowSplit1 = shadowData.CascadeSplit1,
			ShadowSplit2 = shadowData.CascadeSplit2,
			ShadowCascadeBlendDistance = shadowData.CascadeBlendDistance,
			ShadowedDirectionalLightIndex = shadowData.ShadowedDirectionalLightIndex,
			ShadowDepthBiases = shadowData.DepthBiases,
			ShadowStrength = shadowData.Strength,
			ShadowsEnabled = shadowData.Enabled,
			ShadowMaxDistance = shadowData.MaxDistance,
			ShadowTexelSizeX = 1.0f / shadowResolution,
			ShadowTexelSizeY = 1.0f / shadowResolution,
			AoEnabled = resources.AmbientOcclusionFinal.IsValid,
			ReflectionsEnabled = reflections is not null,
			DdgiEnabled = ddgiEnabled,
			DdgiOrigin = resources.DdgiRuntimeOrigin,
			DdgiStorageOffset = resources.DdgiStorageOffset,
			DdgiScrollDelta = resources.DdgiScrollDelta,
			DdgiProbeSpacing = Math.Max(resources.Config.DiffuseGlobalIllumination.ProbeSpacing, 0.001f),
			DdgiProbeCountX = ddgiGridShape.CountX,
			DdgiProbeCountY = ddgiGridShape.CountY,
			DdgiProbeCountZ = ddgiGridShape.CountZ,
			DdgiProbeCount = ddgiGridShape.ProbeCount,
			DdgiAtlasColumns = ddgiGridShape.AtlasColumns,
			DdgiAtlasRows = ddgiGridShape.AtlasRows,
				DdgiMaxRayDistance = DdgiUtilities.GetMaxRayDistance(resources.Config.DiffuseGlobalIllumination),
				DdgiViewBias = Math.Max(resources.Config.DiffuseGlobalIllumination.ViewBias, 0.0f),
				DdgiHorizontalBlendDistance = Math.Max(resources.Config.DiffuseGlobalIllumination.HorizontalBlendDistance, 0.001f),
				DdgiVerticalBlendDistance = Math.Max(resources.Config.DiffuseGlobalIllumination.VerticalBlendDistance, 0.001f),
				DdgiProbeRelocationEnabled = resources.Config.DiffuseGlobalIllumination.ProbeRelocationEnabled,
				ClusterCountX = gpuDrawResources.ClusteredLightingLayout.Grid.X,
			ClusterCountY = gpuDrawResources.ClusteredLightingLayout.Grid.Y,
			ClusterCountZ = gpuDrawResources.ClusteredLightingLayout.Grid.Z,
			NearPlane = sceneData.NearPlane,
			FarPlane = sceneData.FarPlane,
			DispatchSize = resources.SceneFramebufferSize
		};
	}

	public void Record(RenderGraphContext context, ref DeferredLightingPassConfig config, SceneDrawData sceneData)
	{
		ArgumentNullException.ThrowIfNull(context);

		var commandList = context.CommandList;

		// Bind pipeline
		commandList.BindPipeline(config.Pipeline);

		var bindlessWriter = _bindlessWriter
			?? throw new InvalidOperationException("Deferred lighting bindless reflection writer was not initialized.");
		bindlessWriter.Clear();
		bindlessWriter.SetUInt("gbufferAlbedoHandle", config.GBufferAlbedo.Value);
		bindlessWriter.SetUInt("gbufferNormalHandle", config.GBufferNormal.Value);
		bindlessWriter.SetUInt("gbufferMaterialHandle", config.GBufferMaterial.Value);
		bindlessWriter.SetUInt("gbufferEmissiveHandle", config.GBufferEmissive.Value);
		bindlessWriter.SetUInt("gbufferDepthHandle", config.GBufferDepth.Value);
		bindlessWriter.SetUInt("ambientOcclusionHandle", config.AmbientOcclusion.Value);
		bindlessWriter.SetUInt("reflectionsHandle", config.Reflections.Value);
		bindlessWriter.SetUInt("ddgiIrradianceL0Handle", config.DdgiIrradianceL0.Value);
		bindlessWriter.SetUInt("ddgiIrradianceLyHandle", config.DdgiIrradianceLy.Value);
		bindlessWriter.SetUInt("ddgiIrradianceLzHandle", config.DdgiIrradianceLz.Value);
		bindlessWriter.SetUInt("ddgiIrradianceLxHandle", config.DdgiIrradianceLx.Value);
		bindlessWriter.SetUInt("ddgiVisibilityHandle", config.DdgiVisibility.Value);
		bindlessWriter.SetUInt("ddgiProbeStateHandle", config.DdgiProbeState.Value);
		bindlessWriter.SetUInt("ddgiProbeActivityHandle", config.DdgiProbeActivity.Value);
		bindlessWriter.SetUInt("ddgiProbeRelocationDecisionHandle", config.DdgiProbeRelocationDecision.Value);
		bindlessWriter.SetUInt("ddgiFinalContributionHandle", config.DdgiFinalContribution.Value);
		bindlessWriter.SetUInt("ddgiProbeBaseWeightDebugHandle", config.DdgiProbeBaseWeightDebug.Value);
		bindlessWriter.SetUInt("ddgiWeightedVisibilityDebugHandle", config.DdgiWeightedVisibilityDebug.Value);
		bindlessWriter.SetUInt("ddgiDominantProbeDebugHandle", config.DdgiDominantProbeDebug.Value);
		bindlessWriter.SetUInt("ddgiDominantProbeCoordDebugHandle", config.DdgiDominantProbeCoordDebug.Value);
		bindlessWriter.SetUInt("ddgiProbeRelocationDebugHandle", config.DdgiProbeRelocationDebug.Value);
		bindlessWriter.SetUInt("ddgiProbeRelocationDecisionDebugHandle", config.DdgiProbeRelocationDecisionDebug.Value);
		bindlessWriter.SetUInt("environmentHandle", config.SkyboxEnvironment.Value);
		bindlessWriter.SetUInt("irradianceHandle", config.SkyboxIrradiance.Value);
		bindlessWriter.SetUInt("prefilteredHandle", config.SkyboxPrefilter.Value);
		bindlessWriter.SetUInt("brdfLutHandle", config.SkyboxBrdfLut.Value);
		bindlessWriter.SetUInt("lightingTargetHandle", config.LightingOutput.Value);
		bindlessWriter.SetUInt("samplerHandle", config.LinearSampler.Value);
		bindlessWriter.SetUInt("shadowMapHandle0", config.ShadowMapDepth0.Value);
		bindlessWriter.SetUInt("shadowMapHandle1", config.ShadowMapDepth1.Value);
		bindlessWriter.SetUInt("shadowMapHandle2", config.ShadowMapDepth2.Value);
		bindlessWriter.SetUInt("shadowSamplerHandle", config.ShadowSampler.Value);
		commandList.SetComputeConstants(bindlessWriter.RegisterIndex, bindlessWriter.AsBytes());

		var cameraWriter = _cameraWriter
			?? throw new InvalidOperationException("Deferred lighting camera reflection writer was not initialized.");
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

		var lightingWriter = _lightingWriter
			?? throw new InvalidOperationException("Deferred lighting lighting reflection writer was not initialized.");
		lightingWriter.Clear();
		var directionalLightCount = 0;
		for (var i = 0; i < sceneData.Lights.Count && directionalLightCount < ClusteredLightingShared.MaxDirectionalLights; i++)
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
				forward = new Vector3(0, -1, 0);
			}

			var position = packet.Transform.Translation;
			var intensityScale = DirectionalLightUtility.GetIntensityScale(light, forward);

			lightingWriter.SetColorRGBA(
				$"directionalLights[{directionalLightCount}].colorIntensity",
				new ColorRGBA(light.Color.R, light.Color.G, light.Color.B, light.Intensity * intensityScale));
			lightingWriter.SetVector4($"directionalLights[{directionalLightCount}].directionAndType", new Vector4(forward, 0.0f));
			directionalLightCount++;
		}
		lightingWriter.SetUInt("directionalLightCount", (uint)directionalLightCount);

		lightingWriter.SetMatrix4x4("shadowViewProjection0", config.ShadowViewProjection0);
		lightingWriter.SetMatrix4x4("shadowViewProjection1", config.ShadowViewProjection1);
		lightingWriter.SetMatrix4x4("shadowViewProjection2", config.ShadowViewProjection2);
		lightingWriter.SetVector4(
			"shadowSplitsBlend",
			new Vector4(
				config.ShadowSplit0,
				config.ShadowSplit1,
				config.ShadowSplit2,
				config.ShadowCascadeBlendDistance));
		lightingWriter.SetVector4(
			"shadowTexelSizeBiasStrength",
			new Vector4(
				config.ShadowTexelSizeX,
				config.ShadowTexelSizeY,
				0.0f,
				config.ShadowStrength));
		lightingWriter.SetVector4("shadowDepthBiases", new Vector4(config.ShadowDepthBiases, 0.0f));
		lightingWriter.SetUInt(
			"shadowLightIndex",
			config.ShadowsEnabled ? (uint)Math.Max(config.ShadowedDirectionalLightIndex, 0) : 0u);
		lightingWriter.SetUInt("shadowsEnabled", config.ShadowsEnabled ? 1u : 0u);
		lightingWriter.SetUInt("aoEnabled", config.AoEnabled ? 1u : 0u);
		lightingWriter.SetUInt("reflectionsEnabled", config.ReflectionsEnabled ? 1u : 0u);
		lightingWriter.SetUInt("ddgiEnabled", config.DdgiEnabled ? 1u : 0u);
		lightingWriter.SetUInt("ddgiFinalContributionDebugEnabled", config.DdgiFinalContributionDebugEnabled ? 1u : 0u);
		lightingWriter.SetUInt("ddgiProbeDebugEnabled", config.DdgiProbeDebugEnabled ? 1u : 0u);
		lightingWriter.SetVector3("ddgiOrigin", config.DdgiOrigin);
		lightingWriter.SetFloat("ddgiProbeSpacing", config.DdgiProbeSpacing);
		lightingWriter.SetInt("ddgiStorageOffsetX", config.DdgiStorageOffset.X);
		lightingWriter.SetInt("ddgiStorageOffsetY", config.DdgiStorageOffset.Y);
		lightingWriter.SetInt("ddgiStorageOffsetZ", config.DdgiStorageOffset.Z);
		lightingWriter.SetInt("ddgiScrollDeltaX", config.DdgiScrollDelta.X);
		lightingWriter.SetInt("ddgiScrollDeltaY", config.DdgiScrollDelta.Y);
		lightingWriter.SetInt("ddgiScrollDeltaZ", config.DdgiScrollDelta.Z);
		lightingWriter.SetUInt("ddgiProbeCountX", (uint)config.DdgiProbeCountX);
		lightingWriter.SetUInt("ddgiProbeCountY", (uint)config.DdgiProbeCountY);
		lightingWriter.SetUInt("ddgiProbeCountZ", (uint)config.DdgiProbeCountZ);
		lightingWriter.SetUInt("ddgiProbeCount", (uint)config.DdgiProbeCount);
		lightingWriter.SetUInt("ddgiAtlasColumns", (uint)config.DdgiAtlasColumns);
		lightingWriter.SetUInt("ddgiAtlasRows", (uint)config.DdgiAtlasRows);
			lightingWriter.SetFloat("ddgiMaxRayDistance", config.DdgiMaxRayDistance);
			lightingWriter.SetFloat("ddgiViewBias", config.DdgiViewBias);
			lightingWriter.SetFloat("ddgiHorizontalBlendDistance", config.DdgiHorizontalBlendDistance);
			lightingWriter.SetFloat("ddgiVerticalBlendDistance", config.DdgiVerticalBlendDistance);
			lightingWriter.SetUInt("ddgiProbeRelocationEnabled", config.DdgiProbeRelocationEnabled ? 1u : 0u);
			lightingWriter.SetFloat("shadowMaxDistance", config.ShadowMaxDistance);
		lightingWriter.SetFloat("nearPlane", config.NearPlane);
		lightingWriter.SetFloat("farPlane", config.FarPlane);
		lightingWriter.SetUInt("framebufferSizeX", (uint)Math.Max(config.DispatchSize.X, 1));
		lightingWriter.SetUInt("framebufferSizeY", (uint)Math.Max(config.DispatchSize.Y, 1));
		commandList.SetComputeConstants(lightingWriter.RegisterIndex, lightingWriter.AsBytes());
		commandList.SetComputeBuffer(3, config.PointLightBuffer);
		commandList.SetComputeBuffer(4, config.ClusterHeaderBuffer);
		commandList.SetComputeBuffer(5, config.ClusterLightIndexBuffer);

		// Dispatch the compute shader
		var threadGroupSize = _threadGroupSize
			?? throw new InvalidOperationException("Deferred lighting threadgroup size was not initialized.");
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
					$"DeferredLightingPass is already compiled for backend '{_compiledBackendKind.Value}', " +
					$"but was requested for '{device.BackendKind}'.");
			}

			return _pipeline;
		}

		EnsureReflectionWriters(device.BackendKind);

		var pipelineKey = new PipelineKey(
			PassKind.Compute,
			vertexEntryPoint: null,
			pixelEntryPoint: null,
			computeEntryPoint: "DeferredLightingCS",
			renderTargets: new RenderTargetFormats(Array.Empty<TextureFormat>()),
			depthStencil: new DepthStencilFormat(TextureFormat.Unknown),
			renderState: default,
			shaderVariant: "deferred_lighting.compute.slang");

		var shaderSet = new ShaderBytecodeSet(compute: _computeShader, computeThreadGroupSize: _threadGroupSize);
		_pipeline = device.GetOrCreatePipeline(pipelineKey, shaderSet);
		return _pipeline;
	}

	private void EnsureReflectionWriters(GraphicsBackendKind backendKind)
	{
		if (_compiledBackendKind.HasValue &&
		    _compiledBackendKind.Value == backendKind &&
		    _computeShader.IsEmpty == false &&
		    _threadGroupSize.HasValue &&
		    _bindlessWriter is not null &&
		    _cameraWriter is not null &&
		    _lightingWriter is not null)
		{
			return;
		}

		var compiled = _shaderCompiler.GetComputeShaderWithReflection(
			EngineShaderPrograms.DeferredLighting,
			"DeferredLightingCS",
			backendKind);

		_computeShader = compiled.Bytecode;
		_threadGroupSize = compiled.ThreadGroupSize;
		var reflection = compiled.ReflectionLayout;
		_bindlessWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("BindlessHandles"));
		_cameraWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("CameraParams"));
		_lightingWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("LightingParams"));
		_compiledBackendKind = backendKind;
	}
}

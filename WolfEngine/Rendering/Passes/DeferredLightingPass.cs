
using System.Numerics;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

/// <summary>
/// API-agnostic deferred lighting compute pass that shades the G-buffer into the output texture.
/// </summary>
public sealed class DeferredLightingPass
{
	private readonly IShaderCompiler _shaderCompiler;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private IGfxPipeline _pipeline;
	private ReadOnlyMemory<byte> _computeShader;
	private GraphicsBackendKind? _compiledBackendKind;
	private ShaderPropertyWriter? _bindlessWriter;
	private ShaderPropertyWriter? _cameraWriter;
	private ShaderPropertyWriter? _lightingWriter;
	private DescriptorHandle _linearSampler = DescriptorHandle.Invalid;
	private DescriptorHandle _shadowSampler = DescriptorHandle.Invalid;
	private const int MaxLights = 3;

	public DeferredLightingPass(IShaderCompiler shaderCompiler, BindlessResourceRegistry bindlessRegistry)
	{
		_shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
	}

	public DeferredLightingPassConfig BuildConfig(
		RenderGraphContext context,
		RenderGraphFrameResources resources,
		IGfxDevice device,
		ShadowFrameData shadowData)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(device);

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

		return new DeferredLightingPassConfig
		{
			Pipeline = pipeline,
			GBufferAlbedo = _bindlessRegistry.GetTextureHandle(albedo),
			GBufferNormal = _bindlessRegistry.GetTextureHandle(normal),
			GBufferMaterial = _bindlessRegistry.GetTextureHandle(material),
			GBufferEmissive = _bindlessRegistry.GetTextureHandle(emissive),
			GBufferDepth = _bindlessRegistry.GetTextureHandle(depth),
			AmbientOcclusion = ambientOcclusionHandle,
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
			ShadowViewProjection0 = shadowData.CascadeViewProjection0,
			ShadowViewProjection1 = shadowData.CascadeViewProjection1,
			ShadowViewProjection2 = shadowData.CascadeViewProjection2,
			ShadowSplit0 = shadowData.CascadeSplit0,
			ShadowSplit1 = shadowData.CascadeSplit1,
			ShadowSplit2 = shadowData.CascadeSplit2,
			ShadowCascadeBlendDistance = shadowData.CascadeBlendDistance,
			ShadowedDirectionalLightIndex = shadowData.ShadowedDirectionalLightIndex,
			ShadowDepthBias = shadowData.DepthBias,
			ShadowStrength = shadowData.Strength,
			ShadowsEnabled = shadowData.Enabled,
			ShadowTexelSizeX = 1.0f / shadowResolution,
			ShadowTexelSizeY = 1.0f / shadowResolution,
			AoEnabled = resources.AmbientOcclusionFinal.IsValid,
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
		commandList.SetComputeConstants(cameraWriter.RegisterIndex, cameraWriter.AsBytes());

		var lightingWriter = _lightingWriter
			?? throw new InvalidOperationException("Deferred lighting lighting reflection writer was not initialized.");
		lightingWriter.Clear();
		var lightCountInt = Math.Min(sceneData.Lights.Count, MaxLights);
		lightingWriter.SetUInt("lightCount", (uint)lightCountInt);
		for (var i = 0; i < lightCountInt; i++)
		{
			var packet = sceneData.Lights[i];
			var light = packet.Light;

			var forward = Vector3.TransformNormal(Vector3.UnitZ, packet.Transform);
			if (forward == Vector3.Zero)
			{
				forward = new Vector3(0, -1, 0);
			}

			var position = packet.Transform.Translation;

			lightingWriter.SetColorRGBA($"lights[{i}].colorIntensity", new ColorRGBA(light.Color.R, light.Color.G, light.Color.B, light.Intensity));
			lightingWriter.SetVector4($"lights[{i}].directionType", new Vector4(forward, (float)light.Type));
			lightingWriter.SetVector4($"lights[{i}].positionRange", new Vector4(position, 25.0f)); // TODO: light range from component when available
		}

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
				config.ShadowDepthBias,
				config.ShadowStrength));
		lightingWriter.SetUInt(
			"shadowLightIndex",
			config.ShadowsEnabled ? (uint)Math.Max(config.ShadowedDirectionalLightIndex, 0) : 0u);
		lightingWriter.SetUInt("shadowsEnabled", config.ShadowsEnabled ? 1u : 0u);
		lightingWriter.SetUInt("aoEnabled", config.AoEnabled ? 1u : 0u);
		lightingWriter.SetFloat("shadowMaxDistance", ShadowMapPass.MaxShadowDistance);
		commandList.SetComputeConstants(lightingWriter.RegisterIndex, lightingWriter.AsBytes());

		// Dispatch the compute shader
		var dispatchX = (uint)((config.DispatchSize.X + 7) / 8);
		var dispatchY = (uint)((config.DispatchSize.Y + 7) / 8);
		commandList.Dispatch(dispatchX, dispatchY, 1);
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
			computeEntryPoint: "CSMain",
			renderTargets: new RenderTargetFormats(Array.Empty<TextureFormat>()),
			depthStencil: new DepthStencilFormat(TextureFormat.Unknown),
			renderState: default,
			shaderVariant: "deferred_lighting.compute.slang");

		var shaderSet = new ShaderBytecodeSet(compute: _computeShader);
		_pipeline = device.GetOrCreatePipeline(pipelineKey, shaderSet);
		return _pipeline;
	}

	private void EnsureReflectionWriters(GraphicsBackendKind backendKind)
	{
		if (_compiledBackendKind.HasValue &&
		    _compiledBackendKind.Value == backendKind &&
		    _computeShader.IsEmpty == false &&
		    _bindlessWriter is not null &&
		    _cameraWriter is not null &&
		    _lightingWriter is not null)
		{
			return;
		}

		var compiled = _shaderCompiler.GetComputeShaderWithReflection(
			"deferred_lighting.compute.slang",
			"CSMain",
			backendKind);

		_computeShader = compiled.Bytecode;
		var reflection = compiled.ReflectionLayout;
		_bindlessWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("BindlessHandles"));
		_cameraWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("CameraParams"));
		_lightingWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("LightingParams"));
		_compiledBackendKind = backendKind;
	}
}

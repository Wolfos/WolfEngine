using System;
using System.Collections.Generic;
using System.Numerics;
using WolfEngine.Profiling;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

/// <summary>
/// API-agnostic forward transparent graphics pass that alpha-blends transparent materials over the lit scene.
/// </summary>
public sealed class TransparentForwardPass
{
	private readonly IShaderCompiler _shaderCompiler;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private readonly Dictionary<GpuDrawExecutionKey, IGfxPipeline> _pipelinesByExecutionKey = new();
	private DescriptorHandle _linearSampler = DescriptorHandle.Invalid;
	private DescriptorHandle _shadowSampler = DescriptorHandle.Invalid;
	private GraphicsBackendKind? _reflectionBackendKind;
	private ShaderPropertyWriter? _environmentWriter;
	private ShaderPropertyWriter? _lightingWriter;
	private uint _cameraRegisterIndex = 2;

	public TransparentForwardPass(IShaderCompiler shaderCompiler, BindlessResourceRegistry bindlessRegistry)
	{
		_shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
	}

	public TransparentForwardPassConfig BuildConfig(
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
		EnsureReflectionWriters(device.BackendKind);

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

		var lighting = context.GetTexture(resources.ResolvedSceneColor);
		var depth = context.GetTexture(resources.GBufferDepth);
		var shadowMap0 = context.GetTexture(resources.ShadowMapDepth0);
		var shadowMap1 = context.GetTexture(resources.ShadowMapDepth1);
		var shadowMap2 = context.GetTexture(resources.ShadowMapDepth2);
		var shadowResolution = Math.Max(1, shadowData.MapResolution);

		var buckets = BuildBuckets(device, gpuDrawResources);

		return new TransparentForwardPassConfig
		{
			FramebufferWidth = resources.SceneFramebufferSize.X,
			FramebufferHeight = resources.SceneFramebufferSize.Y,
			LightingTarget = lighting,
			DepthTarget = depth,
			ShadowMapDepth0 = shadowMap0,
			ShadowMapDepth1 = shadowMap1,
			ShadowMapDepth2 = shadowMap2,
			SkyboxEnvironment = ResolveSkyboxHandle(context, resources.SkyboxEnvironment),
			SkyboxIrradiance = ResolveSkyboxHandle(context, resources.SkyboxIrradiance),
			SkyboxPrefilter = ResolveSkyboxHandle(context, resources.SkyboxPrefilter),
			SkyboxBrdfLut = ResolveSkyboxHandle(context, resources.SkyboxBrdfLut),
			LinearSampler = _linearSampler,
			ShadowMapHandle0 = _bindlessRegistry.RegisterTexture(shadowMap0),
			ShadowMapHandle1 = _bindlessRegistry.RegisterTexture(shadowMap1),
			ShadowMapHandle2 = _bindlessRegistry.RegisterTexture(shadowMap2),
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
			NearPlane = sceneData.NearPlane,
			FarPlane = sceneData.FarPlane,
			InstanceBuffer = gpuDrawResources.InstanceBuffer,
			MaterialBuffer = gpuDrawResources.MaterialBuffer,
			DrawArgsBuffer = gpuDrawResources.DrawArgsBuffer,
			CameraBuffer = gpuDrawResources.CameraBuffer,
			TransparentEnvironmentBuffer = gpuDrawResources.TransparentEnvironmentBuffer,
			TransparentLightingBuffer = gpuDrawResources.TransparentLightingBuffer,
			PointLightBuffer = gpuDrawResources.ClusterPointLightBuffer,
			ClusterHeaderBuffer = gpuDrawResources.ClusterHeaderBuffer,
			ClusterLightIndexBuffer = gpuDrawResources.ClusterLightIndexBuffer,
			MaterialGenerationBuffer = gpuDrawResources.MaterialGenerationBuffer,
			VisibleDrawIdsPerExecutionLaneBuffer = gpuDrawResources.VisibleDrawIdsPerExecutionLaneBuffer,
			DrawExecutionRangePerBucketBuffer = gpuDrawResources.DrawExecutionRangePerBucketBuffer,
			Buckets = buckets.ToArray(),
			FallbackMaxCommandCount = gpuDrawResources.ActiveDrawCommandUpperBound
		};
	}

	public void Record(RenderGraphContext context, in TransparentForwardPassConfig config, SceneDrawData sceneData)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(sceneData);

		var commandList = context.CommandList;
		var targets = new PassTargets(
			new[] { new ColorTargetBinding(config.LightingTarget) },
			new DepthTargetBinding(config.DepthTarget));
		var viewport = new Viewport(0.0f, 0.0f, config.FramebufferWidth, config.FramebufferHeight);
		commandList.BeginPass(targets, viewport);
		commandList.SetScissorRect(new RectInt(0, 0, config.FramebufferWidth, config.FramebufferHeight));

		if (config.InstanceBuffer is null ||
		    config.MaterialBuffer is null ||
		    config.DrawArgsBuffer is null ||
		    config.CameraBuffer is null)
		{
			commandList.EndPass();
			return;
		}

		var environmentWriter = _environmentWriter
			?? throw new InvalidOperationException("Transparent environment writer was not initialized.");
		environmentWriter.Clear();
		environmentWriter.SetUInt("environmentHandle", config.SkyboxEnvironment.Value);
		environmentWriter.SetUInt("irradianceHandle", config.SkyboxIrradiance.Value);
		environmentWriter.SetUInt("prefilteredHandle", config.SkyboxPrefilter.Value);
		environmentWriter.SetUInt("brdfLutHandle", config.SkyboxBrdfLut.Value);
		environmentWriter.SetUInt("samplerHandle", config.LinearSampler.Value);
		environmentWriter.SetUInt("shadowMapHandle0", config.ShadowMapHandle0.Value);
		environmentWriter.SetUInt("shadowMapHandle1", config.ShadowMapHandle1.Value);
		environmentWriter.SetUInt("shadowMapHandle2", config.ShadowMapHandle2.Value);
		environmentWriter.SetUInt("shadowSamplerHandle", config.ShadowSampler.Value);

		var lightingWriter = _lightingWriter
			?? throw new InvalidOperationException("Transparent lighting writer was not initialized.");
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
				config.ShadowDepthBias,
				config.ShadowStrength));
		lightingWriter.SetUInt(
			"shadowLightIndex",
			config.ShadowsEnabled ? (uint)Math.Max(config.ShadowedDirectionalLightIndex, 0) : 0u);
		lightingWriter.SetUInt("shadowsEnabled", config.ShadowsEnabled ? 1u : 0u);
		lightingWriter.SetFloat("shadowMaxDistance", ShadowMapPass.MaxShadowDistance);
		
		if (config.TransparentEnvironmentBuffer is not IWritableGpuBuffer writableEnvironmentBuffer ||
		    config.TransparentLightingBuffer is not IWritableGpuBuffer writableLightingBuffer)
		{
			commandList.EndPass();
			return;
		}

		writableEnvironmentBuffer.Write<byte>(environmentWriter.AsBytes());
		writableLightingBuffer.Write<byte>(lightingWriter.AsBytes());


		var buckets = config.Buckets.Span;
		if (buckets.Length == 0)
		{
			commandList.EndPass();
			return;
		}

		var fallbackCount = config.FallbackMaxCommandCount == 0
			? (uint)GpuDrawResources.MaxDrawCount
			: config.FallbackMaxCommandCount;
		commandList.SetPrimitiveTopology(PrimitiveTopology.TriangleList);

		for (var i = 0; i < buckets.Length; i++)
		{
			var bucket = buckets[i];
			using (FrameProfiler.Instance.Measure(bucket.DebugName))
			{
				commandList.BindPipeline(bucket.Pipeline);
				commandList.BindConstantBuffer(10, config.InstanceBuffer);
				commandList.BindConstantBuffer(11, config.MaterialBuffer);
				commandList.BindConstantBuffer(12, config.DrawArgsBuffer);
				if (config.MaterialGenerationBuffer is not null)
				{
					commandList.BindConstantBuffer(13, config.MaterialGenerationBuffer);
				}
				if (config.PointLightBuffer is not null)
				{
					commandList.BindConstantBuffer(14, config.PointLightBuffer);
				}
				if (config.ClusterHeaderBuffer is not null)
				{
					commandList.BindConstantBuffer(15, config.ClusterHeaderBuffer);
				}
				if (config.ClusterLightIndexBuffer is not null)
				{
					commandList.BindConstantBuffer(16, config.ClusterLightIndexBuffer);
				}

				commandList.BindConstantBuffer(_cameraRegisterIndex, config.CameraBuffer);
				if (config.VisibleDrawIdsPerExecutionLaneBuffer is not null &&
				    config.DrawExecutionRangePerBucketBuffer is not null)
				{
					var indicesOffsetBytes = (ulong)(bucket.ExecutionIndex * GpuDrawResources.MaxDrawCount * sizeof(uint));
					var rangeOffsetBytes = (ulong)(bucket.ExecutionIndex * 2 * sizeof(uint));
					commandList.ExecuteIndirectCommandBufferIndexed(
						bucket.IndirectCommandBuffer,
						config.VisibleDrawIdsPerExecutionLaneBuffer,
						indicesOffsetBytes,
						config.DrawExecutionRangePerBucketBuffer,
						rangeOffsetBytes);
				}
				else
				{
					commandList.ExecuteIndirectCommandBuffer(bucket.IndirectCommandBuffer, fallbackCount);
				}
			}
		}

		commandList.EndPass();
	}

	private List<TransparentExecutionBucket> BuildBuckets(IGfxDevice device, GpuDrawResources gpuDrawResources)
	{
		var laneDefinitions = GpuDrawExecutionLanes.GetDefinitionsForPass(DrawPassParticipation.ForwardTransparent);
		var buckets = new List<TransparentExecutionBucket>(laneDefinitions.Length);
		var activeIndirectSlot = gpuDrawResources.ActiveIndirectCommandSlot;
		for (var i = 0; i < laneDefinitions.Length; i++)
		{
			var laneDefinition = laneDefinitions[i];
			var indirectCommandBuffer = gpuDrawResources.GetIndirectCommandBufferSlot(activeIndirectSlot, laneDefinition);
			if (indirectCommandBuffer is null)
			{
				continue;
			}

			var pipeline = EnsurePipeline(device, laneDefinition);
			buckets.Add(new TransparentExecutionBucket(
				laneDefinition.DrawKind,
				laneDefinition.BucketId,
				laneDefinition.ExecutionIndex,
				laneDefinition.DebugName,
				pipeline,
				indirectCommandBuffer));
		}

		return buckets;
	}

	private IGfxPipeline EnsurePipeline(IGfxDevice device, in GpuDrawExecutionLaneDefinition lane)
	{
		if (_pipelinesByExecutionKey.TryGetValue(lane.Key, out var pipeline))
		{
			return pipeline;
		}

		var renderState = new RenderStateDescriptor(
			FillMode.Solid,
			CullMode.Back,
			depthTestEnabled: true,
			depthWriteEnabled: false,
			BlendMode.AlphaBlend);

		var key = new PipelineKey(
			PassKind.Graphics,
			vertexEntryPoint: "vertexShader",
			pixelEntryPoint: "fragmentShader",
			computeEntryPoint: null,
			renderTargets: new(new[] { TextureFormat.Rgba16Float }),
			depthStencil: new DepthStencilFormat(TextureFormat.D32Float),
			renderState: renderState,
			layout: GraphicsLayoutKind.Material,
			shaderVariant: $"Transparent:{lane.ShaderVariant}");

		var shaders = GraphicsShaderCompiler.Compile(
			_shaderCompiler,
			device.BackendKind,
			GetShaderPath(lane.DrawKind),
			"vertexShader",
			"fragmentShader",
			lane.PreprocessorDefine);
		pipeline = device.GetOrCreatePipeline(key, shaders);
		_pipelinesByExecutionKey[lane.Key] = pipeline;
		return pipeline;
	}

	private static string GetShaderPath(GpuDrawKind drawKind) => drawKind switch
	{
		GpuDrawKind.Mesh => "transparent_forward.slang",
		GpuDrawKind.DebugPrimitive => "debug_primitive_forward.slang",
		_ => throw new NotSupportedException($"Transparent shared draw kind '{drawKind}' does not define a shader.")
	};

	private void EnsureReflectionWriters(GraphicsBackendKind backendKind)
	{
		if (_reflectionBackendKind.HasValue &&
		    _reflectionBackendKind.Value == backendKind &&
		    _environmentWriter is not null &&
		    _lightingWriter is not null)
		{
			return;
		}

		var compiled = GraphicsShaderCompiler.CompileWithReflection(
			_shaderCompiler,
			backendKind,
			"transparent_forward.slang",
			"vertexShader",
			"fragmentShader");
		var reflection = compiled.ReflectionLayout;
		_environmentWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("TransparentEnvironmentParams"));
		var cameraLayout = reflection.GetConstantBuffer("CameraParams");
		_cameraRegisterIndex = cameraLayout.RegisterIndex;
		_lightingWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("LightingParams"));
		_reflectionBackendKind = backendKind;
	}

	private DescriptorHandle ResolveSkyboxHandle(RenderGraphContext context, RenderGraphResourceHandle handle)
	{
		if (handle.IsValid == false)
		{
			return _bindlessRegistry.ErrorTextureHandle;
		}

		var texture = context.GetTexture(handle);
		return _bindlessRegistry.GetTextureHandle(texture);
	}

}

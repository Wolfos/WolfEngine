using System.Numerics;
using System.Runtime.InteropServices;
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
	private readonly Dictionary<int, IGfxPipeline> _pipelinesByBucketIndex = new();
	private DescriptorHandle _linearSampler = DescriptorHandle.Invalid;
	private DescriptorHandle _shadowSampler = DescriptorHandle.Invalid;
	private const int MaxLights = 3;
	private const int LightingConstantsCount = 100;

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
		ShadowFrameData shadowData)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(device);
		ArgumentNullException.ThrowIfNull(gpuDrawResources);

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

		var lighting = context.GetTexture(resources.LightingBuffer);
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
			InstanceBuffer = gpuDrawResources.InstanceBuffer,
			MaterialBuffer = gpuDrawResources.MaterialBuffer,
			DrawArgsBuffer = gpuDrawResources.DrawArgsBuffer,
			CameraBuffer = gpuDrawResources.CameraBuffer,
			TransparentEnvironmentBuffer = gpuDrawResources.TransparentEnvironmentBuffer,
			TransparentLightingBuffer = gpuDrawResources.TransparentLightingBuffer,
			MaterialGenerationBuffer = gpuDrawResources.MaterialGenerationBuffer,
			VisibleDrawIdsPerBucketBuffer = gpuDrawResources.VisibleDrawIdsPerBucketBuffer,
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

		Span<uint> textureHandles = stackalloc uint[12];
		textureHandles[0] = config.SkyboxEnvironment.Value;
		textureHandles[1] = config.SkyboxIrradiance.Value;
		textureHandles[2] = config.SkyboxPrefilter.Value;
		textureHandles[3] = config.SkyboxBrdfLut.Value;
		textureHandles[4] = config.LinearSampler.Value;
		textureHandles[5] = config.ShadowMapHandle0.Value;
		textureHandles[6] = config.ShadowMapHandle1.Value;
		textureHandles[7] = config.ShadowMapHandle2.Value;
		textureHandles[8] = config.ShadowSampler.Value;
		textureHandles[9] = 0;
		textureHandles[10] = 0;
		textureHandles[11] = 0;

		Span<ShaderLight> shaderLights = stackalloc ShaderLight[MaxLights];
		shaderLights.Clear();
		var lightCountInt = Math.Min(sceneData.Lights.Count, MaxLights);
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

			shaderLights[i] = new ShaderLight
			{
				ColorIntensity = new Vector4(light.Color.X, light.Color.Y, light.Color.Z, light.Intensity),
				DirectionType = new Vector4(forward, (float)light.Type),
				PositionRange = new Vector4(position, 25.0f)
			};
		}

		var lightWords = MemoryMarshal.Cast<ShaderLight, uint>(shaderLights);
		Span<uint> lightingConstants = stackalloc uint[LightingConstantsCount];
		lightingConstants.Clear();
		lightingConstants[0] = (uint)lightCountInt;
		lightWords.CopyTo(lightingConstants.Slice(4));
		WriteMatrix(MemoryMarshal.Cast<uint, float>(lightingConstants.Slice(40, 16)), config.ShadowViewProjection0);
		WriteMatrix(MemoryMarshal.Cast<uint, float>(lightingConstants.Slice(56, 16)), config.ShadowViewProjection1);
		WriteMatrix(MemoryMarshal.Cast<uint, float>(lightingConstants.Slice(72, 16)), config.ShadowViewProjection2);
		lightingConstants[88] = BitConverter.SingleToUInt32Bits(config.ShadowSplit0);
		lightingConstants[89] = BitConverter.SingleToUInt32Bits(config.ShadowSplit1);
		lightingConstants[90] = BitConverter.SingleToUInt32Bits(config.ShadowSplit2);
		lightingConstants[91] = BitConverter.SingleToUInt32Bits(config.ShadowCascadeBlendDistance);
		lightingConstants[92] = BitConverter.SingleToUInt32Bits(config.ShadowTexelSizeX);
		lightingConstants[93] = BitConverter.SingleToUInt32Bits(config.ShadowTexelSizeY);
		lightingConstants[94] = BitConverter.SingleToUInt32Bits(config.ShadowDepthBias);
		lightingConstants[95] = BitConverter.SingleToUInt32Bits(config.ShadowStrength);
		lightingConstants[96] = config.ShadowsEnabled ? (uint)Math.Max(config.ShadowedDirectionalLightIndex, 0) : 0;
		lightingConstants[97] = config.ShadowsEnabled ? 1u : 0u;
		lightingConstants[98] = BitConverter.SingleToUInt32Bits(ShadowMapPass.MaxShadowDistance);
		lightingConstants[99] = 0;
		
		if (config.TransparentEnvironmentBuffer is not IWritableGpuBuffer writableEnvironmentBuffer ||
		    config.TransparentLightingBuffer is not IWritableGpuBuffer writableLightingBuffer)
		{
			commandList.EndPass();
			return;
		}

		writableEnvironmentBuffer.Write<uint>(textureHandles);
		writableLightingBuffer.Write<uint>(lightingConstants);


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

				commandList.BindConstantBuffer(2, config.CameraBuffer);
				if (config.VisibleDrawIdsPerBucketBuffer is not null &&
				    config.DrawExecutionRangePerBucketBuffer is not null)
				{
					var indicesOffsetBytes = (ulong)(bucket.BucketIndex * GpuDrawResources.MaxDrawCount * sizeof(uint));
					var rangeOffsetBytes = (ulong)(bucket.BucketIndex * 2 * sizeof(uint));
					commandList.ExecuteIndirectCommandBufferIndexed(
						bucket.IndirectCommandBuffer,
						config.VisibleDrawIdsPerBucketBuffer,
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
		var bucketDefinitions = GBufferDrawBuckets.Definitions;
		var buckets = new List<TransparentExecutionBucket>(bucketDefinitions.Length);
		var activeIndirectSlot = gpuDrawResources.ActiveIndirectCommandSlot;
		for (var i = 0; i < bucketDefinitions.Length; i++)
		{
			var bucketDefinition = bucketDefinitions[i];
			if (bucketDefinition.SupportsPass(DrawPassParticipation.ForwardTransparent) == false)
			{
				continue;
			}

			var indirectCommandBuffer = gpuDrawResources.GetIndirectCommandBufferSlot(activeIndirectSlot, i);
			if (indirectCommandBuffer is null)
			{
				continue;
			}

			var pipeline = EnsurePipeline(device, bucketDefinition, i);
			buckets.Add(new TransparentExecutionBucket(
				i,
				bucketDefinition.DebugName,
				pipeline,
				indirectCommandBuffer));
		}

		return buckets;
	}

	private IGfxPipeline EnsurePipeline(IGfxDevice device, in GBufferDrawBucketDefinition bucket, int bucketIndex)
	{
		if (_pipelinesByBucketIndex.TryGetValue(bucketIndex, out var pipeline))
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
			renderTargets: new(new[] { TextureFormat.Bgra8Unorm }),
			depthStencil: new DepthStencilFormat(TextureFormat.D32Float),
			renderState: renderState,
			layout: GraphicsLayoutKind.Material,
			shaderVariant: $"Transparent:{bucket.ShaderVariant}");

		var shaders = GraphicsShaderCompiler.Compile(
			_shaderCompiler,
			device.BackendKind,
			"transparent_forward.slang",
			"vertexShader",
			"fragmentShader",
			bucket.PreprocessorDefine);
		pipeline = device.GetOrCreatePipeline(key, shaders);
		_pipelinesByBucketIndex[bucketIndex] = pipeline;
		return pipeline;
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

	private static void WriteMatrix(Span<float> destination, Matrix4x4 matrix)
	{
		if (destination.Length < 16)
		{
			throw new ArgumentException("Destination span must contain at least 16 elements.", nameof(destination));
		}

		destination[0] = matrix.M11;
		destination[1] = matrix.M12;
		destination[2] = matrix.M13;
		destination[3] = matrix.M14;
		destination[4] = matrix.M21;
		destination[5] = matrix.M22;
		destination[6] = matrix.M23;
		destination[7] = matrix.M24;
		destination[8] = matrix.M31;
		destination[9] = matrix.M32;
		destination[10] = matrix.M33;
		destination[11] = matrix.M34;
		destination[12] = matrix.M41;
		destination[13] = matrix.M42;
		destination[14] = matrix.M43;
		destination[15] = matrix.M44;
	}

	[StructLayout(LayoutKind.Sequential, Pack = 4)]
	private struct ShaderLight
	{
		public Vector4 ColorIntensity;
		public Vector4 DirectionType;
		public Vector4 PositionRange;
	}
}

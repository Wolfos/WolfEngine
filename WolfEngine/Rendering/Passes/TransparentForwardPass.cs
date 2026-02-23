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
	private const int MaxLights = 3;

	public TransparentForwardPass(IShaderCompiler shaderCompiler, BindlessResourceRegistry bindlessRegistry)
	{
		_shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
	}

	public TransparentForwardPassConfig BuildConfig(
		RenderGraphContext context,
		RenderGraphFrameResources resources,
		IGfxDevice device,
		GpuDrawResources gpuDrawResources)
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

		var lighting = context.GetTexture(resources.LightingBuffer);
		var depth = context.GetTexture(resources.GBufferDepth);

		var buckets = BuildBuckets(device, gpuDrawResources);

		return new TransparentForwardPassConfig
		{
			FramebufferWidth = resources.FramebufferSize.X,
			FramebufferHeight = resources.FramebufferSize.Y,
			LightingTarget = lighting,
			DepthTarget = depth,
			SkyboxEnvironment = ResolveSkyboxHandle(context, resources.SkyboxEnvironment),
			SkyboxIrradiance = ResolveSkyboxHandle(context, resources.SkyboxIrradiance),
			SkyboxPrefilter = ResolveSkyboxHandle(context, resources.SkyboxPrefilter),
			SkyboxBrdfLut = ResolveSkyboxHandle(context, resources.SkyboxBrdfLut),
			LinearSampler = _linearSampler,
			InstanceBuffer = gpuDrawResources.InstanceBuffer,
			MaterialBuffer = gpuDrawResources.MaterialBuffer,
			DrawArgsBuffer = gpuDrawResources.DrawArgsBuffer,
			CameraBuffer = gpuDrawResources.CameraBuffer,
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

		commandList.SetPrimitiveTopology(PrimitiveTopology.TriangleList);
		commandList.BindConstantBuffer(10, config.InstanceBuffer);
		commandList.BindConstantBuffer(11, config.MaterialBuffer);
		commandList.BindConstantBuffer(12, config.DrawArgsBuffer);
		if (config.MaterialGenerationBuffer is not null)
		{
			commandList.BindConstantBuffer(13, config.MaterialGenerationBuffer);
		}
		commandList.BindConstantBuffer(2, config.CameraBuffer);

		Span<uint> textureHandles = stackalloc uint[8];
		textureHandles[0] = config.SkyboxEnvironment.Value;
		textureHandles[1] = config.SkyboxIrradiance.Value;
		textureHandles[2] = config.SkyboxPrefilter.Value;
		textureHandles[3] = config.SkyboxBrdfLut.Value;
		textureHandles[4] = config.LinearSampler.Value;
		textureHandles[5] = 0;
		textureHandles[6] = 0;
		textureHandles[7] = 0;
		commandList.SetGraphicsConstants(0, MemoryMarshal.AsBytes(textureHandles));

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

		var lightBytes = MemoryMarshal.AsBytes(shaderLights);
		const int headerSize = 32;
		var lightingConstantsSize = headerSize + lightBytes.Length;
		Span<byte> lightingConstants = stackalloc byte[lightingConstantsSize];
		lightingConstants.Clear();
		var lightCount = (uint)lightCountInt;
		MemoryMarshal.Write(lightingConstants, ref lightCount);
		lightBytes.CopyTo(lightingConstants.Slice(headerSize));
		commandList.SetGraphicsConstants(3, lightingConstants);

		var buckets = config.Buckets.Span;
		if (buckets.Length == 0)
		{
			commandList.EndPass();
			return;
		}

		var fallbackCount = config.FallbackMaxCommandCount == 0
			? (uint)GpuDrawResources.MaxDrawCount
			: config.FallbackMaxCommandCount;
		if (commandList.BackendKind != GraphicsBackendKind.Metal)
		{
			var fallbackBucket = buckets[0];
			commandList.BindPipeline(fallbackBucket.Pipeline);
			commandList.ExecuteIndirectCommandBuffer(fallbackBucket.IndirectCommandBuffer, fallbackCount);
			commandList.EndPass();
			return;
		}

		for (var i = 0; i < buckets.Length; i++)
		{
			var bucket = buckets[i];
			using (FrameProfiler.Instance.Measure(bucket.DebugName))
			{
				commandList.BindPipeline(bucket.Pipeline);
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

	[StructLayout(LayoutKind.Sequential, Pack = 4)]
	private struct ShaderLight
	{
		public Vector4 ColorIntensity;
		public Vector4 DirectionType;
		public Vector4 PositionRange;
	}
}

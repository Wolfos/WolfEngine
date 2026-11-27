using System.Numerics;
using WolfEngine.Profiling;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;
using WolfEngine.Rendering.Atmosphere;
using WolfEngine.Rendering.Clouds;

namespace WolfEngine.Rendering.Passes;

/// <summary>
/// API-agnostic forward transparent graphics pass that alpha-blends transparent materials over the lit scene.
/// </summary>
public sealed class TransparentForwardPass
{
	private static readonly RenderTargetFormats LaneRenderTargets = new(new[] { TextureFormat.Rgba16Float, TextureFormat.Rgba8Unorm });

	private readonly ShaderSource _shaderSource;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	// One per lane, built on demand. Its generation is the lanes' binding generation, so records encoded against
	// replaced pipelines re-encode.
	private readonly ShaderVariantSet<GpuDrawExecutionKey, TransparentLane> _lanes;
	// The transparent and debug primitive shaders the pass's shared constant buffers are reflected from.
	private readonly GraphicsShader _environmentShader;
	private readonly GraphicsShader _debugShader;
	private readonly GpuDrawResources _gpuDrawResources;
	private readonly PerViewIndirectCommandSets _indirectCommandSets;
	private DescriptorHandle _linearSampler = DescriptorHandle.Invalid;
	private DescriptorHandle _shadowSampler = DescriptorHandle.Invalid;

	public TransparentForwardPass(
		ShaderSource shaderSource,
		BindlessResourceRegistry bindlessRegistry,
		IndirectDrawRecords records,
		GpuDrawResources gpuDrawResources)
	{
		_shaderSource = shaderSource ?? throw new ArgumentNullException(nameof(shaderSource));
		_lanes = new ShaderVariantSet<GpuDrawExecutionKey, TransparentLane>(shaderSource, BuildLane);
		_environmentShader = new GraphicsShader(shaderSource, EngineShaderPrograms.TransparentForward, "vertexShader", "fragmentShader");
		_debugShader = new GraphicsShader(shaderSource, EngineShaderPrograms.DebugPrimitiveForward, "vertexShader", "fragmentShader");
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
		_gpuDrawResources = gpuDrawResources ?? throw new ArgumentNullException(nameof(gpuDrawResources));
		_indirectCommandSets = new PerViewIndirectCommandSets(
			records,
			DrawPassParticipation.ForwardTransparent,
			new LaneBindings(this));
	}

	/// <summary>The given view's command set; see <see cref="PerViewIndirectCommandSets"/>.</summary>
	public DrawRecordSet GetIndirectCommandSet(int viewIndex) => _indirectCommandSets.Get(viewIndex);

	/// <summary>Releases and retires a destroyed view's command set.</summary>
	internal void ReleaseViewCommandSets(int viewIndex, IGfxDevice device) =>
		_indirectCommandSets.ReleaseView(viewIndex, device);

	public void EnsureIndirectResources(IGfxDevice device, int viewIndex)
	{
		ArgumentNullException.ThrowIfNull(device);
		var laneDefinitions = GpuDrawExecutionLanes.GetDefinitionsForPass(DrawPassParticipation.ForwardTransparent);
		for (var i = 0; i < laneDefinitions.Length; i++)
		{
			_lanes.Get(laneDefinitions[i].Key, device);
		}

		_indirectCommandSets.Get(viewIndex).EnsureCreated(device);
	}

	public bool HasIndirectLane(GpuDrawExecutionLaneDefinition lane) => _lanes.TryGet(lane.Key, out _);

	private IGfxPipeline? GetPipeline(GpuDrawExecutionLaneDefinition lane) =>
		_lanes.TryGet(lane.Key, out var variant) ? variant.Pipeline : null;

	private SharedDrawGraphicsBufferBindings? GetBufferBindings(GpuDrawExecutionLaneDefinition lane) =>
		_lanes.TryGet(lane.Key, out var variant) ? variant.BufferBindings : null;

	private GraphicsPassBindingSet? GetPassBindingSet(GpuDrawExecutionLaneDefinition lane)
	{
		if (_lanes.TryGet(lane.Key, out var variant) == false)
			return null;
		return GraphicsPassBindingSet.FromReflection(variant.Reflection,
			new Dictionary<string, IGfxBuffer?>(StringComparer.Ordinal)
			{
				["CameraParams"] = _gpuDrawResources.CameraBuffer,
				["TransparentEnvironmentParams"] = _gpuDrawResources.TransparentEnvironmentBuffer,
				["LightingParams"] = _gpuDrawResources.TransparentLightingBuffer,
				["DdgiDebugParams"] = _gpuDrawResources.DdgiDebugBuffer,
				["g_PointLights"] = _gpuDrawResources.ClusterPointLightBuffer,
				["g_ClusterHeaders"] = _gpuDrawResources.ClusterHeaderBuffer,
				["g_ClusterLightIndices"] = _gpuDrawResources.ClusterLightIndexBuffer
			},
			SharedDrawPerDrawBindings.ResourceNames);
	}

	public TransparentForwardPassConfig BuildConfig(
		RenderGraphContext context,
		RenderViewResources resources,
		RenderFrameSharedResources sharedResources,
		IGfxDevice device,
		GpuDrawResources gpuDrawResources,
		ShadowFrameData shadowData,
		SceneDrawData sceneData)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(device);
		ArgumentNullException.ThrowIfNull(gpuDrawResources);
		ArgumentNullException.ThrowIfNull(sceneData);
		_environmentShader.EnsureCurrent(device);
		_debugShader.EnsureCurrent(device);

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
		var reactiveMask = context.GetTexture(resources.Fsr3.TransparencyMask);
		var depth = context.GetTexture(resources.GBufferDepth);
		var shadowMap0 = context.GetTexture(resources.ShadowMapDepth0);
		var shadowMap1 = context.GetTexture(resources.ShadowMapDepth1);
		var shadowMap2 = context.GetTexture(resources.ShadowMapDepth2);
		var shadowResolution = Math.Max(1, shadowData.MapResolution);
		var ddgiProbeStateAvailable = resources.DdgiProbeStateWrite.IsValid;
		var ddgiProbeState = ddgiProbeStateAvailable
			? _bindlessRegistry.GetTextureHandle(context.GetTexture(resources.DdgiProbeStateWrite))
			: _bindlessRegistry.ErrorTextureHandle;
		var ddgiGridShape = DdgiUtilities.GetGridShape(resources.Config.DiffuseGlobalIllumination);
		var fogIntegrated = resources.FogIntegrated.IsValid
			? _bindlessRegistry.GetTextureHandle(context.GetTexture(resources.FogIntegrated))
			: _bindlessRegistry.ErrorTextureHandle;

		var buckets = BuildBuckets(device, gpuDrawResources);

		return new TransparentForwardPassConfig
		{
			FramebufferWidth = resources.SceneFramebufferSize.X,
			FramebufferHeight = resources.SceneFramebufferSize.Y,
			LightingTarget = lighting,
			ReactiveMaskTarget = reactiveMask,
			DepthTarget = depth,
			ShadowMapDepth0 = shadowMap0,
			ShadowMapDepth1 = shadowMap1,
			ShadowMapDepth2 = shadowMap2,
			SkyboxIrradiance = ResolveSkyboxHandle(context, resources.SkyboxIrradiance),
			Atmosphere = AtmosphereBinding.Resolve(context, _bindlessRegistry, resources.Atmosphere),
			Clouds = CloudCompositeBinding.Resolve(context, _bindlessRegistry, resources.Clouds, includeComposite: false),
			SkyboxPrefilter = ResolveSkyboxHandle(context, resources.SkyboxPrefilter),
			SkyboxBrdfLut = ResolveSkyboxHandle(context, sharedResources.SkyboxBrdfLut),
			LinearSampler = _linearSampler,
			ShadowMapHandle0 = _bindlessRegistry.RegisterTexture(shadowMap0),
			ShadowMapHandle1 = _bindlessRegistry.RegisterTexture(shadowMap1),
			ShadowMapHandle2 = _bindlessRegistry.RegisterTexture(shadowMap2),
			ShadowSampler = _shadowSampler,
			FogIntegrated = fogIntegrated,
			FogEnabled = resources.FogIntegrated.IsValid,
			FogSliceCount = Math.Clamp(resources.Config.VolumetricFog.SliceCount, 16, 128),
			FogMaxDistance = Math.Max(resources.Config.VolumetricFog.MaxDistance, sceneData.NearPlane + 0.001f),
			DdgiProbeState = ddgiProbeState,
			DdgiProbeStateAvailable = ddgiProbeStateAvailable,
			DdgiProbeRelocationEnabled =
				resources.Config.DiffuseGlobalIllumination.ProbeRelocationEnabled,
			DdgiGridShape = ddgiGridShape,
			DdgiStorageOffset = resources.DdgiStorageOffset,
			ShadowViewProjection0 = shadowData.CascadeViewProjection0,
			ShadowViewProjection1 = shadowData.CascadeViewProjection1,
			ShadowViewProjection2 = shadowData.CascadeViewProjection2,
			ShadowSplit0 = shadowData.CascadeSplit0,
			ShadowSplit1 = shadowData.CascadeSplit1,
			ShadowSplit2 = shadowData.CascadeSplit2,
			ShadowCascadeBlendDistance = shadowData.CascadeBlendDistance,
			ShadowedDirectionalLightIndex = shadowData.ShadowedDirectionalLightIndex,
			ShadowDepthBiases = shadowData.DepthBiases,
			ShadowNormalOffsets = shadowData.NormalOffsets,
			ShadowStrength = shadowData.Strength,
			ShadowsEnabled = shadowData.Enabled,
			ShadowMaxDistance = shadowData.MaxDistance,
			ShadowTexelSizeX = 1.0f / shadowResolution,
			ShadowTexelSizeY = 1.0f / shadowResolution,
			NearPlane = sceneData.NearPlane,
			FarPlane = sceneData.FarPlane,
			InstanceBuffer = gpuDrawResources.InstanceBuffer,
			MaterialBuffer = gpuDrawResources.MaterialBuffer,
			DrawArgsBuffer = gpuDrawResources.DrawArgsBuffer,
			VisibleInstanceBuffer = gpuDrawResources.VisibleInstanceBuffer,
			CameraBuffer = gpuDrawResources.CameraBuffer,
			TransparentEnvironmentBuffer = gpuDrawResources.TransparentEnvironmentBuffer,
			TransparentLightingBuffer = gpuDrawResources.TransparentLightingBuffer,
			DdgiDebugBuffer = gpuDrawResources.DdgiDebugBuffer,
			PointLightBuffer = gpuDrawResources.ClusterPointLightBuffer,
			ClusterHeaderBuffer = gpuDrawResources.ClusterHeaderBuffer,
			ClusterLightIndexBuffer = gpuDrawResources.ClusterLightIndexBuffer,
			MaterialGenerationBuffer = gpuDrawResources.MaterialGenerationBuffer,
			DrawExecutionRangePerBucketBuffer = gpuDrawResources.DrawExecutionRangePerBucketBuffer,
			Buckets = buckets.ToArray(),
			FallbackMaxCommandCount = gpuDrawResources.ActiveDrawCommandUpperBound,
			PackedVertexBuffer = gpuDrawResources.PackedMeshVertexBuffer,
			PackedIndexBuffer = gpuDrawResources.PackedMeshIndexBuffer,
			PackedVertexStride = gpuDrawResources.PackedMeshVertexStride
		};
	}

	public void Record(RenderGraphContext context, in TransparentForwardPassConfig config, SceneDrawData sceneData)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(sceneData);

		var commandList = context.CommandList;
		var targets = new PassTargets(
			new[]
			{
				new ColorTargetBinding(config.LightingTarget),
				new ColorTargetBinding(config.ReactiveMaskTarget)
			},
			new DepthTargetBinding(config.DepthTarget));
		var viewport = new Viewport(0.0f, 0.0f, config.FramebufferWidth, config.FramebufferHeight);
		commandList.BeginPass(targets, viewport);
		commandList.ClearColorAttachment(1, new ColorRGBA(0.0f, 0.0f, 0.0f, 0.0f));
		commandList.SetScissorRect(new RectInt(0, 0, config.FramebufferWidth, config.FramebufferHeight));

		if (config.InstanceBuffer is null ||
		    config.MaterialBuffer is null ||
		    config.DrawArgsBuffer is null ||
		    config.VisibleInstanceBuffer is null ||
		    config.CameraBuffer is null)
		{
			commandList.EndPass();
			return;
		}

		var environmentWriter = _environmentShader.Writer("TransparentEnvironmentParams");
		environmentWriter.Clear();
		environmentWriter.SetUInt("irradianceHandle", config.SkyboxIrradiance.Value);
		environmentWriter.SetUInt("prefilteredHandle", config.SkyboxPrefilter.Value);
		environmentWriter.SetUInt("brdfLutHandle", config.SkyboxBrdfLut.Value);
		environmentWriter.SetUInt("samplerHandle", config.LinearSampler.Value);
		environmentWriter.SetUInt("shadowMapHandle0", config.ShadowMapHandle0.Value);
		environmentWriter.SetUInt("shadowMapHandle1", config.ShadowMapHandle1.Value);
		environmentWriter.SetUInt("shadowMapHandle2", config.ShadowMapHandle2.Value);
		environmentWriter.SetUInt("shadowSamplerHandle", config.ShadowSampler.Value);
		environmentWriter.SetUInt("fogIntegratedHandle", config.FogIntegrated.Value);
		environmentWriter.SetUInt("fogEnabled", config.FogEnabled ? 1u : 0u);
		environmentWriter.SetUInt("aerialPerspectiveEnabled", config.Atmosphere.Enabled ? 1u : 0u);
		environmentWriter.SetUInt("aerialPerspectiveHandle", config.Atmosphere.AerialPerspective.Value);
		environmentWriter.SetUInt("fogSliceCount", (uint)config.FogSliceCount);
		environmentWriter.SetFloat("fogMaxDistance", config.FogMaxDistance);
		config.Clouds.Write(environmentWriter);

		var lightingWriter = _environmentShader.Writer("LightingParams");
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

			var colorScale = sceneData.SunLightAttenuation.GetColorScale(light, forward);

			lightingWriter.SetColorRGBA(
				$"directionalLights[{directionalLightCount}].colorIntensity",
				new ColorRGBA(
					light.Color.R * colorScale.X,
					light.Color.G * colorScale.Y,
					light.Color.B * colorScale.Z,
					light.Intensity));
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
		lightingWriter.SetVector4("shadowNormalOffsets", new Vector4(config.ShadowNormalOffsets, 0.0f));
		lightingWriter.SetUInt(
			"shadowLightIndex",
			config.ShadowsEnabled ? (uint)Math.Max(config.ShadowedDirectionalLightIndex, 0) : 0u);
		lightingWriter.SetUInt("shadowsEnabled", config.ShadowsEnabled ? 1u : 0u);
		lightingWriter.SetFloat("shadowMaxDistance", config.ShadowMaxDistance);
		
		if (config.TransparentEnvironmentBuffer is not IWritableGpuBuffer writableEnvironmentBuffer ||
		    config.TransparentLightingBuffer is not IWritableGpuBuffer writableLightingBuffer ||
		    config.DdgiDebugBuffer is not IWritableGpuBuffer writableDdgiDebugBuffer)
		{
			commandList.EndPass();
			return;
		}

		writableEnvironmentBuffer.Write<byte>(environmentWriter.AsBytes());
		writableLightingBuffer.Write<byte>(lightingWriter.AsBytes());

		var ddgiDebugWriter = _debugShader.Writer("DdgiDebugParams");
		ddgiDebugWriter.Clear();
		ddgiDebugWriter.SetUInt("ddgiProbeStateHandle", config.DdgiProbeState.Value);
		ddgiDebugWriter.SetUInt("ddgiProbeStateAvailable", config.DdgiProbeStateAvailable ? 1u : 0u);
		ddgiDebugWriter.SetUInt("ddgiProbeRelocationEnabled", config.DdgiProbeRelocationEnabled ? 1u : 0u);
		ddgiDebugWriter.SetUInt("ddgiAtlasColumns", (uint)config.DdgiGridShape.AtlasColumns);
		ddgiDebugWriter.SetUInt("ddgiProbeCountX", (uint)config.DdgiGridShape.CountX);
		ddgiDebugWriter.SetUInt("ddgiProbeCountY", (uint)config.DdgiGridShape.CountY);
		ddgiDebugWriter.SetUInt("ddgiProbeCountZ", (uint)config.DdgiGridShape.CountZ);
		ddgiDebugWriter.SetInt("ddgiStorageOffsetX", config.DdgiStorageOffset.X);
		ddgiDebugWriter.SetInt("ddgiStorageOffsetY", config.DdgiStorageOffset.Y);
		ddgiDebugWriter.SetInt("ddgiStorageOffsetZ", config.DdgiStorageOffset.Z);
		writableDdgiDebugBuffer.Write<byte>(ddgiDebugWriter.AsBytes());

		var buckets = config.Buckets.Span;
		if (buckets.Length == 0)
		{
			commandList.EndPass();
			return;
		}

		var fallbackCount = config.FallbackMaxCommandCount == 0
			? (uint)GpuDrawResources.MaxDrawCount
			: config.FallbackMaxCommandCount;
		var cameraRegisterIndex = _environmentShader.ReflectionLayout.GetConstantBuffer("CameraParams").RegisterIndex;
		commandList.SetPrimitiveTopology(PrimitiveTopology.TriangleList);
		SharedDrawIndirectExecution.BindPackedGeometry(
			commandList,
			config.PackedVertexBuffer,
			config.PackedIndexBuffer,
			config.PackedVertexStride);

		for (var i = 0; i < buckets.Length; i++)
		{
			var bucket = buckets[i];
			using (FrameProfiler.Instance.Measure(bucket.DebugName))
			{
				commandList.BindPipeline(bucket.Pipeline);
				BindPassBindings(commandList, bucket.PassBindings);
				commandList.BindConstantBuffer(bucket.BufferBindings.InstanceRegisterIndex, config.InstanceBuffer);
				commandList.BindConstantBuffer(bucket.BufferBindings.MaterialRegisterIndex, config.MaterialBuffer);
				commandList.BindConstantBuffer(bucket.BufferBindings.DrawArgsRegisterIndex, config.DrawArgsBuffer);
				commandList.BindConstantBuffer(bucket.BufferBindings.VisibleInstanceRegisterIndex, config.VisibleInstanceBuffer);
				if (config.MaterialGenerationBuffer is not null)
				{
					commandList.BindConstantBuffer(bucket.BufferBindings.MaterialGenerationRegisterIndex, config.MaterialGenerationBuffer);
				}
				if (config.PointLightBuffer is not null &&
				    bucket.BufferBindings.PointLightRegisterIndex is { } pointLightRegisterIndex)
				{
					commandList.BindConstantBuffer(pointLightRegisterIndex, config.PointLightBuffer);
				}
				if (config.ClusterHeaderBuffer is not null &&
				    bucket.BufferBindings.ClusterHeaderRegisterIndex is { } clusterHeaderRegisterIndex)
				{
					commandList.BindConstantBuffer(clusterHeaderRegisterIndex, config.ClusterHeaderBuffer);
				}
				if (config.ClusterLightIndexBuffer is not null &&
				    bucket.BufferBindings.ClusterLightIndexRegisterIndex is { } clusterLightIndexRegisterIndex)
				{
					commandList.BindConstantBuffer(clusterLightIndexRegisterIndex, config.ClusterLightIndexBuffer);
				}

				commandList.BindConstantBuffer(cameraRegisterIndex, config.CameraBuffer);
				// Transparency stays on the full-range path: compaction orders draws by atomic arrival,
				// which would make the alpha-blend order shuffle from frame to frame.
				SharedDrawIndirectExecution.ExecutePages(
					commandList,
					bucket.IndirectCommandPages.Span,
					fallbackCount);
			}
		}

		commandList.EndPass();
	}

	private static void BindPassBindings(IGfxCommandList commandList, GraphicsPassBindingSet passBindings)
	{
		foreach (var binding in passBindings.Bindings)
		{
			commandList.BindConstantBuffer(binding.RegisterIndex, binding.Resource);
		}
	}

	private List<TransparentExecutionBucket> BuildBuckets(IGfxDevice device, GpuDrawResources gpuDrawResources)
	{
		var laneDefinitions = GpuDrawExecutionLanes.GetDefinitionsForPass(DrawPassParticipation.ForwardTransparent);
		var buckets = new List<TransparentExecutionBucket>(laneDefinitions.Length);
		var activeIndirectSlot = gpuDrawResources.ActiveIndirectCommandSlot;
		var indirectCommandSet = _indirectCommandSets.Get(gpuDrawResources.ActiveViewIndex);
		indirectCommandSet.EnsureCreated(device);
		for (var i = 0; i < laneDefinitions.Length; i++)
		{
			var laneDefinition = laneDefinitions[i];
			var lane = _lanes.Get(laneDefinition.Key, device);
			buckets.Add(new TransparentExecutionBucket(
				laneDefinition.DrawKind,
				laneDefinition.BucketId,
				laneDefinition.ExecutionIndex,
				laneDefinition.DebugName,
				lane.BufferBindings,
				GetPassBindingSet(laneDefinition)
					?? throw new InvalidOperationException($"Missing pass bindings for transparent lane '{laneDefinition.DebugName}'."),
				lane.Pipeline,
				indirectCommandSet.GetAllocatedPages(activeIndirectSlot, laneDefinition.ExecutionIndex)));
		}

		return buckets;
	}

	private TransparentLane BuildLane(GpuDrawExecutionKey key, IGfxDevice device)
	{
		if (GpuDrawExecutionLanes.TryGetDefinition(key, out var lane) == false)
		{
			throw new InvalidOperationException($"No transparent execution lane is defined for '{key}'.");
		}

		var renderState = new RenderStateDescriptor(
			FillMode.Solid,
			lane.ResolveCullMode(CullMode.Back),
			depthTestEnabled: true,
			depthWriteEnabled: false,
			BlendMode.AlphaBlend,
			reverseWinding: lane.ReverseWinding);
		var state = new GraphicsPipelineState(
			LaneRenderTargets,
			new DepthStencilFormat(TextureFormat.D32Float),
			renderState,
			GraphicsLayoutKind.Material,
			$"Transparent:{lane.ShaderVariant}");
		var shader = new GraphicsShader(
			_shaderSource,
			GetShaderPath(lane.DrawKind),
			"vertexShader",
			"fragmentShader",
			lane.PreprocessorDefine);
		var pipeline = shader.GetPipeline(device, state);
		var reflection = shader.ReflectionLayout;

		return new TransparentLane(
			pipeline,
			SharedDrawGraphicsBufferBindings.FromTransparentReflection(reflection),
			reflection);
	}

	private static ShaderProgramId GetShaderPath(GpuDrawKind drawKind) => drawKind switch
	{
		GpuDrawKind.Mesh => EngineShaderPrograms.TransparentForward,
		GpuDrawKind.DebugPrimitive => EngineShaderPrograms.DebugPrimitiveForward,
		_ => throw new NotSupportedException($"Transparent shared draw kind '{drawKind}' does not define a shader.")
	};

	private DescriptorHandle ResolveSkyboxHandle(RenderGraphContext context, RenderGraphResourceHandle handle)
	{
		if (handle.IsValid == false)
		{
			return _bindlessRegistry.ErrorTextureHandle;
		}

		var texture = context.GetTexture(handle);
		return _bindlessRegistry.GetTextureHandle(texture);
	}

	/// <summary>The lanes of this pass's draw record sets.</summary>
	private sealed class LaneBindings : ILaneBindingProvider
	{
		private readonly TransparentForwardPass _pass;

		public LaneBindings(TransparentForwardPass pass)
		{
			_pass = pass;
		}

		public ulong Generation => _pass._lanes.Generation;

		public bool IsLaneDrawn(GpuDrawExecutionLaneDefinition lane) => _pass.HasIndirectLane(lane);

		public SharedDrawGraphicsBufferBindings? GetBufferBindings(GpuDrawExecutionLaneDefinition lane) =>
			_pass.GetBufferBindings(lane);

		public GraphicsPassBindingSet? GetPassBindings(GpuDrawExecutionLaneDefinition lane) =>
			_pass.GetPassBindingSet(lane);

		public IGfxPipeline? GetPipeline(GpuDrawExecutionLaneDefinition lane) => _pass.GetPipeline(lane);
	}

	/// <summary>What a lane's draws need from its shader variant.</summary>
	private sealed record TransparentLane(
		IGfxPipeline Pipeline,
		SharedDrawGraphicsBufferBindings BufferBindings,
		ShaderReflectionLayout Reflection);
}

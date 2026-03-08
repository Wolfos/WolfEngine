#nullable enable

using System.Numerics;
using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Rendering;

public readonly struct RenderGraphFrameResources
{
	public Int2 FramebufferSize { get; init; }
	public Int2 SceneFramebufferSize { get; init; }
	public bool SceneEnabled { get; init; }
	public RenderGraphResourceHandle FinalColor { get; init; }
	public RenderGraphResourceHandle GBufferAlbedo { get; init; }
	public RenderGraphResourceHandle GBufferNormal { get; init; }
	public RenderGraphResourceHandle GBufferMaterial { get; init; }
	public RenderGraphResourceHandle GBufferEmissive { get; init; }
	public RenderGraphResourceHandle GBufferDepth { get; init; }
	public RenderGraphResourceHandle ShadowMapDepth0 { get; init; }
	public RenderGraphResourceHandle ShadowMapDepth1 { get; init; }
	public RenderGraphResourceHandle ShadowMapDepth2 { get; init; }
	public RenderGraphResourceHandle LightingBuffer { get; init; }
	public RenderGraphResourceHandle SkyboxEnvironment { get; init; }
	public RenderGraphResourceHandle SkyboxIrradiance { get; init; }
	public RenderGraphResourceHandle SkyboxPrefilter { get; init; }
	public RenderGraphResourceHandle SkyboxBrdfLut { get; init; }
	public RenderConfig Config { get; init; }
}

public sealed class RenderGraphFrameBuilder
{
	private readonly RenderGraphResourceRegistry _resources;
	private readonly IRenderer _renderer;
	private readonly DeferredLightingPass _deferredLightingPass;
	private readonly TransparentForwardPass _transparentForwardPass;
	private readonly ShadowMapPass _shadowMapPass;
	private readonly GpuDrawPass _gpuDrawPass;
	private readonly GpuDrawResources _gpuDrawResources;
	private readonly SkyboxPass _skyboxPass;
	private readonly IImGuiRenderer _imGuiRenderer;
	private SkyboxResources? _externalSkybox;
	private RenderGraphFrameResources _frameResources;
	private UiFrameData _uiFrame = UiFrameData.Empty;
	private bool _hasPreviousFrameShape;
	private Int2 _previousFramebufferSize;
	private Int2 _previousSceneFramebufferSize;
	private bool _previousSceneEnabled;
	
	private readonly Action<RenderGraphContext> _gbufferExecute;
	private readonly Action<RenderGraphContext> _deferredLightingExecute;
	private readonly Action<RenderGraphContext> _transparentForwardExecute;
	private readonly Action<RenderGraphContext> _imguiExecute;
	private readonly Action<RenderGraphContext> _gpuDrawUpdateExecute;
	private readonly Action<RenderGraphContext> _gpuDrawShadowCullExecute;
	private readonly Action<RenderGraphContext> _shadowMapExecute;
	private readonly Action<RenderGraphContext> _gpuDrawCameraCullExecute;
	private readonly Action<RenderGraphContext> _skyboxEnvironmentExecute;
	private readonly Action<RenderGraphContext> _skyboxIrradianceExecute;
	private readonly Action<RenderGraphContext> _skyboxPrefilterExecute;
	private readonly Action<RenderGraphContext> _skyboxBrdfExecute;
	private bool _useProceduralSkybox;
	private bool _recordProceduralSkyLighting;
	private bool _recordProceduralSkyBrdf;
	private ResourceState _proceduralSkyboxInitialState = ResourceState.ShaderResource;

	
	public RenderGraphFrameBuilder(
		RenderGraphResourceRegistry resources,
		IRenderer renderer,
		DeferredLightingPass deferredLightingPass,
		TransparentForwardPass transparentForwardPass,
		ShadowMapPass shadowMapPass,
		GpuDrawPass gpuDrawPass,
		GpuDrawResources gpuDrawResources,
		SkyboxPass skyboxPass,
		IImGuiRenderer imGuiRenderer)
	{
		_resources = resources;
		_renderer = renderer;
		_deferredLightingPass = deferredLightingPass;
		_transparentForwardPass = transparentForwardPass;
		_shadowMapPass = shadowMapPass;
		_gpuDrawPass = gpuDrawPass;
		_gpuDrawResources = gpuDrawResources;
		_skyboxPass = skyboxPass;
		_imGuiRenderer = imGuiRenderer;

		_gbufferExecute = ExecuteGBuffer;
		_deferredLightingExecute = ExecuteDeferredLighting;
		_transparentForwardExecute = ExecuteTransparentForward;
		_imguiExecute = ExecuteImGui;
		_gpuDrawUpdateExecute = ExecuteGpuDrawUpdate;
		_gpuDrawShadowCullExecute = ExecuteGpuDrawCullShadow;
		_shadowMapExecute = ExecuteShadowMap;
		_gpuDrawCameraCullExecute = ExecuteGpuDrawCullCamera;
		_skyboxEnvironmentExecute = ExecuteSkyboxEnvironment;
		_skyboxIrradianceExecute = ExecuteSkyboxIrradiance;
		_skyboxPrefilterExecute = ExecuteSkyboxPrefilter;
		_skyboxBrdfExecute = ExecuteSkyboxBrdf;
	}

	public void SetSkybox(SkyboxResources skybox)
	{
		_externalSkybox = skybox;
	}

	public void BeginFrame(
		Int2 framebufferSize,
		Int2 sceneFramebufferSize,
		RenderGraphResourceHandle sceneColorHandle,
		bool sceneEnabled,
		Vector3 sunDirection,
		RenderConfig config)
	{
		InvalidateTransientPoolIfFrameShapeChanged(framebufferSize, sceneFramebufferSize, sceneEnabled);

		_skyboxPass.PrepareFrame(_renderer.GetGfxDevice(), sunDirection);
		var activeSkybox = _externalSkybox ?? _skyboxPass.GetProceduralResources();
		_useProceduralSkybox = ReferenceEquals(activeSkybox, _externalSkybox) == false;
		_recordProceduralSkyLighting = _useProceduralSkybox && _skyboxPass.ShouldRecordProceduralLightingUpdate;
		_recordProceduralSkyBrdf = _useProceduralSkybox && _skyboxPass.ShouldRecordBrdfLutUpdate;
		_proceduralSkyboxInitialState = _skyboxPass.ProceduralResourcesInitialState;

		var skyboxEnvHandle = default(RenderGraphResourceHandle);
		var skyboxIrrHandle = default(RenderGraphResourceHandle);
		var skyboxPrefilterHandle = default(RenderGraphResourceHandle);
		var skyboxBrdfHandle = default(RenderGraphResourceHandle);
		if (activeSkybox.EnvironmentTexture is IGfxTexture envTexture)
		{
			var initialState = _useProceduralSkybox
				? _proceduralSkyboxInitialState
				: ResourceState.ShaderResource;
			skyboxEnvHandle = _resources.ImportTexture(envTexture, takeOwnership: false, initialState: initialState);
			if (activeSkybox.IrradianceTexture is IGfxTexture irr)
			{
				skyboxIrrHandle = _resources.ImportTexture(irr, takeOwnership: false, initialState: initialState);
			}
			if (activeSkybox.PrefilteredEnvironment is IGfxTexture prefilter)
			{
				skyboxPrefilterHandle = _resources.ImportTexture(prefilter, takeOwnership: false, initialState: initialState);
			}
			if (activeSkybox.BrdfLut is IGfxTexture brdf)
			{
				skyboxBrdfHandle = _resources.ImportTexture(brdf, takeOwnership: false, initialState: initialState);
			}
		}

		var lightingHandle = default(RenderGraphResourceHandle);
		var gbufferAlbedoHandle = default(RenderGraphResourceHandle);
		var gbufferNormalHandle = default(RenderGraphResourceHandle);
		var gbufferMaterialHandle = default(RenderGraphResourceHandle);
		var gbufferEmissiveHandle = default(RenderGraphResourceHandle);
		var gbufferDepthHandle = default(RenderGraphResourceHandle);
		var shadowMapHandle0 = default(RenderGraphResourceHandle);
		var shadowMapHandle1 = default(RenderGraphResourceHandle);
		var shadowMapHandle2 = default(RenderGraphResourceHandle);
		if (sceneEnabled)
		{
			gbufferAlbedoHandle = _resources.CreateTransientTexture(new TextureDescriptor(
				sceneFramebufferSize.X,
				sceneFramebufferSize.Y,
				TextureFormat.Bgra8Unorm,
				TextureUsage.RenderTarget | TextureUsage.ShaderResource,
				new ColorRGBA(0.392f, 0.584f, 0.929f, 1.0f)));
			gbufferNormalHandle = _resources.CreateTransientTexture(new TextureDescriptor(
				sceneFramebufferSize.X,
				sceneFramebufferSize.Y,
				TextureFormat.Rgba16Float,
				TextureUsage.RenderTarget | TextureUsage.ShaderResource,
				new ColorRGBA(0.5f, 0.5f, 1.0f, 1.0f)));
			gbufferMaterialHandle = _resources.CreateTransientTexture(new TextureDescriptor(
				sceneFramebufferSize.X,
				sceneFramebufferSize.Y,
				TextureFormat.Rgba8Unorm,
				TextureUsage.RenderTarget | TextureUsage.ShaderResource,
				new ColorRGBA(0.0f, 0.0f, 0.0f, 1.0f)));
			gbufferEmissiveHandle = _resources.CreateTransientTexture(new TextureDescriptor(
				sceneFramebufferSize.X,
				sceneFramebufferSize.Y,
				TextureFormat.Rgba8Unorm,
				TextureUsage.RenderTarget | TextureUsage.ShaderResource,
				new ColorRGBA(0.0f, 0.0f, 0.0f, 1.0f)));
			gbufferDepthHandle = _resources.CreateTransientTexture(new TextureDescriptor(
				sceneFramebufferSize.X,
				sceneFramebufferSize.Y,
				TextureFormat.D32Float,
				TextureUsage.DepthStencil | TextureUsage.ShaderResource,
				default(ColorRGBA),
				1.0f));
			shadowMapHandle0 = _resources.CreateTransientTexture(new TextureDescriptor(
				ShadowMapPass.CascadeResolution,
				ShadowMapPass.CascadeResolution,
				TextureFormat.D32Float,
				TextureUsage.DepthStencil | TextureUsage.ShaderResource,
				default(ColorRGBA),
				1.0f));
			shadowMapHandle1 = _resources.CreateTransientTexture(new TextureDescriptor(
				ShadowMapPass.CascadeResolution,
				ShadowMapPass.CascadeResolution,
				TextureFormat.D32Float,
				TextureUsage.DepthStencil | TextureUsage.ShaderResource,
				default(ColorRGBA),
				1.0f));
			shadowMapHandle2 = _resources.CreateTransientTexture(new TextureDescriptor(
				ShadowMapPass.CascadeResolution,
				ShadowMapPass.CascadeResolution,
				TextureFormat.D32Float,
				TextureUsage.DepthStencil | TextureUsage.ShaderResource,
				default(ColorRGBA),
				1.0f));
			lightingHandle = sceneColorHandle.IsValid
				? sceneColorHandle
				: _resources.CreateTransientTexture(new TextureDescriptor(
					sceneFramebufferSize.X,
					sceneFramebufferSize.Y,
					TextureFormat.Bgra8Unorm,
					TextureUsage.RenderTarget | TextureUsage.ShaderResource | TextureUsage.UnorderedAccess));
		}

		_frameResources = new()
		{
			FramebufferSize = framebufferSize,
			SceneFramebufferSize = sceneFramebufferSize,
			SceneEnabled = sceneEnabled,
			FinalColor = _resources.CreateTransientTexture(new TextureDescriptor(
				framebufferSize.X,
				framebufferSize.Y,
				TextureFormat.Bgra8Unorm,
				TextureUsage.RenderTarget | TextureUsage.ShaderResource,
				new ColorRGBA(0.05f, 0.05f, 0.05f, 1.0f))),
			GBufferAlbedo = gbufferAlbedoHandle,
			GBufferNormal = gbufferNormalHandle,
			GBufferMaterial = gbufferMaterialHandle,
			GBufferEmissive = gbufferEmissiveHandle,
			GBufferDepth = gbufferDepthHandle,
			ShadowMapDepth0 = shadowMapHandle0,
			ShadowMapDepth1 = shadowMapHandle1,
			ShadowMapDepth2 = shadowMapHandle2,
			LightingBuffer = lightingHandle,
			SkyboxEnvironment = skyboxEnvHandle,
			SkyboxIrradiance = skyboxIrrHandle,
			SkyboxPrefilter = skyboxPrefilterHandle,
			SkyboxBrdfLut = skyboxBrdfHandle,
			Config = config
		};
	}

	public void SetUiFrame(UiFrameData uiFrame)
	{
		_uiFrame = uiFrame;
	}
	

	public void Build(RenderGraph graph)
	{
		if (_frameResources.SceneEnabled)
		{
			graph.AddPass("GpuDraw Update", PassKind.Compute)
				.SetExecute(_gpuDrawUpdateExecute);

			graph.AddPass("GpuDraw Cull (Shadow View)", PassKind.Compute)
				.SetExecute(_gpuDrawShadowCullExecute);

			graph.AddPass("Shadow Map", PassKind.Graphics)
				.WriteTexture(_frameResources.ShadowMapDepth0, ResourceState.DepthWrite)
				.WriteTexture(_frameResources.ShadowMapDepth1, ResourceState.DepthWrite)
				.WriteTexture(_frameResources.ShadowMapDepth2, ResourceState.DepthWrite)
				.SetExecute(_shadowMapExecute);

			graph.AddPass("GpuDraw Cull (Camera View)", PassKind.Compute)
				.SetExecute(_gpuDrawCameraCullExecute);

			graph.AddPass("GBuffer", PassKind.Graphics)
				.WriteTexture(_frameResources.GBufferAlbedo, ResourceState.RenderTarget)
				.WriteTexture(_frameResources.GBufferNormal, ResourceState.RenderTarget)
				.WriteTexture(_frameResources.GBufferMaterial, ResourceState.RenderTarget)
				.WriteTexture(_frameResources.GBufferEmissive, ResourceState.RenderTarget)
				.WriteTexture(_frameResources.GBufferDepth, ResourceState.DepthWrite)
				.SetExecute(_gbufferExecute);

			if (_useProceduralSkybox && _recordProceduralSkyLighting)
			{
				graph.AddPass("Skybox Environment", PassKind.Compute)
					.WriteTexture(_frameResources.SkyboxEnvironment, ResourceState.UnorderedAccess)
					.SetExecute(_skyboxEnvironmentExecute);

				graph.AddPass("Skybox Irradiance", PassKind.Compute)
					.ReadTexture(_frameResources.SkyboxEnvironment, ResourceState.ShaderResource)
					.WriteTexture(_frameResources.SkyboxIrradiance, ResourceState.UnorderedAccess)
					.SetExecute(_skyboxIrradianceExecute);

				graph.AddPass("Skybox Prefilter", PassKind.Compute)
					.ReadTexture(_frameResources.SkyboxEnvironment, ResourceState.ShaderResource)
					.WriteTexture(_frameResources.SkyboxPrefilter, ResourceState.UnorderedAccess)
					.SetExecute(_skyboxPrefilterExecute);
			}

			if (_useProceduralSkybox && _recordProceduralSkyBrdf)
			{
				graph.AddPass("Skybox BRDF LUT", PassKind.Compute)
					.WriteTexture(_frameResources.SkyboxBrdfLut, ResourceState.UnorderedAccess)
					.SetExecute(_skyboxBrdfExecute);
			}

			var deferredLightingBuilder = graph.AddPass("Deferred Lighting", PassKind.Compute)
				.ReadTexture(_frameResources.GBufferAlbedo, ResourceState.ShaderResource)
				.ReadTexture(_frameResources.GBufferNormal, ResourceState.ShaderResource)
				.ReadTexture(_frameResources.GBufferMaterial, ResourceState.ShaderResource)
				.ReadTexture(_frameResources.GBufferEmissive, ResourceState.ShaderResource)
				.ReadTexture(_frameResources.GBufferDepth, ResourceState.ShaderResource)
				.ReadTexture(_frameResources.ShadowMapDepth0, ResourceState.ShaderResource)
				.ReadTexture(_frameResources.ShadowMapDepth1, ResourceState.ShaderResource)
				.ReadTexture(_frameResources.ShadowMapDepth2, ResourceState.ShaderResource);
			if (_frameResources.SkyboxEnvironment.IsValid)
			{
				deferredLightingBuilder.ReadTexture(_frameResources.SkyboxEnvironment, ResourceState.ShaderResource);
			}
			if (_frameResources.SkyboxIrradiance.IsValid)
			{
				deferredLightingBuilder.ReadTexture(_frameResources.SkyboxIrradiance, ResourceState.ShaderResource);
			}
			if (_frameResources.SkyboxPrefilter.IsValid)
			{
				deferredLightingBuilder.ReadTexture(_frameResources.SkyboxPrefilter, ResourceState.ShaderResource);
			}
			if (_frameResources.SkyboxBrdfLut.IsValid)
			{
				deferredLightingBuilder.ReadTexture(_frameResources.SkyboxBrdfLut, ResourceState.ShaderResource);
			}
			deferredLightingBuilder
				.WriteTexture(_frameResources.LightingBuffer, ResourceState.UnorderedAccess)
				.SetExecute(_deferredLightingExecute);

			var transparentForwardBuilder = graph.AddPass("Transparent Forward", PassKind.Graphics)
				.ReadTexture(_frameResources.GBufferDepth, ResourceState.DepthWrite)
				.ReadTexture(_frameResources.ShadowMapDepth0, ResourceState.ShaderResource)
				.ReadTexture(_frameResources.ShadowMapDepth1, ResourceState.ShaderResource)
				.ReadTexture(_frameResources.ShadowMapDepth2, ResourceState.ShaderResource)
				.WriteTexture(_frameResources.LightingBuffer, ResourceState.RenderTarget);
			if (_frameResources.SkyboxEnvironment.IsValid)
			{
				transparentForwardBuilder.ReadTexture(_frameResources.SkyboxEnvironment, ResourceState.ShaderResource);
			}
			if (_frameResources.SkyboxIrradiance.IsValid)
			{
				transparentForwardBuilder.ReadTexture(_frameResources.SkyboxIrradiance, ResourceState.ShaderResource);
			}
			if (_frameResources.SkyboxPrefilter.IsValid)
			{
				transparentForwardBuilder.ReadTexture(_frameResources.SkyboxPrefilter, ResourceState.ShaderResource);
			}
			if (_frameResources.SkyboxBrdfLut.IsValid)
			{
				transparentForwardBuilder.ReadTexture(_frameResources.SkyboxBrdfLut, ResourceState.ShaderResource);
			}
			transparentForwardBuilder.SetExecute(_transparentForwardExecute);
		}

		graph.AddPass("ImGui", PassKind.Graphics)
			.WriteTexture(_frameResources.FinalColor, ResourceState.RenderTarget)
			.SetExecute(_imguiExecute);
	}

	public RenderGraphResourceHandle GetFinalColorHandle() => _frameResources.FinalColor;

	private void ExecuteGpuDrawUpdate(RenderGraphContext context)
	{
		_gpuDrawPass.RecordUpdate(context);
	}

	private void ExecuteGpuDrawCullShadow(RenderGraphContext context)
	{
		var sceneData = context.SceneData!;
		_shadowMapPass.PrepareFrame(sceneData);
		var shadowData = _shadowMapPass.GetCurrentFrameData();
		if (shadowData.Enabled == false)
		{
			return;
		}

		_gpuDrawPass.RecordCullForView(
			context,
			shadowData.GetCascadeViewProjection(ShadowMapPass.CascadeCount - 1),
			sceneData.CameraOrigin,
			useShadowBuffers: true);
	}

	private void ExecuteShadowMap(RenderGraphContext context)
	{
		for (var cascadeIndex = 0; cascadeIndex < ShadowMapPass.CascadeCount; cascadeIndex++)
		{
			var shadowMapHandle = GetShadowMapHandle(_frameResources, cascadeIndex);
			var depthTexture = context.GetTexture(shadowMapHandle);
			var config = _shadowMapPass.BuildConfig(
				context,
				depthTexture,
				_renderer.GetGfxDevice(),
				_gpuDrawResources,
				cascadeIndex);
			_shadowMapPass.Record(context, in config);
		}
	}

	private void ExecuteGpuDrawCullCamera(RenderGraphContext context)
	{
		_gpuDrawPass.RecordCull(context, context.SceneData!);
	}

	private void ExecuteSkyboxEnvironment(RenderGraphContext context)
	{
		_skyboxPass.RecordEnvironment(context, _frameResources.Config.SkyboxConfig);
	}

	private void ExecuteSkyboxIrradiance(RenderGraphContext context)
	{
		_skyboxPass.RecordIrradiance(context);
	}

	private void ExecuteSkyboxPrefilter(RenderGraphContext context)
	{
		_skyboxPass.RecordPrefilter(context);
	}

	private void ExecuteSkyboxBrdf(RenderGraphContext context)
	{
		_skyboxPass.RecordBrdfLut(context);
	}

	private void ExecuteGBuffer(RenderGraphContext context)
	{
		var albedoTexture = context.GetTexture(_frameResources.GBufferAlbedo);
		var normalTexture = context.GetTexture(_frameResources.GBufferNormal);
		var materialTexture = context.GetTexture(_frameResources.GBufferMaterial);
		var emissiveTexture = context.GetTexture(_frameResources.GBufferEmissive);
		var depthTexture = context.GetTexture(_frameResources.GBufferDepth);
		var bucketDefinitions = GBufferDrawBuckets.Definitions;
		var bucketList = new List<GBufferExecutionBucket>(bucketDefinitions.Length);
		var activeIndirectSlot = _gpuDrawResources.ActiveIndirectCommandSlot;
		for (var i = 0; i < bucketDefinitions.Length; i++)
		{
			var bucketDefinition = bucketDefinitions[i];
			if (bucketDefinition.SupportsPass(DrawPassParticipation.GBuffer) == false)
			{
				continue;
			}

			var pipeline = _gpuDrawResources.GetGBufferPipeline(i);
			var indirectCommandBuffer = _gpuDrawResources.GetIndirectCommandBufferSlot(activeIndirectSlot, i);
			if (pipeline is null || indirectCommandBuffer is null)
			{
				continue;
			}

			bucketList.Add(new GBufferExecutionBucket(
				i,
				bucketDefinition.DebugName,
				pipeline,
				indirectCommandBuffer));
		}

		var gbufferConfig = new GBufferPassConfig
		{
			FramebufferWidth = _frameResources.SceneFramebufferSize.X,
			FramebufferHeight = _frameResources.SceneFramebufferSize.Y,
			AlbedoTarget = albedoTexture,
			NormalTarget = normalTexture,
			MaterialTarget = materialTexture,
			EmissiveTarget = emissiveTexture,
			DepthTarget = depthTexture,
			AlbedoClearColor = new(0.392f, 0.584f, 0.929f, 1.0f),
			EmissiveClearColor = new(0.0f, 0.0f, 0.0f, 1.0f),
			NormalClearColor = new(0.5f, 0.5f, 1.0f, 1.0f),
			MaterialClearColor = new(0.0f, 0.0f, 0.0f, 1.0f),
			DepthClearValue = 1.0f,
			InstanceBuffer = _gpuDrawResources.InstanceBuffer,
			MaterialBuffer = _gpuDrawResources.MaterialBuffer,
			DrawArgsBuffer = _gpuDrawResources.DrawArgsBuffer,
			VisibleDrawIdsPerBucketBuffer = _gpuDrawResources.VisibleDrawIdsPerBucketBuffer,
			DrawCountPerBucketBuffer = _gpuDrawResources.DrawCountPerBucketBuffer,
			DrawExecutionRangePerBucketBuffer = _gpuDrawResources.DrawExecutionRangePerBucketBuffer,
			MaterialGenerationBuffer = _gpuDrawResources.MaterialGenerationBuffer,
			Buckets = bucketList.ToArray(),
			FallbackMaxCommandCount = _gpuDrawResources.ActiveDrawCommandUpperBound,
			CameraLayout = _gpuDrawResources.GBufferCameraLayout,
			CameraBuffer = _gpuDrawResources.CameraBuffer,
			SkyboxEnvironment = DescriptorHandle.Invalid,
			SkyboxSampler = DescriptorHandle.Invalid
		};

		GBufferPass.Record(context, gbufferConfig, context.SceneData!);
	}
	
	
	private void ExecuteDeferredLighting(RenderGraphContext context)
	{
		var config = _deferredLightingPass.BuildConfig(
			context,
			_frameResources,
			_renderer.GetGfxDevice(),
			_shadowMapPass.GetCurrentFrameData());
		_deferredLightingPass.Record(context, ref config, context.SceneData!);
	}

	private void ExecuteTransparentForward(RenderGraphContext context)
	{
		var config = _transparentForwardPass.BuildConfig(
			context,
			_frameResources,
			_renderer.GetGfxDevice(),
			_gpuDrawResources,
			_shadowMapPass.GetCurrentFrameData());
		_transparentForwardPass.Record(context, in config, context.SceneData!);
	}

	private void ExecuteImGui(RenderGraphContext context)
	{
		var finalColor = context.GetTexture(_frameResources.FinalColor);
		_imGuiRenderer.EnsureResources(_renderer.GetGfxDevice(), _uiFrame);
		_imGuiRenderer.Record(context, _uiFrame, finalColor);
	}

	private static RenderGraphResourceHandle GetShadowMapHandle(in RenderGraphFrameResources resources, int cascadeIndex)
	{
		return cascadeIndex switch
		{
			0 => resources.ShadowMapDepth0,
			1 => resources.ShadowMapDepth1,
			2 => resources.ShadowMapDepth2,
			_ => throw new ArgumentOutOfRangeException(nameof(cascadeIndex), cascadeIndex, "Cascade index is out of range.")
		};
	}

	private void InvalidateTransientPoolIfFrameShapeChanged(Int2 framebufferSize, Int2 sceneFramebufferSize, bool sceneEnabled)
	{
		var changed = _hasPreviousFrameShape == false ||
		              _previousFramebufferSize.X != framebufferSize.X ||
		              _previousFramebufferSize.Y != framebufferSize.Y ||
		              _previousSceneFramebufferSize.X != sceneFramebufferSize.X ||
		              _previousSceneFramebufferSize.Y != sceneFramebufferSize.Y ||
		              _previousSceneEnabled != sceneEnabled;
		if (changed == false)
		{
			return;
		}

		_resources.InvalidateTransientTexturePool();
		_previousFramebufferSize = framebufferSize;
		_previousSceneFramebufferSize = sceneFramebufferSize;
		_previousSceneEnabled = sceneEnabled;
		_hasPreviousFrameShape = true;
	}
}

#nullable enable

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Rendering;

public readonly struct RenderGraphFrameResources
{
	public Int2 FramebufferSize { get; init; }
	public RenderGraphResourceHandle FinalColor { get; init; }
	public RenderGraphResourceHandle GBufferAlbedo { get; init; }
	public RenderGraphResourceHandle GBufferNormal { get; init; }
	public RenderGraphResourceHandle GBufferMaterial { get; init; }
	public RenderGraphResourceHandle GBufferEmissive { get; init; }
	public RenderGraphResourceHandle GBufferDepth { get; init; }
	public RenderGraphResourceHandle ShadowMapDepth { get; init; }
	public RenderGraphResourceHandle LightingBuffer { get; init; }
	public RenderGraphResourceHandle SkyboxEnvironment { get; init; }
	public RenderGraphResourceHandle SkyboxIrradiance { get; init; }
	public RenderGraphResourceHandle SkyboxPrefilter { get; init; }
	public RenderGraphResourceHandle SkyboxBrdfLut { get; init; }
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
	private readonly IImGuiRenderer _imGuiRenderer;
	private SkyboxResources? _skybox;
	private RenderGraphFrameResources _frameResources;
	private UiFrameData _uiFrame = UiFrameData.Empty;
	
	private readonly Action<RenderGraphContext> _gbufferExecute;
	private readonly Action<RenderGraphContext> _deferredLightingExecute;
	private readonly Action<RenderGraphContext> _transparentForwardExecute;
	private readonly Action<RenderGraphContext> _imguiExecute;
	private readonly Action<RenderGraphContext> _gpuDrawUpdateExecute;
	private readonly Action<RenderGraphContext> _gpuDrawShadowCullExecute;
	private readonly Action<RenderGraphContext> _shadowMapExecute;
	private readonly Action<RenderGraphContext> _gpuDrawCameraCullExecute;

	
	public RenderGraphFrameBuilder(RenderGraphResourceRegistry resources, IRenderer renderer,
		DeferredLightingPass deferredLightingPass, TransparentForwardPass transparentForwardPass, ShadowMapPass shadowMapPass, GpuDrawPass gpuDrawPass, GpuDrawResources gpuDrawResources, IImGuiRenderer imGuiRenderer)
	{
		_resources = resources;
		_renderer = renderer;
		_deferredLightingPass = deferredLightingPass;
		_transparentForwardPass = transparentForwardPass;
		_shadowMapPass = shadowMapPass;
		_gpuDrawPass = gpuDrawPass;
		_gpuDrawResources = gpuDrawResources;
		_imGuiRenderer = imGuiRenderer;

		_gbufferExecute = ExecuteGBuffer;
		_deferredLightingExecute = ExecuteDeferredLighting;
		_transparentForwardExecute = ExecuteTransparentForward;
		_imguiExecute = ExecuteImGui;
		_gpuDrawUpdateExecute = ExecuteGpuDrawUpdate;
		_gpuDrawShadowCullExecute = ExecuteGpuDrawCullShadow;
		_shadowMapExecute = ExecuteShadowMap;
		_gpuDrawCameraCullExecute = ExecuteGpuDrawCullCamera;
	}

	public void SetSkybox(SkyboxResources skybox)
	{
		_skybox = skybox;
	}

	public void BeginFrame(
		Int2 framebufferSize)
	{
		var skyboxEnvHandle = default(RenderGraphResourceHandle);
		var skyboxIrrHandle = default(RenderGraphResourceHandle);
		var skyboxPrefilterHandle = default(RenderGraphResourceHandle);
		var skyboxBrdfHandle = default(RenderGraphResourceHandle);
		if (_skybox?.EnvironmentTexture is IGfxTexture envTexture)
		{
			skyboxEnvHandle = _resources.ImportTexture(envTexture, takeOwnership: false,
				initialState: ResourceState.ShaderResource);
			if (_skybox.IrradianceTexture is IGfxTexture irr)
			{
				skyboxIrrHandle = _resources.ImportTexture(irr, takeOwnership: false,
					initialState: ResourceState.ShaderResource);
			}
			if (_skybox.PrefilteredEnvironment is IGfxTexture prefilter)
			{
				skyboxPrefilterHandle = _resources.ImportTexture(prefilter, takeOwnership: false,
					initialState: ResourceState.ShaderResource);
			}
			if (_skybox.BrdfLut is IGfxTexture brdf)
			{
				skyboxBrdfHandle = _resources.ImportTexture(brdf, takeOwnership: false,
					initialState: ResourceState.ShaderResource);
			}
		}

		_frameResources = new()
		{
			FramebufferSize = framebufferSize,
			FinalColor = _resources.CreateTransientTexture(new TextureDescriptor(
				framebufferSize.X,
				framebufferSize.Y,
				TextureFormat.Bgra8Unorm,
				TextureUsage.RenderTarget | TextureUsage.ShaderResource)),
			GBufferAlbedo = _resources.CreateTransientTexture(new TextureDescriptor(framebufferSize.X,
				framebufferSize.Y, TextureFormat.Bgra8Unorm, TextureUsage.RenderTarget | TextureUsage.ShaderResource,
				new Vector4(0.392f, 0.584f, 0.929f, 1.0f))),
			GBufferNormal = _resources.CreateTransientTexture(new TextureDescriptor(framebufferSize.X,
				framebufferSize.Y, TextureFormat.Rgba16Float, TextureUsage.RenderTarget | TextureUsage.ShaderResource,
				new Vector4(0.5f, 0.5f, 1.0f, 1.0f))),
			GBufferMaterial = _resources.CreateTransientTexture(new TextureDescriptor(framebufferSize.X,
				framebufferSize.Y, TextureFormat.Rgba8Unorm, TextureUsage.RenderTarget | TextureUsage.ShaderResource,
				new Vector4(0.0f, 0.0f, 0.0f, 1.0f))),
			GBufferEmissive = _resources.CreateTransientTexture(new TextureDescriptor(framebufferSize.X,
				framebufferSize.Y, TextureFormat.Rgba8Unorm, TextureUsage.RenderTarget | TextureUsage.ShaderResource,
				new Vector4(0.0f, 0.0f, 0.0f, 1.0f))),
			GBufferDepth = _resources.CreateTransientTexture(new TextureDescriptor(framebufferSize.X, framebufferSize.Y,
				TextureFormat.D32Float, TextureUsage.DepthStencil | TextureUsage.ShaderResource, Vector4.Zero, 1.0f)),
			ShadowMapDepth = _resources.CreateTransientTexture(new TextureDescriptor(
				ShadowMapPass.ShadowMapResolution,
				ShadowMapPass.ShadowMapResolution,
				TextureFormat.D32Float,
				TextureUsage.DepthStencil | TextureUsage.ShaderResource,
				Vector4.Zero,
				1.0f)),
			LightingBuffer = _resources.CreateTransientTexture(new TextureDescriptor(
				framebufferSize.X,
				framebufferSize.Y,
				TextureFormat.Bgra8Unorm,
				TextureUsage.RenderTarget | TextureUsage.ShaderResource | TextureUsage.UnorderedAccess)),
			SkyboxEnvironment = skyboxEnvHandle,
			SkyboxIrradiance = skyboxIrrHandle,
			SkyboxPrefilter = skyboxPrefilterHandle,
			SkyboxBrdfLut = skyboxBrdfHandle
		};
	}

	public void SetUiFrame(UiFrameData uiFrame)
	{
		_uiFrame = uiFrame;
	}
	

	public void Build(RenderGraph graph)
	{
		graph.AddPass("GpuDraw Update", PassKind.Compute)
			.SetExecute(_gpuDrawUpdateExecute);

		graph.AddPass("GpuDraw Cull (Shadow View)", PassKind.Compute)
			.SetExecute(_gpuDrawShadowCullExecute);

		graph.AddPass("Shadow Map", PassKind.Graphics)
			.WriteTexture(_frameResources.ShadowMapDepth, ResourceState.DepthWrite)
			.SetExecute(_shadowMapExecute);

		graph.AddPass("GpuDraw Cull (Camera View)", PassKind.Compute)
			.SetExecute(_gpuDrawCameraCullExecute);

		// Register GBuffer pass with proper resource states
		graph.AddPass("GBuffer", PassKind.Graphics)
			.WriteTexture(_frameResources.GBufferAlbedo, ResourceState.RenderTarget)
			.WriteTexture(_frameResources.GBufferNormal, ResourceState.RenderTarget)
			.WriteTexture(_frameResources.GBufferMaterial, ResourceState.RenderTarget)
			.WriteTexture(_frameResources.GBufferEmissive, ResourceState.RenderTarget)
			.WriteTexture(_frameResources.GBufferDepth, ResourceState.DepthWrite)
			.SetExecute(_gbufferExecute);

		// Register Deferred Lighting pass with proper resource states
		var deferredLightingBuilder = graph.AddPass("Deferred Lighting", PassKind.Compute)
			.ReadTexture(_frameResources.GBufferAlbedo, ResourceState.ShaderResource)
			.ReadTexture(_frameResources.GBufferNormal, ResourceState.ShaderResource)
			.ReadTexture(_frameResources.GBufferMaterial, ResourceState.ShaderResource)
			.ReadTexture(_frameResources.GBufferEmissive, ResourceState.ShaderResource)
			.ReadTexture(_frameResources.GBufferDepth, ResourceState.ShaderResource)
			.ReadTexture(_frameResources.ShadowMapDepth, ResourceState.ShaderResource);
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
			.ReadTexture(_frameResources.ShadowMapDepth, ResourceState.ShaderResource)
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

		graph.AddPass("ImGui", PassKind.Graphics)
			.ReadTexture(_frameResources.LightingBuffer, ResourceState.CopySource)
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

		_gpuDrawPass.RecordCullForView(context, shadowData.ViewProjection, sceneData.CameraOrigin, useShadowBuffers: true);
	}

	private void ExecuteShadowMap(RenderGraphContext context)
	{
		var config = _shadowMapPass.BuildConfig(context, _frameResources, _renderer.GetGfxDevice(), _gpuDrawResources);
		_shadowMapPass.Record(context, in config);
	}

	private void ExecuteGpuDrawCullCamera(RenderGraphContext context)
	{
		_gpuDrawPass.RecordCull(context, context.SceneData!);
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
			FramebufferWidth = _frameResources.FramebufferSize.X,
			FramebufferHeight = _frameResources.FramebufferSize.Y,
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

	private unsafe void ExecuteImGui(RenderGraphContext context)
	{
		var finalColor = context.GetTexture(_frameResources.FinalColor);
		var lighting = context.GetTexture(_frameResources.LightingBuffer);
		_imGuiRenderer.EnsureResources(_renderer.GetGfxDevice(), _uiFrame);
		_imGuiRenderer.Record(context, _uiFrame, finalColor, lighting);
	}
}

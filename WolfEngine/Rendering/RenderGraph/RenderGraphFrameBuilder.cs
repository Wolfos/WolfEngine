#nullable enable

using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Rendering;

public readonly struct RenderGraphFrameResources
{
	public Int2 FramebufferSize { get; init; }
	public RenderGraphResourceHandle Backbuffer { get; init; }
	public RenderGraphResourceHandle GBufferAlbedo { get; init; }
	public RenderGraphResourceHandle GBufferNormal { get; init; }
	public RenderGraphResourceHandle GBufferMaterial { get; init; }
	public RenderGraphResourceHandle GBufferDepth { get; init; }
	public RenderGraphResourceHandle LightingBuffer { get; init; }
}

public sealed class RenderGraphFrameBuilder
{
	private readonly RenderGraphResourceRegistry _resources;
	private readonly IRenderer _renderer;
	private readonly DeferredLightingPass _deferredLightingPass;
	private RenderGraphFrameResources _frameResources;

	public RenderGraphFrameBuilder(RenderGraphResourceRegistry resources, IRenderer renderer,
		DeferredLightingPass deferredLightingPass)
	{
		_resources = resources;
		_renderer = renderer;
		_deferredLightingPass = deferredLightingPass;
	}

	public RenderGraphFrameResources BeginFrame(
		Int2 framebufferSize,
		RenderGraphResourceHandle backBuffer)
	{
		_frameResources = new()
		{
			FramebufferSize = framebufferSize,
			Backbuffer = backBuffer,
			GBufferAlbedo = _resources.CreateTransientTexture(new TextureDescriptor(framebufferSize.X,
				framebufferSize.Y, TextureFormat.Bgra8Unorm, TextureUsage.RenderTarget | TextureUsage.ShaderResource)),
			GBufferNormal = _resources.CreateTransientTexture(new TextureDescriptor(framebufferSize.X,
				framebufferSize.Y, TextureFormat.Rgba16Float, TextureUsage.RenderTarget | TextureUsage.ShaderResource)),
			GBufferMaterial = _resources.CreateTransientTexture(new TextureDescriptor(framebufferSize.X,
				framebufferSize.Y, TextureFormat.Rgba8Unorm, TextureUsage.RenderTarget | TextureUsage.ShaderResource)),
			GBufferDepth = _resources.CreateTransientTexture(new TextureDescriptor(framebufferSize.X, framebufferSize.Y,
				TextureFormat.D32Float, TextureUsage.DepthStencil | TextureUsage.ShaderResource)),
			LightingBuffer = _resources.CreateTransientTexture(new TextureDescriptor(
				framebufferSize.X,
				framebufferSize.Y,
				TextureFormat.Bgra8Unorm,
				TextureUsage.ShaderResource | TextureUsage.UnorderedAccess))
		};

		return _frameResources;
	}

	public bool Build(RenderGraph graph)
	{
		// Register GBuffer pass with proper resource states
		graph.AddPass("GBuffer", PassKind.Graphics)
			.WriteTexture(_frameResources.GBufferAlbedo, ResourceState.RenderTarget)
			.WriteTexture(_frameResources.GBufferNormal, ResourceState.RenderTarget)
			.WriteTexture(_frameResources.GBufferMaterial, ResourceState.RenderTarget)
			.WriteTexture(_frameResources.GBufferDepth, ResourceState.DepthWrite)
			.SetExecute(context =>
			{
				// SceneData is guaranteed to be non-null here as RenderGraph skips passes when null
				var albedoTexture = context.GetTexture(_frameResources.GBufferAlbedo);
				var normalTexture = context.GetTexture(_frameResources.GBufferNormal);
				var materialTexture = context.GetTexture(_frameResources.GBufferMaterial);
				var depthTexture = context.GetTexture(_frameResources.GBufferDepth);

				var gbufferConfig = new GBufferPassConfig
				{
					FramebufferWidth = _frameResources.FramebufferSize.X,
					FramebufferHeight = _frameResources.FramebufferSize.Y,
					AlbedoTarget = albedoTexture,
					NormalTarget = normalTexture,
					MaterialTarget = materialTexture,
					DepthTarget = depthTexture,
					AlbedoClearColor = new(0.392f, 0.584f, 0.929f, 1.0f),
					NormalClearColor = new(0.5f, 0.5f, 1.0f, 1.0f),
					MaterialClearColor = new(0.0f, 0.0f, 0.0f, 1.0f),
					DepthClearValue = 1.0f
				};

				GBufferPass.Record(context, gbufferConfig, context.SceneData!);
			});

		// Register Deferred Lighting pass with proper resource states
		graph.AddPass("Deferred Lighting", PassKind.Compute)
			.ReadTexture(_frameResources.GBufferAlbedo, ResourceState.ShaderResource)
			.ReadTexture(_frameResources.GBufferNormal, ResourceState.ShaderResource)
			.ReadTexture(_frameResources.GBufferMaterial, ResourceState.ShaderResource)
			.ReadTexture(_frameResources.GBufferDepth, ResourceState.ShaderResource)
			.WriteTexture(_frameResources.LightingBuffer, ResourceState.UnorderedAccess)
			.SetExecute(context =>
			{
				// SceneData is guaranteed to be non-null here as RenderGraph skips passes when null
				var config = _deferredLightingPass.BuildConfig(context, _frameResources, _renderer.GetGfxDevice());
				try
				{
					_deferredLightingPass.Record(context, ref config, context.SceneData!);
				}
				finally
				{
					config.SrvTable.Dispose();
					config.UavTable.Dispose();
				}
			});

		return true;
	}
}
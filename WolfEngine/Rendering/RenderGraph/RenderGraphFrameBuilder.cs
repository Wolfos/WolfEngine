#nullable enable

using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Rendering;

public readonly struct RenderGraphFrameResources
{
	public Int2 FramebufferSize { get; init; }
	public RenderGraphResourceHandle Backbuffer { get; init; }
	public RenderGraphResourceHandle Depth { get; init; }
	public RenderGraphResourceHandle GBufferAlbedo { get; init; }
	public RenderGraphResourceHandle GBufferNormal { get; init; }
	public RenderGraphResourceHandle GBufferMaterial { get; init; }
	public RenderGraphResourceHandle GBufferDepth { get; init; }
	public RenderGraphResourceHandle LightingBuffer { get; init; }
}

public sealed class RenderGraphFrameBuilder
{
	private readonly RenderGraphResourceRegistry _resources;
	private readonly WolfRendererD3D _renderer;
	private RenderGraphFrameResources _frameResources;

	public RenderGraphFrameBuilder(RenderGraphResourceRegistry resources, IRenderer renderer)
	{
		_resources = resources;
		_renderer = renderer as WolfRendererD3D
		            ?? throw new ArgumentException("Renderer must be WolfRendererD3D for now.", nameof(renderer));
	}

	public RenderGraphFrameResources BeginFrame(
		Int2 framebufferSize,
		RenderGraphResourceHandle backBuffer,
		RenderGraphResourceHandle depthTexture)
	{
		_frameResources = new()
		{
			FramebufferSize = framebufferSize,
			Backbuffer = backBuffer,
			Depth = depthTexture,
			GBufferAlbedo = _resources.CreateTransientTexture(new TextureDescriptor(framebufferSize.X,
				framebufferSize.Y, TextureFormat.Bgra8Unorm, TextureUsage.RenderTarget | TextureUsage.ShaderResource)),
			GBufferNormal = _resources.CreateTransientTexture(new TextureDescriptor(framebufferSize.X,
				framebufferSize.Y, TextureFormat.Rgba16Float, TextureUsage.RenderTarget | TextureUsage.ShaderResource)),
			GBufferMaterial = _resources.CreateTransientTexture(new TextureDescriptor(framebufferSize.X,
				framebufferSize.Y, TextureFormat.Rgba8Unorm, TextureUsage.RenderTarget | TextureUsage.ShaderResource)),
			GBufferDepth = _resources.CreateTransientTexture(new TextureDescriptor(framebufferSize.X, framebufferSize.Y,
				TextureFormat.D32Float, TextureUsage.DepthStencil | TextureUsage.ShaderResource)),
			LightingBuffer = _renderer.ImportLightingBuffer(_resources, framebufferSize.X, framebufferSize.Y)
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
					AlbedoClearColor = new[] { 0.392f, 0.584f, 0.929f, 1.0f }
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
				var config = BuildDeferredLightingConfig(context, _frameResources);
				DeferredLightingPass.Record(context, config, context.SceneData!);
			});
		
		return true;
	}

	private DeferredLightingPassConfig BuildDeferredLightingConfig(RenderGraphContext context, RenderGraphFrameResources resources)
	{
		// Build pipeline key for deferred lighting
			var pipelineKey = new PipelineKey(
				PassKind.Compute,
				vertexEntryPoint: null,
				pixelEntryPoint: null,
				computeEntryPoint: "CSMain",
				renderTargets: new RenderTargetFormats(Array.Empty<TextureFormat>()),
				depthStencil: new Abstraction.DepthStencilFormat(TextureFormat.Unknown),
				renderState: default);

		var pipeline = new Backend.D3D12.D3D12Pipeline(
			pipelineKey,
			PassKind.Compute,
			_renderer.LightingPipeline,
			_renderer.LightingRootSignature);

		var device = _renderer.GetGfxDevice();

		var srvTableBuilder = device.CreateDescriptorSetBuilder();
		srvTableBuilder.AddShaderResource(0, context.GetTexture(resources.GBufferAlbedo));
		srvTableBuilder.AddShaderResource(1, context.GetTexture(resources.GBufferNormal));
		srvTableBuilder.AddShaderResource(2, context.GetTexture(resources.GBufferMaterial));
		srvTableBuilder.AddShaderResource(3, context.GetTexture(resources.GBufferDepth));
		var srvTable = srvTableBuilder.Build();

		var uavTableBuilder = device.CreateDescriptorSetBuilder();
		uavTableBuilder.AddUnorderedAccess(0, context.GetTexture(resources.LightingBuffer));
		var uavTable = uavTableBuilder.Build();

		return new DeferredLightingPassConfig
		{
			Pipeline = pipeline,
			SrvTable = srvTable,
			UavTable = uavTable,
			DispatchSize = resources.FramebufferSize
		};
	}
}

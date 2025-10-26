#nullable enable

using System;
using WolfEngine.Mathematics;

namespace WolfEngine.Rendering;

public readonly record struct RenderPassCallbacks(
	Action<RenderGraphContext, RenderGraphFrameResources> ExecuteGBuffer,
	Func<RenderGraphContext, RenderGraphFrameResources, bool> ExecuteDeferred);

public readonly struct RenderGraphFrameResources
{
	public Int2 FramebufferSize { get; init; }
	public RenderGraphResourceHandle Backbuffer { get; init; }
	public RenderGraphResourceHandle Depth { get; init; }
	public RenderGraphResourceHandle GBufferAlbedo { get; init; }
	public RenderGraphResourceHandle GBufferNormal { get; init; }
	public RenderGraphResourceHandle GBufferMaterial { get; init; }
	public RenderGraphResourceHandle GBufferDepth { get; init; }
}

public sealed class RenderGraphFrameBuilder
{
	private readonly RenderGraphResourceRegistry _resources;
	private RenderGraphFrameResources _frameResources;

	public RenderGraphFrameBuilder(RenderGraphResourceRegistry resources)
	{
		_resources = resources;
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
				TextureFormat.D32Float, TextureUsage.DepthStencil))
		};

		return _frameResources;
	}

	public bool Build(RenderPassCallbacks callbacks, RenderGraph graph)
	{
		var renderedScene = false;

		graph.AddPass("GBuffer")
			.WriteTexture(_frameResources.GBufferAlbedo)
			.WriteTexture(_frameResources.GBufferNormal)
			.WriteTexture(_frameResources.GBufferMaterial)
			.WriteTexture(_frameResources.GBufferDepth)
			.SetExecute(context => callbacks.ExecuteGBuffer(context, _frameResources));

		graph.AddPass("Deferred Lighting")
			.WriteTexture(_frameResources.Backbuffer)
			.SetExecute(context => renderedScene = callbacks.ExecuteDeferred(context, _frameResources));
		
		return renderedScene;
	}
}

#nullable enable

using System;
using WolfEngine.Mathematics;

namespace WolfEngine.Rendering;

public readonly record struct RenderPassCallbacks(
	Action<RenderGraphContext, RenderGraphFrameResources> ExecuteGBuffer,
	Func<RenderGraphContext, RenderGraphFrameResources, bool> ExecuteForward);

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
	private readonly RenderGraph _graph;
	private RenderGraphFrameResources _frameResources;

	public RenderGraphFrameBuilder(RenderGraphResourceRegistry resources, RenderGraph graph)
	{
		_resources = resources;
		_graph = graph;
	}

	public RenderGraphFrameResources BeginFrame(
		Int2 framebufferSize,
		Func<RenderGraphResourceRegistry, int, int, RenderGraphResourceHandle> importBackbuffer,
		Func<RenderGraphResourceRegistry, int, int, RenderGraphResourceHandle> importDepth)
	{
		_graph.BeginFrame();

		_frameResources = new RenderGraphFrameResources
		{
			FramebufferSize = framebufferSize,
			Backbuffer = importBackbuffer(_resources, framebufferSize.X, framebufferSize.Y),
			Depth = importDepth(_resources, framebufferSize.X, framebufferSize.Y),
			GBufferAlbedo = _resources.CreateTransientTexture(new TextureDescriptor(framebufferSize.X, framebufferSize.Y, TextureFormat.Bgra8Unorm, TextureUsage.RenderTarget | TextureUsage.ShaderResource)),
			GBufferNormal = _resources.CreateTransientTexture(new TextureDescriptor(framebufferSize.X, framebufferSize.Y, TextureFormat.Rgba16Float, TextureUsage.RenderTarget | TextureUsage.ShaderResource)),
			GBufferMaterial = _resources.CreateTransientTexture(new TextureDescriptor(framebufferSize.X, framebufferSize.Y, TextureFormat.Rgba8Unorm, TextureUsage.RenderTarget | TextureUsage.ShaderResource)),
			GBufferDepth = _resources.CreateTransientTexture(new TextureDescriptor(framebufferSize.X, framebufferSize.Y, TextureFormat.D32Float, TextureUsage.DepthStencil))
		};

		return _frameResources;
	}

	public bool BuildAndExecute(RenderPassCallbacks callbacks)
	{
		var renderedScene = false;

		_graph.AddPass("GBuffer")
			.WriteTexture(_frameResources.GBufferAlbedo)
			.WriteTexture(_frameResources.GBufferNormal)
			.WriteTexture(_frameResources.GBufferMaterial)
			.WriteTexture(_frameResources.GBufferDepth)
			.SetExecute(context => callbacks.ExecuteGBuffer(context, _frameResources));

		_graph.AddPass("Forward Lighting")
			.WriteTexture(_frameResources.Backbuffer)
			.WriteTexture(_frameResources.Depth)
			.SetExecute(context => renderedScene = callbacks.ExecuteForward(context, _frameResources));

		_graph.Execute();

		return renderedScene;
	}

	public void EndFrame()
	{
		_graph.EndFrame();
	}
}

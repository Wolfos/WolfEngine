using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering;

/// <summary>
/// Entry point for recording and executing passes in the renderer's frame graph.
/// Responsible for owning pass order, compiling transient resources, and dispatching execution.
/// </summary>
public sealed class RenderGraph
{
	private readonly RenderGraphResourceRegistry _resourceRegistry;
	private readonly RenderGraphFrameBuilder _frameBuilder;
	private readonly IRenderer _renderer;
	private readonly List<RenderGraphPass> _passes = new();

	public RenderGraph(RenderGraphResourceRegistry resourceRegistry, IRenderer renderer, RenderGraphFrameBuilder frameBuilder)
	{
		_resourceRegistry = resourceRegistry;
		_renderer = renderer;
		_frameBuilder = frameBuilder;
	}
	

	public RenderGraphBuilder AddPass(string name)
	{
		var pass = new RenderGraphPass(name);
		_passes.Add(pass);
		return new RenderGraphBuilder(pass, _resourceRegistry);
	}

	public void Execute()
	{
		foreach (var pass in _passes)
		{
			foreach (var read in pass.Reads)
			{
				_resourceRegistry.GetTexture(read);
			}

			foreach (var write in pass.Writes)
			{
				_resourceRegistry.GetTexture(write);
			}

			var context = new RenderGraphContext(_resourceRegistry, pass.Name);
			pass.Execute(context);
		}

		_passes.Clear();
	}

	public void Startup(Action startup, Action update)
	{
		_renderer.Run(startup, update, OnRender);
		_resourceRegistry.SetDevice(_renderer.GetGfxDevice());
	}

	public void OnRender(float deltaTime)
	{
		_resourceRegistry.BeginFrame();
		_passes.Clear();
		
		_renderer.BeginFrame();

		var frameBufferSize = _renderer.GetFrameBufferSize();
		var backBuffer = _renderer.ImportBackbuffer(_resourceRegistry, frameBufferSize.X, frameBufferSize.Y);
		var depthTexture = _renderer.ImportDepthTexture(_resourceRegistry, frameBufferSize.X, frameBufferSize.Y);
		_frameBuilder.BeginFrame(frameBufferSize, backBuffer, depthTexture);
		
		
		//var callbacks = new RenderPassCallbacks()
		//_frameBuilder.Build(); // TODO: This is spaghet
		Execute();
		
		_renderer.Render(deltaTime, _resourceRegistry, backBuffer, depthTexture);
	}

	public IMaterialResources EnsureMaterialResources(Material material)
	{
		// TODO: Should probably be handled in resource registry
		return _renderer.CreateMaterialResources(material);
	}

	public void SubmitCommand(RenderCommand command)
	{
		_renderer.SubmitCommand(command);
	}

	public void EndFrame()
	{
		_resourceRegistry.EndFrame();
	}
}

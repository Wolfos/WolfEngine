namespace WolfEngine.Rendering;

/// <summary>
/// Entry point for recording and executing passes in the renderer's frame graph.
/// Responsible for owning pass order, compiling transient resources, and dispatching execution.
/// </summary>
public sealed class RenderGraph
{
	private readonly RenderGraphResourceRegistry _resourceRegistry;
	private readonly List<RenderGraphPass> _passes = new();

	public RenderGraph(RenderGraphResourceRegistry resourceRegistry)
	{
		_resourceRegistry = resourceRegistry ?? throw new ArgumentNullException(nameof(resourceRegistry));
	}

	public RenderGraphBuilder AddPass(string name)
	{
		var pass = new RenderGraphPass(name);
		_passes.Add(pass);
		return new RenderGraphBuilder(pass, _resourceRegistry);
	}

	public void Execute()
	{
		_resourceRegistry.BeginFrame();

		foreach (var pass in _passes)
		{
			var context = new RenderGraphContext(_resourceRegistry, pass.Name);
			pass.Execute(context);
		}

		_resourceRegistry.EndFrame();
		_passes.Clear();
	}
}

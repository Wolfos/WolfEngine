namespace WolfEngine.Rendering;

/// <summary>
/// Lightweight description of a render pass recorded into the graph.
/// Responsible for storing callbacks and metadata until execution time.
/// </summary>
public sealed class RenderGraphPass
{
	private Action<RenderGraphContext>? _execute;

	internal RenderGraphPass(string name)
	{
		Name = name ?? throw new ArgumentNullException(nameof(name));
	}

	public string Name { get; }

	internal void SetExecute(Action<RenderGraphContext> execute)
	{
		_execute = execute ?? throw new ArgumentNullException(nameof(execute));
	}

	internal void Execute(RenderGraphContext context)
	{
		if (_execute is null)
		{
			// Pass authoring should always supply an execute callback; early return keeps things safe during scaffolding.
			return;
		}

		_execute(context);
	}
}

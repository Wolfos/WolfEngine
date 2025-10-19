namespace WolfEngine.Rendering;

/// <summary>
/// Lightweight description of a render pass recorded into the graph.
/// Responsible for storing callbacks and metadata until execution time.
/// </summary>
public sealed class RenderGraphPass
{
private Action<RenderGraphContext> _execute = context => { };
	private readonly List<RenderGraphResourceHandle> _reads = new();
	private readonly List<RenderGraphResourceHandle> _writes = new();

	internal RenderGraphPass(string name)
	{
		Name = name ?? throw new ArgumentNullException(nameof(name));
	}

	public string Name { get; }

	internal IReadOnlyList<RenderGraphResourceHandle> Reads => _reads;

	internal IReadOnlyList<RenderGraphResourceHandle> Writes => _writes;

	internal void SetExecute(Action<RenderGraphContext> execute)
	{
		_execute = execute ?? throw new ArgumentNullException(nameof(execute));
	}

	internal void AddRead(RenderGraphResourceHandle handle)
	{
		if (handle.IsValid == false)
		{
			throw new ArgumentException("Handle is not valid.", nameof(handle));
		}

		if (_reads.Contains(handle) == false)
		{
			_reads.Add(handle);
		}
	}

	internal void AddWrite(RenderGraphResourceHandle handle)
	{
		if (handle.IsValid == false)
		{
			throw new ArgumentException("Handle is not valid.", nameof(handle));
		}

		if (_writes.Contains(handle) == false)
		{
			_writes.Add(handle);
		}
	}

	internal void Execute(RenderGraphContext context)
	{
	_execute(context);
	}
}

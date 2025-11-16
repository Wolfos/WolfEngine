using WolfEngine.Rendering.Abstraction;

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
	private readonly List<ResourceUsage> _resourceUsages = new();
	private readonly List<ResourceBarrierDescription> _barriers = new();

	internal RenderGraphPass(string name, PassKind kind = PassKind.Graphics)
	{
		Name = name ?? throw new ArgumentNullException(nameof(name));
		Kind = kind;
	}

	public string Name { get; }
	
	public PassKind Kind { get; }

	internal IReadOnlyList<RenderGraphResourceHandle> Reads => _reads;

	internal IReadOnlyList<RenderGraphResourceHandle> Writes => _writes;
	
	internal IReadOnlyList<ResourceUsage> ResourceUsages => _resourceUsages;
	
	internal IReadOnlyList<ResourceBarrierDescription> Barriers => _barriers;

	internal void SetExecute(Action<RenderGraphContext> execute)
	{
		_execute = execute ?? throw new ArgumentNullException(nameof(execute));
	}

	internal void AddRead(RenderGraphResourceHandle handle, ResourceState state = ResourceState.ShaderResource)
	{
		if (handle.IsValid == false)
		{
			throw new ArgumentException("Handle is not valid.", nameof(handle));
		}

		if (_reads.Contains(handle) == false)
		{
			_reads.Add(handle);
		}
		
		_resourceUsages.Add(new ResourceUsage(handle, ResourceAccessMode.Read, state));
	}

	internal void AddWrite(RenderGraphResourceHandle handle, ResourceState state = ResourceState.RenderTarget)
	{
		if (handle.IsValid == false)
		{
			throw new ArgumentException("Handle is not valid.", nameof(handle));
		}

		if (_writes.Contains(handle) == false)
		{
			_writes.Add(handle);
		}
		
		_resourceUsages.Add(new ResourceUsage(handle, ResourceAccessMode.Write, state));
	}

	internal void AddBarrier(ResourceBarrierDescription barrier)
	{
		_barriers.Add(barrier);
	}

	internal void Execute(RenderGraphContext context)
	{
	_execute(context);
	}
}

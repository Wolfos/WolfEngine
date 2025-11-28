#nullable enable

using WolfEngine.Rendering.Abstraction;
using WolfEngine.Utility;

namespace WolfEngine.Rendering;

/// <summary>
/// Analyzes pass dependencies and generates resource barriers for the render graph.
/// </summary>
public sealed class RenderGraphCompiler
{
	private readonly RenderGraphResourceRegistry _registry;
	private readonly Dictionary<int, ResourceState> _resourceStates = new();

	public RenderGraphCompiler(RenderGraphResourceRegistry registry)
	{
		_registry = registry ?? throw new ArgumentNullException(nameof(registry));
	}

	/// <summary>
	/// Compiles barriers for all passes based on resource usage patterns.
	/// </summary>
	public void Compile(IReadOnlyList<RenderGraphPass> passes)
	{
		if (passes == null || passes.Count == 0)
		{
			return;
		}

		// Track the last state each resource was in
		var resourceStates = _resourceStates;
		resourceStates.Clear();
		

		for(int i = 0; i < passes.Count; i++)
		{
			var pass = passes[i];
			Profiler.StartBlock(pass.Name);

			// Generate barriers for resources this pass uses
			for (var j = 0; j < pass.ResourceUsages.Count; j++)
			{
				var usage = pass.ResourceUsages[j];
				var currentState = resourceStates.TryGetValue(usage.Handle.Id, out var state)
					? state
					: _registry.GetResourceState(usage.Handle);
				
				// If the required state differs from current state, we need a barrier
				if (currentState != usage.State && (currentState & usage.State) != usage.State)
				{
					var resource = _registry.GetResource(usage.Handle);
					
					var barrier = new ResourceBarrierDescription(resource, currentState, usage.State);
					pass.AddBarrier(barrier);

					// Update tracked state
					resourceStates[usage.Handle.Id] = usage.State;
				}
				else
				{
					// Even if no barrier needed, update state tracking
					resourceStates[usage.Handle.Id] = usage.State;
				}
			}
			
			Profiler.EndBlock(pass.Name);

		}
		


		// Update registry with final states
		foreach (var (handleId, finalState) in resourceStates)
		{
			var handle = new RenderGraphResourceHandle(handleId);
			_registry.SetResourceState(handle, finalState);
		}
		

		resourceStates.Clear();
	}

	/// <summary>
	/// Validates that all resources used by passes are properly registered.
	/// </summary>
	public void Validate(IReadOnlyList<RenderGraphPass> passes)
	{
		foreach (var pass in passes)
		{
			foreach (var usage in pass.ResourceUsages)
			{
				try
				{
					_ = _registry.GetResource(usage.Handle);
				}
				catch (InvalidOperationException ex)
				{
					throw new InvalidOperationException(
						$"Pass '{pass.Name}' references unregistered resource handle {usage.Handle.Id}.", ex);
				}
			}
		}
	}
}


#nullable enable

using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering;

/// <summary>
/// Analyzes pass dependencies and generates resource barriers for the render graph.
/// </summary>
public sealed class RenderGraphCompiler
{
	private readonly RenderGraphResourceRegistry _registry;

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
		var resourceStates = new Dictionary<int, ResourceState>();

		foreach (var pass in passes)
		{
			// Generate barriers for resources this pass uses
			foreach (var usage in pass.ResourceUsages)
			{
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
		}

		// Update registry with final states
		foreach (var (handleId, finalState) in resourceStates)
		{
			var handle = new RenderGraphResourceHandle(handleId);
			_registry.SetResourceState(handle, finalState);
		}
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


#nullable enable

using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering;

/// <summary>
/// Analyzes pass dependencies and generates resource barriers for the render graph.
/// </summary>
public sealed class RenderGraphCompiler
{
	private readonly struct TransientLifetime
	{
		public TransientLifetime(int handleId, int firstUse, int lastUse, TextureDescriptor descriptor)
		{
			HandleId = handleId;
			FirstUse = firstUse;
			LastUse = lastUse;
			Descriptor = descriptor;
		}

		public int HandleId { get; }
		public int FirstUse { get; }
		public int LastUse { get; }
		public TextureDescriptor Descriptor { get; }
	}

	private struct AliasSlot
	{
		public AliasSlot(int slotId, int lastUse, TextureDescriptor descriptor)
		{
			SlotId = slotId;
			LastUse = lastUse;
			Descriptor = descriptor;
		}

		public int SlotId;
		public int LastUse;
		public TextureDescriptor Descriptor;
	}

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

		var aliasAssignments = BuildTransientAliasAssignments(passes);
		_registry.AssignTransientTextureSlots(aliasAssignments);

		var resourceStates = _resourceStates;
		resourceStates.Clear();

		for (var passIndex = 0; passIndex < passes.Count; passIndex++)
		{
			var pass = passes[passIndex];

			for (var usageIndex = 0; usageIndex < pass.ResourceUsages.Count; usageIndex++)
			{
				var usage = pass.ResourceUsages[usageIndex];
				var resource = _registry.GetResource(usage.Handle);
				var trackingKey = _registry.GetStateTrackingKey(usage.Handle);
				var currentState = resourceStates.TryGetValue(trackingKey, out var state)
					? state
					: _registry.GetResourceStateByTrackingKey(usage.Handle, trackingKey);

				if (currentState != usage.State && (currentState & usage.State) != usage.State)
				{
					var barrier = new ResourceBarrierDescription(resource, currentState, usage.State);
					pass.AddBarrier(barrier);
				}

				resourceStates[trackingKey] = usage.State;
			}
		}

		foreach (var (trackingKey, finalState) in resourceStates)
		{
			_registry.SetResourceStateByTrackingKey(trackingKey, finalState);
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

	private Dictionary<int, int> BuildTransientAliasAssignments(IReadOnlyList<RenderGraphPass> passes)
	{
		var lifetimesByHandle = new Dictionary<int, (int FirstUse, int LastUse, TextureDescriptor Descriptor)>();
		for (var passIndex = 0; passIndex < passes.Count; passIndex++)
		{
			var pass = passes[passIndex];
			for (var usageIndex = 0; usageIndex < pass.ResourceUsages.Count; usageIndex++)
			{
				var usage = pass.ResourceUsages[usageIndex];
				if (_registry.TryGetTransientTextureDescriptor(usage.Handle, out var descriptor) == false)
				{
					continue;
				}

				var handleId = usage.Handle.Id;
				if (lifetimesByHandle.TryGetValue(handleId, out var lifetime))
				{
					lifetime.LastUse = passIndex;
					lifetimesByHandle[handleId] = lifetime;
				}
				else
				{
					lifetimesByHandle[handleId] = (passIndex, passIndex, descriptor);
				}
			}
		}

		if (lifetimesByHandle.Count == 0)
		{
			return new Dictionary<int, int>();
		}

		var lifetimes = new List<TransientLifetime>(lifetimesByHandle.Count);
		foreach (var (handleId, lifetime) in lifetimesByHandle)
		{
			lifetimes.Add(new TransientLifetime(handleId, lifetime.FirstUse, lifetime.LastUse, lifetime.Descriptor));
		}

		lifetimes.Sort(static (a, b) =>
		{
			var firstCmp = a.FirstUse.CompareTo(b.FirstUse);
			return firstCmp != 0 ? firstCmp : a.LastUse.CompareTo(b.LastUse);
		});

		var assignments = new Dictionary<int, int>(lifetimes.Count);
		var slots = new List<AliasSlot>();
		var nextSlotId = 1;
		for (var i = 0; i < lifetimes.Count; i++)
		{
			var lifetime = lifetimes[i];
			var assignedSlot = 0;
			for (var slotIndex = 0; slotIndex < slots.Count; slotIndex++)
			{
				var slot = slots[slotIndex];
				if (slot.LastUse >= lifetime.FirstUse)
				{
					continue;
				}

				if (AreDescriptorsCompatible(slot.Descriptor, lifetime.Descriptor) == false)
				{
					continue;
				}

				slot.LastUse = lifetime.LastUse;
				slots[slotIndex] = slot;
				assignedSlot = slot.SlotId;
				break;
			}

			if (assignedSlot == 0)
			{
				assignedSlot = nextSlotId++;
				slots.Add(new AliasSlot(assignedSlot, lifetime.LastUse, lifetime.Descriptor));
			}

			assignments[lifetime.HandleId] = assignedSlot;
		}

		return assignments;
	}

	private static bool AreDescriptorsCompatible(TextureDescriptor a, TextureDescriptor b)
	{
		return a.Width == b.Width &&
		       a.Height == b.Height &&
		       a.Format == b.Format &&
		       a.Usage == b.Usage &&
		       a.ClearColor.Equals(b.ClearColor) &&
		       a.DepthClear.Equals(b.DepthClear);
	}
}

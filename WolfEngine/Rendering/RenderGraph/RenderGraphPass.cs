using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering;

/// <summary>
/// Lightweight description of a render pass recorded into the graph.
/// Responsible for storing callbacks and metadata until execution time.
/// </summary>
public sealed class RenderGraphPass
{
	private Action<RenderGraphContext> _execute = static _ => { };
	private readonly List<RenderGraphResourceHandle> _reads = new();
	private readonly List<RenderGraphResourceHandle> _writes = new();
	private readonly List<ResourceUsage> _resourceUsages = new();
	private readonly List<ResourceBarrierDescription> _barriers = new();

	internal RenderGraphPass()
	{
		Name = string.Empty;
	}

	internal RenderGraphPass(string name, PassKind kind = PassKind.Graphics)
	{
		Configure(name, kind);
	}

	public string Name { get; private set; }
	
	public PassKind Kind { get; private set; }

	[MemberNotNull(nameof(Name))]
	internal void Configure(string name, PassKind kind)
	{
		Name = name ?? throw new ArgumentNullException(nameof(name));
		Kind = kind;
		Clear();
	}

	internal void Clear()
	{
		_reads.Clear();
		_writes.Clear();
		_resourceUsages.Clear();
		_barriers.Clear();
		_execute = static _ => { };
	}

	internal IReadOnlyList<RenderGraphResourceHandle> Reads => _reads;

	internal IReadOnlyList<RenderGraphResourceHandle> Writes => _writes;
	
	internal IReadOnlyList<ResourceUsage> ResourceUsages => _resourceUsages;
	
	internal IReadOnlyList<ResourceBarrierDescription> Barriers => _barriers;

	/// <summary>The pass's barriers as a span, so they can be submitted in one batched call.</summary>
	internal ReadOnlySpan<ResourceBarrierDescription> BarrierSpan => CollectionsMarshal.AsSpan(_barriers);

	internal void SetExecute(Action<RenderGraphContext> execute)
	{
		_execute = execute;
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

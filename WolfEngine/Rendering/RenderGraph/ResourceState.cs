using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering;

/// <summary>
/// Describes how a pass accesses a resource.
/// </summary>
public enum ResourceAccessMode
{
	Read,
	Write,
	ReadWrite
}

/// <summary>
/// Tracks a single resource usage within a pass.
/// </summary>
public readonly struct ResourceUsage
{
	public ResourceUsage(RenderGraphResourceHandle handle, ResourceAccessMode accessMode, ResourceState state)
	{
		Handle = handle;
		AccessMode = accessMode;
		State = state;
	}

	public RenderGraphResourceHandle Handle { get; }
	public ResourceAccessMode AccessMode { get; }
	public ResourceState State { get; }
}


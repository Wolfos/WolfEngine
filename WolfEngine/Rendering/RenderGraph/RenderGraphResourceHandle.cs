namespace WolfEngine.Rendering;

/// <summary>
/// Lightweight identifier handed out when a pass declares a resource dependency.
/// Responsible for staying stable across setup/execute phases so the registry can resolve a concrete allocation.
/// </summary>
public readonly struct RenderGraphResourceHandle
{
	internal RenderGraphResourceHandle(int id)
	{
		Id = id;
	}

	internal int Id { get; }

	public bool IsValid => Id != 0;
}

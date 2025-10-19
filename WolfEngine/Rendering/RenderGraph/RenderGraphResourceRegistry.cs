namespace WolfEngine.Rendering;

/// <summary>
/// Central book-keeping for logical render graph resources.
/// Responsible for allocating transient handles and resolving them to real GPU allocations later.
/// </summary>
public sealed class RenderGraphResourceRegistry
{
	private int _nextHandleId = 1;
	private readonly Dictionary<int, TextureDescriptor> _transientTextures = new();

	public void BeginFrame()
	{
		_transientTextures.Clear();
		_nextHandleId = 1;
	}

	public void EndFrame()
	{
		// Will later release transient resources or return them to pools.
	}

	public RenderGraphResourceHandle CreateTransientTexture(in TextureDescriptor descriptor)
	{
		var handle = new RenderGraphResourceHandle(_nextHandleId++);
		_transientTextures[handle.Id] = descriptor;
		return handle;
	}

	public TextureDescriptor GetTextureDescriptor(RenderGraphResourceHandle handle)
	{
		return _transientTextures[handle.Id];
	}
}

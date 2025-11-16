using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering;

/// <summary>
/// Fluent helper exposed when authoring a render pass.
/// Responsible for declaring resource reads/writes and supplying the execution callback.
/// </summary>
public sealed class RenderGraphBuilder
{
	private readonly RenderGraphPass _pass;
	private readonly RenderGraphResourceRegistry _resourceRegistry;

	internal RenderGraphBuilder(RenderGraphPass pass, RenderGraphResourceRegistry resourceRegistry)
	{
		_pass = pass;
		_resourceRegistry = resourceRegistry;
	}

	public RenderGraphResourceHandle CreateTransientTexture(in TextureDescriptor descriptor)
	{
		return _resourceRegistry.CreateTransientTexture(descriptor);
	}
	
	public RenderGraphResourceHandle CreateTransientBuffer(in BufferDescriptor descriptor)
	{
		return _resourceRegistry.CreateTransientBuffer(descriptor);
	}

	public RenderGraphBuilder ReadTexture(RenderGraphResourceHandle handle, ResourceState state = ResourceState.ShaderResource)
	{
		_pass.AddRead(handle, state);
		return this;
	}

	public RenderGraphBuilder WriteTexture(RenderGraphResourceHandle handle, ResourceState state = ResourceState.RenderTarget)
	{
		_pass.AddWrite(handle, state);
		return this;
	}

	public void SetExecute(Action<RenderGraphContext> execute)
	{
		_pass.SetExecute(execute ?? throw new ArgumentNullException(nameof(execute)));
	}
}

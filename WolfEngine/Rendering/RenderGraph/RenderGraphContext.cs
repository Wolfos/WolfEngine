using System;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering;

/// <summary>
/// Runtime data handed to a pass while it is executing.
/// Responsible for exposing resolved GPU resources and frame-level services.
/// </summary>
public sealed class RenderGraphContext
{
	private IGfxCommandList _commandList;
	private SceneDrawData _sceneData;

	internal RenderGraphContext(RenderGraphResourceRegistry resourceRegistry, string passName)
	{
		ResourceRegistry = resourceRegistry ?? throw new ArgumentNullException(nameof(resourceRegistry));
		PassName = passName ?? throw new ArgumentNullException(nameof(passName));
	}

	public string PassName { get; }

	public RenderGraphResourceRegistry ResourceRegistry { get; }
	
	public IGfxCommandList CommandList
	{
		get => _commandList ?? throw new InvalidOperationException("Command list has not been set for this pass.");
		internal set => _commandList = value;
	}
	
	public SceneDrawData SceneData
	{
		get => _sceneData;
		internal set => _sceneData = value;
	}

	public IGfxTexture GetTexture(RenderGraphResourceHandle handle)
	{
		return ResourceRegistry.GetTexture(handle);
	}
	
	public IGfxBuffer GetBuffer(RenderGraphResourceHandle handle)
	{
		return ResourceRegistry.GetBuffer(handle);
	}
}

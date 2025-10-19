namespace WolfEngine.Rendering;

/// <summary>
/// Runtime data handed to a pass while it is executing.
/// Responsible for exposing resolved GPU resources and frame-level services.
/// </summary>
public sealed class RenderGraphContext
{
	internal RenderGraphContext(RenderGraphResourceRegistry resourceRegistry, string passName)
	{
		ResourceRegistry = resourceRegistry ?? throw new ArgumentNullException(nameof(resourceRegistry));
		PassName = passName ?? throw new ArgumentNullException(nameof(passName));
	}

	public string PassName { get; }

	public RenderGraphResourceRegistry ResourceRegistry { get; }
}

namespace WolfEngine.Rendering;

public interface IEditorSceneOverlayHook
{
	bool SupportsSceneViewportRenderTarget { get; }
	void BuildOverlayPasses(RenderGraph graph, in RenderGraphFrameResources frameResources);
}

public sealed class MetalEditorSceneOverlayHook : IEditorSceneOverlayHook
{
	public bool SupportsSceneViewportRenderTarget => true;

	public void BuildOverlayPasses(RenderGraph graph, in RenderGraphFrameResources frameResources)
	{
		// Reserved extension point for Metal scene overlay rendering.
	}
}

public sealed class D3DEditorSceneOverlayHook : IEditorSceneOverlayHook
{
	public bool SupportsSceneViewportRenderTarget => false;

	public void BuildOverlayPasses(RenderGraph graph, in RenderGraphFrameResources frameResources)
	{
		throw new NotImplementedException("D3D editor scene overlay passes are not implemented.");
	}
}

public sealed class NullEditorSceneOverlayHook : IEditorSceneOverlayHook
{
	public bool SupportsSceneViewportRenderTarget => false;

	public void BuildOverlayPasses(RenderGraph graph, in RenderGraphFrameResources frameResources)
	{
	}
}

using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.UI;

internal sealed class NullImGuiRenderer : IImGuiRenderer
{
	public static readonly NullImGuiRenderer Instance = new();

	public void EnsureResources(IGfxDevice device, UiFrameData frame)
	{
	}

	public void Record(RenderGraphContext context, UiFrameData frame, IGfxTexture finalColorTarget, IGfxTexture lightingSource)
	{
	}
}

using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.UI;

public sealed class NullImGuiRenderer : IImGuiRenderer
{
	public static readonly NullImGuiRenderer Instance = new();

	public void EnsureResources(IGfxDevice device, UiFrameData frame)
	{
	}

	public void InvalidateShaderPipeline()
	{
	}

	public void Record(RenderGraphContext context, UiFrameData frame, IGfxTexture finalColorTarget, bool clearTarget)
	{
	}
}

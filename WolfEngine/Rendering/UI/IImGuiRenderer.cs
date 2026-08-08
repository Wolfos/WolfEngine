using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.UI;

/// <summary>Backend-specific renderer that consumes producer-neutral UI frame data.</summary>
public interface IUiDrawRenderer
{
	void EnsureResources(IGfxDevice device, UiFrameData frame);

	void InvalidateShaderPipeline();

	void Record(RenderGraphContext context, UiFrameData frame, IGfxTexture finalColorTarget, bool clearTarget,
		ColorRGBA? clearColor = null);
}

/// <summary>Editor-facing UI renderer service. ImGui is only one producer of the shared draw packets.</summary>
public interface IImGuiRenderer : IUiDrawRenderer
{
}

using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.UI;

/// <summary>
/// Backend-specific ImGui renderer that consumes UI frame data and records draw calls.
/// </summary>
public interface IImGuiRenderer
{
	void EnsureResources(IGfxDevice device, UiFrameData frame);

	void InvalidateShaderPipeline();

	void Record(RenderGraphContext context, UiFrameData frame, IGfxTexture finalColorTarget, bool clearTarget);
}

using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.UI;
using WolfEngine.Rendering;

namespace WolfEngine;

public interface IRenderer
{
	void Run(Action startup, Action<float> update, Action<float> render);
	IMaterialResources CreateMaterialResources(Material material);
	ITextureResources CreateTextureResources(Texture texture);
	IGfxDevice GetGfxDevice();
	Int2 GetFrameBufferSize();
	Int2 GetWindowSize();
	void BeginFrame();
	void Render(
		RenderGraphResourceRegistry resourceRegistry,
		RenderGraphResourceHandle backBuffer,
		RenderGraphResourceHandle presentedTexture);
	RenderGraphResourceHandle ImportBackbuffer(RenderGraphResourceRegistry registry, int width, int height);
	void EnsureMeshResources(Mesh mesh);
	IImGuiRenderer GetImGuiRenderer();
}

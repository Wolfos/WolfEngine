using WolfEngine.Mathematics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine;

public interface IRenderer
{
	void Run(Action startup, Action<float> update, Action<float> render);
	IMaterialResources CreateMaterialResources(Material material);
	IGfxDevice GetGfxDevice();
	Int2 GetFrameBufferSize();
	void BeginFrame();
	void Render(float deltaTime, RenderGraphResourceRegistry resourceRegistry, RenderGraphResourceHandle backBuffer, RenderGraphResourceHandle depthTexture);
	RenderGraphResourceHandle ImportBackbuffer(RenderGraphResourceRegistry registry, int width, int height);
	RenderGraphResourceHandle ImportDepthTexture(RenderGraphResourceRegistry registry, int width, int height);
	void EnsureMeshResources(Mesh mesh);
}

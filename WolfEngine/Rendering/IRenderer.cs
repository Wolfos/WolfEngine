using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;
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
		RenderGraphResourceHandle backBuffer);
	RenderGraphResourceHandle ImportBackbuffer(RenderGraphResourceRegistry registry, int width, int height);
	void EnsureMeshResources(Mesh mesh);
	bool SupportsGpuCapture { get; }
	bool IsGpuCaptureActive { get; }
	string LastGpuCapturePath { get; }
	bool TryStartGpuCapture(string outputPath, out string error);
	bool TryStopGpuCapture(out string error);
}

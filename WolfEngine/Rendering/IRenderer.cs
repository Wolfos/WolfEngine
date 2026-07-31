using System;
using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering;

namespace WolfEngine;

public interface IRenderer : IFrameCaptureSource
{
	void SetWindowSize(Int2 size) => throw new PlatformNotSupportedException("Window configuration is not supported by this renderer.");
	void Run(Action startup, Action<float> update, Action<float> render);
	IMaterialResources CreateMaterialResources(Material material);
	ITextureResources CreateTextureResources(Texture texture);
	bool TryUpdateTextureResources(Texture texture, ITextureResources resources) => false;
	IGfxDevice GetGfxDevice();
	Int2 GetFrameBufferSize();
	Int2 GetWindowSize();
	void BeginFrame();
	void Render(
		RenderGraphResourceRegistry resourceRegistry,
		RenderGraphResourceHandle finalColor);
	void CompletePendingFrameCapture(RenderGraphResourceRegistry resourceRegistry, RenderGraphResourceHandle sceneColor) { }
	RenderGraphResourceHandle ImportBackbuffer(RenderGraphResourceRegistry registry, int width, int height);
	void EnsureMeshResources(Mesh mesh);
	void ReleaseMeshResources(Mesh mesh);
	IGfxBuffer GetPackedMeshVertexBuffer();
	IGfxBuffer GetPackedMeshIndexBuffer();

	/// <summary>
	/// Stride of the packed mesh vertex stream. Every mesh shares one packed buffer at one stride,
	/// which is what lets the shared draw passes bind geometry once instead of per indirect command.
	/// </summary>
	uint GetPackedMeshVertexStride();
	bool SupportsGpuCapture { get; }
	bool IsGpuCaptureActive { get; }
	string LastGpuCapturePath { get; }
	bool TryStartGpuCapture(string outputPath, out string error);
	bool TryStopGpuCapture(out string error);
}

#nullable enable

using System;

namespace WolfEngine.Rendering.Abstraction;

/// <summary>
/// API-neutral command list used by the render graph to encode graphics or compute work.
/// </summary>
public interface IGfxCommandList
{
	void BeginPass(in PassTargets targets, in Viewport viewport);

	void EndPass();

	void BindPipeline(IGfxPipeline pipeline);

	void SetScissorRect(in RectInt rect);

	void ClearColorAttachment(uint index, ReadOnlySpan<float> color);

	void ClearDepthStencil(float depth);

	void SetBindlessTable(IGfxDescriptorTable table);

	void PushConstants<T>(in T data) where T : unmanaged;

	void SetVertexBuffers(ReadOnlySpan<VertexBufferView> vertexBuffers);

	void SetIndexBuffer(in IndexBufferView indexBuffer);

	void Draw(in DrawArguments arguments);

	void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ);

	void Barrier(in ResourceBarrierDescription barrier);
}

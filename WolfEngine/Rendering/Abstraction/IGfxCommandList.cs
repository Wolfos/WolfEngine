#nullable enable

namespace WolfEngine.Rendering.Abstraction;

/// <summary>
/// Defines the primitive topology for rendering.
/// </summary>
public enum PrimitiveTopology
{
	TriangleList,
	TriangleStrip,
	LineList,
	LineStrip,
	PointList
}

/// <summary>
/// API-neutral command list used by the render graph to encode graphics or compute work.
/// </summary>
public interface IGfxCommandList
{
	void BeginPass(in PassTargets targets, in Viewport viewport);

	void EndPass();

	void BindPipeline(IGfxPipeline pipeline);

	void SetPrimitiveTopology(PrimitiveTopology topology);

	void SetScissorRect(in RectInt rect);

	void ClearColorAttachment(uint index, ReadOnlySpan<float> color);

	void ClearDepthStencil(float depth);

	void SetBindlessTable(IGfxDescriptorTable table);

	void BindConstantBuffer(uint slot, IGfxBuffer buffer, ulong offset = 0);

	void SetGraphicsConstants(uint slot, ReadOnlySpan<byte> data);

	void SetComputeConstants(uint slot, ReadOnlySpan<byte> data);

	void PushConstants<T>(in T data) where T : unmanaged;

	void SetVertexBuffers(ReadOnlySpan<VertexBufferView> vertexBuffers);

	void SetIndexBuffer(in IndexBufferView indexBuffer);

	void Draw(in DrawArguments arguments);

	void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ);

	void Barrier(in ResourceBarrierDescription barrier);
}

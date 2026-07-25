#nullable enable

using System;

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
	GraphicsBackendKind BackendKind { get; }

	/// <summary>
	/// Begins a named command scope. Backends may use this boundary to finish encoding work from the previous scope.
	/// </summary>
	void BeginEvent(string name) { }

	void EndEvent() { }

	void BeginPass(in PassTargets targets, in Viewport viewport);

	void EndPass();

	void BindPipeline(IGfxPipeline pipeline);

	void SetPrimitiveTopology(PrimitiveTopology topology);

	void SetScissorRect(in RectInt rect);

	void ClearColorAttachment(uint index, ColorRGBA color);

	void ClearDepthStencil(float depth);

	void BindGraphicsDescriptorSet(uint slot, IGfxDescriptorSet descriptorSet);

	void BindComputeDescriptorSet(uint slot, IGfxDescriptorSet descriptorSet);

	void SetBindlessTable(IGfxDescriptorTable table);

	void BindConstantBuffer(uint slot, IGfxBuffer buffer, ulong offset = 0);

	void SetGraphicsConstants(uint slot, ReadOnlySpan<byte> data);

	void SetComputeConstants(uint slot, ReadOnlySpan<byte> data);

	void SetComputeBuffer(uint slot, IGfxBuffer buffer, ulong offset = 0);

	/// <summary>
	/// Binds a compute buffer as a read-only shader resource.
	/// </summary>
	void SetComputeReadOnlyBuffer(uint slot, IGfxBuffer buffer, ulong offset = 0);

	void PushConstants<T>(in T data) where T : unmanaged;

	void SetVertexBuffer(in VertexBufferView vertexBuffer);

	void SetVertexBuffers(ReadOnlySpan<VertexBufferView> vertexBuffers);

	void SetIndexBuffer(in IndexBufferView indexBuffer);

	void Draw(in DrawArguments arguments);

	void DrawIndexedIndirect(in IndexBufferView indexBuffer, IGfxBuffer indirectArgsBuffer, ulong indirectArgsOffset);

	void ExecuteIndirectCommandBuffer(IGfxIndirectCommandBuffer commandBuffer, uint maxCommandCount);

	void ExecuteIndirectCommandBufferRange(
		IGfxIndirectCommandBuffer commandBuffer,
		IGfxBuffer commandRangeBuffer,
		ulong commandRangeOffsetBytes);

	void BuildBottomLevelAccelerationStructure(IGfxBottomLevelAccelerationStructure accelerationStructure);

	void BuildTopLevelAccelerationStructure(
		IGfxTopLevelAccelerationStructure accelerationStructure,
		ReadOnlySpan<RayTracingInstanceDescription> instances);

	void SynchronizeAccelerationStructureBuildForComputeRead(IGfxTopLevelAccelerationStructure accelerationStructure);

	void SetComputeAccelerationStructure(uint slot, IGfxTopLevelAccelerationStructure accelerationStructure);

	void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ);

	void CopyBuffer(IGfxBuffer source, ulong sourceOffset, IGfxBuffer destination, ulong destinationOffset, ulong sizeInBytes);

	void Barrier(in ResourceBarrierDescription barrier);
}

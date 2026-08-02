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
/// One page of commands to compact, for backends whose commands are opaque objects rather than
/// records a shader can copy. The visibility rules are the engine's, so everything the shared
/// compaction kernel tests is passed through here and the backend only owns the copy.
/// </summary>
/// <param name="ExecutionRangeIndex">
/// Index of this page's <c>{ location, length }</c> entry in <paramref name="ExecutionRangeBuffer"/>.
/// Compaction accumulates the length, which doubles as the allocator for dense destination slots.
/// </param>
/// <param name="DrawArgsBaseOffsetBytes">
/// Byte offset of the view's slice of the draw args, so shadow cascades compact against their own
/// visibility rather than the first cascade's.
/// </param>
/// <param name="LaneIndex">
/// Execution lane that owns this page's draws. Every lane allocates a page over the same draw id
/// range, so a draw is emitted only by the lane its bucket names.
/// </param>
public readonly record struct NativeIndirectCompactionRequest(
	IGfxIndirectCommandBuffer CommandBuffer,
	IGfxBuffer ExecutionRangeBuffer,
	uint ExecutionRangeIndex,
	IGfxBuffer DrawArgsBuffer,
	ulong DrawArgsBaseOffsetBytes,
	IGfxBuffer DrawCommandBuffer,
	uint PageStartCommandIndex,
	uint PageCommandCapacity,
	uint LaneIndex,
	uint ActiveDrawCommandUpperBound);

/// <summary>
/// API-neutral command list used by the render graph to encode graphics or compute work.
/// </summary>
public interface IGfxCommandList
{
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

	/// <summary>
	/// Executes the compacted commands of <paramref name="commandBuffer"/>, taking how many to walk
	/// from GPU memory so culling, not the size of the scene, decides the cost.
	/// Only valid when the buffer's <see cref="IGfxIndirectCommandBuffer.CompactionKind"/> is not
	/// <see cref="IndirectCompactionKind.None"/>.
	/// </summary>
	/// <param name="executionRangeOffsetBytes">
	/// Offset of this page's entry in <paramref name="executionRangeBuffer"/>. Entries are two uints,
	/// <c>{ location, length }</c>; compaction always emits from zero, so backends that want only a
	/// count read the second uint.
	/// </param>
	void ExecuteCompactedIndirectCommandBuffer(
		IGfxIndirectCommandBuffer commandBuffer,
		IGfxBuffer executionRangeBuffer,
		ulong executionRangeOffsetBytes);

	/// <summary>
	/// Records the GPU work that compacts one page of commands down to the draws culling left visible.
	/// Only backends reporting <see cref="IndirectCompactionKind.NativeCommands"/> implement this; the
	/// rest expose their command records instead and are compacted by the engine's shared kernel.
	/// </summary>
	void RecordNativeIndirectCompaction(in NativeIndirectCompactionRequest request) =>
		throw new NotSupportedException(
			$"{GetType().Name} does not record native indirect command compaction.");

	/// <summary>
	/// Zeroes the execution range table ahead of the compaction dispatches that accumulate into it.
	/// Paired with <see cref="RecordNativeIndirectCompaction"/>, and separate from it because the table
	/// is plain GPU memory that frames still in flight are reading: the reset has to be ordered on the
	/// GPU timeline rather than written under them from the CPU.
	/// </summary>
	void ResetNativeIndirectCompactionRanges(IGfxBuffer executionRangeBuffer) =>
		throw new NotSupportedException(
			$"{GetType().Name} does not record native indirect command compaction.");

	void BuildBottomLevelAccelerationStructure(IGfxBottomLevelAccelerationStructure accelerationStructure);

	void BuildTopLevelAccelerationStructure(
		IGfxTopLevelAccelerationStructure accelerationStructure,
		ReadOnlySpan<RayTracingInstanceDescription> instances);

	void SynchronizeAccelerationStructureBuildForComputeRead(IGfxTopLevelAccelerationStructure accelerationStructure);

	void SetComputeAccelerationStructure(uint slot, IGfxTopLevelAccelerationStructure accelerationStructure);

	void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ);

	void CopyBuffer(IGfxBuffer source, ulong sourceOffset, IGfxBuffer destination, ulong destinationOffset, ulong sizeInBytes);

	void Barrier(in ResourceBarrierDescription barrier);

	/// <summary>
	/// Submits a group of barriers as one unit. Backends that can coalesce transitions turn this into a
	/// single flush instead of one per barrier, which matters where a pass transitions many resources at
	/// once. Defaults to issuing them individually for backends with nothing to gain.
	/// </summary>
	void Barriers(ReadOnlySpan<ResourceBarrierDescription> barriers)
	{
		for (var i = 0; i < barriers.Length; i++)
		{
			Barrier(barriers[i]);
		}
	}
}

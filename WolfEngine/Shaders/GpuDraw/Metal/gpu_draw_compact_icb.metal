// Compacts a page of indirect draw commands down to the batches the cull pass left with visible instances,
// so the following execute walks only what survived culling instead of every batch slot in the scene, and
// re-issues each surviving command's draw with the visible instance count instead of the batch capacity.
//
// This is the Metal counterpart to gpu_draw_compact.compute.slang and applies exactly the same
// visibility rules. It cannot share that source: a Metal indirect command buffer is an opaque object
// rather than a buffer of command records, so the surviving commands are moved with the GPU-side
// copy_command intrinsic instead of a raw record copy, and the instance count cannot be patched in place,
// so the draw is issued again after the copy from the geometry MetalIndirectCommandBuffer recorded for the
// command when it was encoded. Keep the two kernels' rules in step.
//
// The structs below mirror draw_data.slang, and MetalIndirectCompactionKernel asserts that they still
// match the C# layouts they are read through.

#include <metal_stdlib>
#include <metal_command_buffer>

using namespace metal;

struct CompactionCommandBuffers
{
	command_buffer source [[id(0)]];
	command_buffer destination [[id(1)]];
};

struct CompactionParams
{
	uint pageStartCommandIndex;
	uint pageCommandCapacity;
	uint laneIndex;
	uint executionRangeIndex;
	uint activeDrawCommandUpperBound;
};

struct GpuDrawBatchData
{
	uint visibleOffset;
	uint capacity;
	uint laneIndex;
	uint pad0;
};

// Written by MetalIndirectCommandBuffer when it encodes a command, and zeroed when it resets one.
struct IndirectDrawGeometry
{
	uint indexCount;
	uint indexOffsetElements;
};

struct GpuDrawArgs
{
	uint indexCount;
	uint instanceCount;
	uint startIndex;
	int baseVertex;
	uint startInstance;
	uint pad0;
	uint pad1;
	uint pad2;
};

kernel void CSCompactIndirectCommands(
	device const CompactionCommandBuffers& commandBuffers [[buffer(0)]],
	device atomic_uint* executionRanges [[buffer(1)]],
	device const GpuDrawArgs* drawArgs [[buffer(2)]],
	device const GpuDrawBatchData* batches [[buffer(3)]],
	constant CompactionParams& params [[buffer(4)]],
	device const IndirectDrawGeometry* geometry [[buffer(5)]],
	// Every command in a page draws from the packed index buffer, which MetalIndirectCommandBuffer asserts.
	device const uint* indices [[buffer(6)]],
	uint pageCommandIndex [[thread_position_in_grid]])
{
	if (pageCommandIndex >= params.pageCommandCapacity)
	{
		return;
	}

	uint drawId = params.pageStartCommandIndex + pageCommandIndex;
	if (drawId >= params.activeDrawCommandUpperBound)
	{
		return;
	}

	GpuDrawBatchData batch = batches[drawId];
	if (batch.capacity == 0u)
	{
		return;
	}

	// Every lane allocates a page over the same draw id range, and the lanes that do not own this batch
	// hold a reset command here. Emitting only from the owning lane keeps the other lanes' compacted
	// lists free of the resets they would otherwise walk past at execute time.
	if (batch.laneIndex != params.laneIndex)
	{
		return;
	}

	// Culling can count past capacity only if the batch and command tables disagree, and the instances
	// past it were never written, so they are not drawn.
	uint visibleCount = min(drawArgs[drawId].instanceCount, batch.capacity);
	if (visibleCount == 0u)
	{
		return;
	}

	// A command whose encode failed, or that was reset, records no geometry and would draw nothing.
	IndirectDrawGeometry drawGeometry = geometry[pageCommandIndex];
	if (drawGeometry.indexCount == 0u)
	{
		return;
	}

	// Entries are execution ranges of { location, length }. Compacted commands always start at zero, so
	// only the length moves, and it doubles as the allocator for the dense destination slots.
	device atomic_uint* length = &executionRanges[(params.executionRangeIndex * 2u) + 1u];
	uint destinationIndex = atomic_fetch_add_explicit(length, 1u, memory_order_relaxed);
	if (destinationIndex >= params.pageCommandCapacity)
	{
		return;
	}

	render_command destination(commandBuffers.destination, destinationIndex);
	render_command source(commandBuffers.source, pageCommandIndex);
	destination.copy_command(source);
	destination.draw_indexed_primitives(
		primitive_type::triangle,
		drawGeometry.indexCount,
		indices + drawGeometry.indexOffsetElements,
		visibleCount,
		0u,
		0u);
}

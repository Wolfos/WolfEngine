// Compacts a page of indirect draw commands down to the draws the cull pass marked visible, so the
// following execute walks only what survived culling instead of every draw slot in the scene.
//
// This is the Metal counterpart to gpu_draw_compact.compute.slang and applies exactly the same
// visibility rules. It cannot share that source: a Metal indirect command buffer is an opaque object
// rather than a buffer of command records, so the surviving commands are moved with the GPU-side
// copy_command intrinsic instead of a raw record copy. Keep the two kernels' rules in step.
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

struct GpuDrawCommand
{
	uint instanceHandle;
	uint drawHandle;
	uint flags;
	uint pad0;
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

constant uint kDrawFlagActive = 1u;
constant uint kDrawFlagBucketShift = 1u;
constant uint kDrawFlagBucketMask = 0x7FFFFFFFu;

kernel void CSCompactIndirectCommands(
	device const CompactionCommandBuffers& commandBuffers [[buffer(0)]],
	device atomic_uint* executionRanges [[buffer(1)]],
	device const GpuDrawArgs* drawArgs [[buffer(2)]],
	device const GpuDrawCommand* drawCommands [[buffer(3)]],
	constant CompactionParams& params [[buffer(4)]],
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

	GpuDrawCommand command = drawCommands[drawId];
	if ((command.flags & kDrawFlagActive) == 0u)
	{
		return;
	}

	// Every lane allocates a page over the same draw id range, and the lanes that do not own this draw
	// hold a reset command here. Emitting only from the owning lane keeps the other lanes' compacted
	// lists free of the resets they would otherwise walk past at execute time.
	uint bucketIndex = (command.flags >> kDrawFlagBucketShift) & kDrawFlagBucketMask;
	if (bucketIndex != params.laneIndex)
	{
		return;
	}

	if (drawArgs[drawId].instanceCount == 0u)
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
}

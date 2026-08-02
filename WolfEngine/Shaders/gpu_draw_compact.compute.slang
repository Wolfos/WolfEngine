// Compacts a page of CPU-encoded indirect command records down to the draws the cull pass marked
// visible, so ExecuteIndirect walks only the visible commands instead of every draw slot.
//
// Records are copied verbatim as raw uints: their layout is owned by the backend command signature,
// and this kernel deliberately knows nothing about it beyond the stride and where the index count
// sits, so signature changes do not require a shader change.
import "draw_data.slang";

static const uint DRAW_FLAG_ACTIVE = 1;
static const uint DRAW_FLAG_BUCKET_SHIFT = 1;
static const uint DRAW_FLAG_BUCKET_MASK = 0x7FFFFFFF;

cbuffer CompactParams : register(b12)
{
    uint pageStartCommandIndex;
    uint pageCommandCapacity;
    uint laneIndex;
    uint executionRangeIndex;
    uint recordStrideUints;
    uint recordIndexCountUintOffset;
    uint activeDrawCommandUpperBound;
    uint pad0;
};

RWStructuredBuffer<uint> g_DestinationRecords : register(u0);
// Entries are execution ranges of { location, length }. Compacted records always start at zero, so
// only the length moves, and it doubles as the allocator for the dense destination slots.
RWStructuredBuffer<uint> g_ExecutionRanges : register(u1);

StructuredBuffer<uint> g_SourceRecords : register(t2);
// Bound with the view's base offset already applied, so this indexes by draw id directly.
StructuredBuffer<GpuDrawArgs> g_DrawArgs : register(t3);
StructuredBuffer<GpuDrawCommand> g_DrawCommands : register(t4);

[numthreads(64, 1, 1)]
void CSCompact(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint pageCommandIndex = dispatchThreadId.x;
    if (pageCommandIndex >= pageCommandCapacity)
    {
        return;
    }

    uint drawId = pageStartCommandIndex + pageCommandIndex;
    if (drawId >= activeDrawCommandUpperBound)
    {
        return;
    }

    GpuDrawCommand cmd = g_DrawCommands[drawId];
    if ((cmd.flags & DRAW_FLAG_ACTIVE) == 0)
    {
        return;
    }

    // Every lane allocates a page for the same draw id range, and the lanes that do not own this
    // draw hold a reset record here. Emitting only from the owning lane keeps the other lanes'
    // compacted lists free of the resets they would otherwise have to skip at execute time.
    uint bucketIndex = (cmd.flags >> DRAW_FLAG_BUCKET_SHIFT) & DRAW_FLAG_BUCKET_MASK;
    if (bucketIndex != laneIndex)
    {
        return;
    }

    if (g_DrawArgs[drawId].instanceCount == 0)
    {
        return;
    }

    // A draw whose encode failed leaves a zeroed record behind. It would draw nothing, but it would
    // still cost a command slot, so drop it here rather than compacting it into the dense list.
    uint sourceBase = pageCommandIndex * recordStrideUints;
    if (g_SourceRecords[sourceBase + recordIndexCountUintOffset] == 0)
    {
        return;
    }

    uint destinationIndex;
    InterlockedAdd(g_ExecutionRanges[(executionRangeIndex * 2) + 1], 1, destinationIndex);
    if (destinationIndex >= pageCommandCapacity)
    {
        return;
    }

    uint destinationBase = destinationIndex * recordStrideUints;
    for (uint i = 0; i < recordStrideUints; i++)
    {
        g_DestinationRecords[destinationBase + i] = g_SourceRecords[sourceBase + i];
    }
}

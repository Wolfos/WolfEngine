// Compacts a page of CPU-encoded indirect command records down to the batches the cull pass left with visible
// instances, so ExecuteIndirect walks only those instead of every batch slot, and patches each copied record's
// instance count down from the batch's capacity to the instances culling left visible.
//
// Records are copied verbatim as raw uints: their layout is owned by the backend command signature,
// and this kernel deliberately knows nothing about it beyond the stride and where the index and
// instance counts sit, so signature changes do not require a shader change.
import "../Common/draw_data.slang";

cbuffer CompactParams : register(b12)
{
    uint pageStartCommandIndex;
    uint pageCommandCapacity;
    uint laneIndex;
    uint executionRangeIndex;
    uint recordStrideUints;
    uint recordIndexCountUintOffset;
    uint activeDrawCommandUpperBound;
    uint recordInstanceCountUintOffset;
};

RWStructuredBuffer<uint> g_DestinationRecords : register(u0);
// Entries are execution ranges of { location, length }. Compacted records always start at zero, so
// only the length moves, and it doubles as the allocator for the dense destination slots.
RWStructuredBuffer<uint> g_ExecutionRanges : register(u1);

StructuredBuffer<uint> g_SourceRecords : register(t2);
// Bound with the view's base offset already applied, so this indexes by draw id directly.
StructuredBuffer<GpuDrawArgs> g_DrawArgs : register(t3);
// Indexed by draw id, which is the batch index.
StructuredBuffer<GpuDrawBatchData> g_Batches : register(t4);

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

    GpuDrawBatchData batch = g_Batches[drawId];
    if (batch.capacity == 0)
    {
        return;
    }

    // Every lane allocates a page for the same draw id range, and the lanes that do not own this
    // batch hold a reset record here. Emitting only from the owning lane keeps the other lanes'
    // compacted lists free of the resets they would otherwise have to skip at execute time.
    if (batch.laneIndex != laneIndex)
    {
        return;
    }

    // Culling can count past capacity only if the batch and command tables disagree, and the
    // instances past it were never written, so they are not drawn.
    uint visibleCount = min(g_DrawArgs[drawId].instanceCount, batch.capacity);
    if (visibleCount == 0)
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

    g_DestinationRecords[destinationBase + recordInstanceCountUintOffset] = visibleCount;
}

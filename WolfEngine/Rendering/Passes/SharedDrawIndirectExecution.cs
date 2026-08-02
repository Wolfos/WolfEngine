#nullable enable

using System;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

/// <summary>
/// Execution of shared-draw indirect command pages, in the two forms the passes choose between:
/// the compacted path, where culling decides how many commands the command processor walks, and the
/// full-range fallback for backends that cannot compact.
/// </summary>
internal static class SharedDrawIndirectExecution
{
	/// <summary>
	/// Binds the packed geometry every shared draw reads from. The indirect commands carry only their
	/// offsets into these streams, so the views have to be bound before any of them execute.
	/// </summary>
	public static void BindPackedGeometry(
		IGfxCommandList commandList,
		IGfxBuffer? packedVertexBuffer,
		IGfxBuffer? packedIndexBuffer,
		uint packedVertexStride)
	{
		if (packedVertexBuffer is null || packedIndexBuffer is null || packedVertexStride == 0)
		{
			return;
		}

		commandList.SetVertexBuffer(new VertexBufferView(packedVertexBuffer, packedVertexStride));
		commandList.SetIndexBuffer(new IndexBufferView(packedIndexBuffer, IndexFormat.UInt32));
	}

	/// <summary>
	/// Executes every command slot up to <paramref name="commandUpperBound"/>. Draws that culling
	/// rejected still cost a command each, because their records stay in place and only the vertex
	/// shader knows to skip them.
	/// </summary>
	public static void ExecutePages(
		IGfxCommandList commandList,
		ReadOnlySpan<SharedDrawIndirectCommandPage> pages,
		uint commandUpperBound)
	{
		for (var i = 0; i < pages.Length; i++)
		{
			var page = pages[i];
			if (commandUpperBound <= page.PageStartCommandIndex)
			{
				continue;
			}

			var pageEnd = page.PageStartCommandIndex + page.PageCommandCapacity;
			var pageUpperBound = Math.Min(commandUpperBound, pageEnd);
			var localCount = pageUpperBound - page.PageStartCommandIndex;
			if (localCount > 0)
			{
				commandList.ExecuteIndirectCommandBuffer(page.CommandBuffer, localCount);
			}
		}
	}

	/// <summary>
	/// Executes the dense commands produced by compaction, taking each page's range from GPU memory.
	/// Pages past the active draw range are skipped outright: their range is empty, so the only thing
	/// executing them would add is a command-processor round trip.
	/// </summary>
	public static void ExecuteCompactedPages(
		IGfxCommandList commandList,
		ReadOnlySpan<SharedDrawIndirectCommandPage> pages,
		IGfxBuffer executionRangeBuffer,
		int slotIndex,
		int executionLaneIndex,
		uint commandUpperBound)
	{
		for (var i = 0; i < pages.Length; i++)
		{
			var page = pages[i];
			if (commandUpperBound <= page.PageStartCommandIndex)
			{
				continue;
			}

			var rangeIndex = SharedDrawIndirectCommandSet.GetCompactedExecutionRangeIndex(
				slotIndex,
				executionLaneIndex,
				page.PageIndex);
			commandList.ExecuteCompactedIndirectCommandBuffer(
				page.CommandBuffer,
				executionRangeBuffer,
				(ulong)rangeIndex * IndirectCompactionExecutionRange.StrideInBytes);
		}
	}
}

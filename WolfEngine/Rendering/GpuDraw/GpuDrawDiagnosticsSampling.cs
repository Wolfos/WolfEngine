#nullable enable

using System;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Rendering;

/// <summary>
/// Interprets the GPU-written hardening diagnostics. Only the acquisition of these values is
/// backend-specific - Metal reads shared-memory buffers directly, D3D12 copies them to a readback
/// buffer - so the interpretation lives here to stop the two backends reporting different things from
/// the same counters.
/// </summary>
internal static class GpuDrawDiagnosticsSampling
{
	/// <summary>Expected element count of the per-lane visible draw count buffer.</summary>
	internal static int VisibleCountElementCount => GpuDrawExecutionLanes.ExecutionLaneCount;

	/// <summary>Expected element count of the per-lane execution range buffer, as {start, end} pairs.</summary>
	internal static int ExecutionRangeElementCount => GpuDrawExecutionLanes.ExecutionLaneCount * 2;

	/// <summary>
	/// Folds per-lane visible counts and execution ranges into per-bucket stats. A lane reporting an end
	/// of zero never executed, so it is skipped rather than dragging the bucket's range down to zero.
	/// </summary>
	internal static void ApplyVisibilityDiagnostics(
		ReadOnlySpan<uint> visibleCounts,
		ReadOnlySpan<uint> executionRanges,
		GpuDrawHardeningStats stats)
	{
		if (stats is null ||
		    visibleCounts.Length < VisibleCountElementCount ||
		    executionRanges.Length < ExecutionRangeElementCount)
		{
			return;
		}

		var definitions = GBufferDrawBuckets.StableOrderDefinitions;
		var laneDefinitions = GpuDrawExecutionLanes.Definitions;
		for (var i = 0; i < definitions.Length; i++)
		{
			var definition = definitions[i];
			long visibleCount = 0;
			var rangeStart = 0u;
			var rangeEnd = 0u;
			var hasRange = false;
			for (var laneIndex = 0; laneIndex < laneDefinitions.Length; laneIndex++)
			{
				var lane = laneDefinitions[laneIndex];
				if (lane.BucketId != definition.BucketId)
				{
					continue;
				}

				visibleCount += visibleCounts[lane.ExecutionIndex];
				var candidateStart = executionRanges[(lane.ExecutionIndex * 2) + 0];
				var candidateEnd = executionRanges[(lane.ExecutionIndex * 2) + 1];
				if (candidateEnd == 0)
				{
					continue;
				}

				if (hasRange == false)
				{
					rangeStart = candidateStart;
					rangeEnd = candidateEnd;
					hasRange = true;
					continue;
				}

				rangeStart = Math.Min(rangeStart, candidateStart);
				rangeEnd = Math.Max(rangeEnd, candidateEnd);
			}

			stats.SetVisibleDrawCount(definition.BucketId, visibleCount);
			stats.SetExecutionRange(definition.BucketId, hasRange ? rangeStart : 0, hasRange ? rangeEnd : 0);
		}
	}

	/// <summary>
	/// Feeds the deltas of the monotonically increasing shader counters into the stats. A counter that
	/// went backwards means the buffer was cleared, so the baseline is reset rather than reporting a
	/// nonsensical delta.
	/// </summary>
	internal static void ApplyDiagnosticCounters(
		ReadOnlySpan<uint> counters,
		uint[] lastCounters,
		GpuDrawHardeningStats stats)
	{
		if (stats is null ||
		    lastCounters is null ||
		    lastCounters.Length < GpuDrawResources.HardeningCounterCount ||
		    counters.Length < GpuDrawResources.HardeningCounterCount)
		{
			return;
		}

		for (var i = 0; i < GpuDrawResources.HardeningCounterCount; i++)
		{
			var current = counters[i];
			var previous = lastCounters[i];
			if (current < previous)
			{
				lastCounters[i] = current;
				continue;
			}

			var delta = current - previous;
			if (delta == 0)
			{
				continue;
			}

			lastCounters[i] = current;
			switch (i)
			{
				case 0:
					stats.AddStaleHandleRejects(delta);
					break;
				case 1:
					stats.AddFallbackProxySubstitutions(delta);
					break;
				case 4:
					stats.AddVisibleListClampHits(delta);
					break;
				case 5:
					stats.AddMaterialFallbackDrawHits(delta);
					break;
			}
		}
	}
}

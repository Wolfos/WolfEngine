#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Rendering;

public sealed class GpuDrawHardeningStats
{
	private long _staleHandleRejects;
	private long _fallbackProxySubstitutions;
	private long _updateOverflowRecoveries;
	private long _packedCapacityFailures;
	private long _visibleListClampHits;
	private long _materialFallbackDrawHits;
	private long _deferredReleaseBacklog;
	private long _icbSlotStarvationStalls;
	private readonly BucketDiagnosticsState[] _bucketDiagnostics;

	public GpuDrawHardeningStats()
	{
		var definitions = GBufferDrawBuckets.StableOrderDefinitions;
		_bucketDiagnostics = new BucketDiagnosticsState[definitions.Length];
		for (var i = 0; i < definitions.Length; i++)
		{
			var definition = definitions[i];
			_bucketDiagnostics[i] = new BucketDiagnosticsState(
				definition.BucketId,
				definition.DebugName,
				definition.ExecutionIndex);
		}
	}

	public void IncrementStaleHandleRejects() => Interlocked.Increment(ref _staleHandleRejects);
	public void AddStaleHandleRejects(long delta) => Interlocked.Add(ref _staleHandleRejects, delta);
	public void IncrementFallbackProxySubstitutions() => Interlocked.Increment(ref _fallbackProxySubstitutions);
	public void AddFallbackProxySubstitutions(long delta) => Interlocked.Add(ref _fallbackProxySubstitutions, delta);
	public void IncrementUpdateOverflowRecoveries() => Interlocked.Increment(ref _updateOverflowRecoveries);
	public void IncrementPackedCapacityFailures() => Interlocked.Increment(ref _packedCapacityFailures);
	public void IncrementVisibleListClampHits() => Interlocked.Increment(ref _visibleListClampHits);
	public void AddVisibleListClampHits(long delta) => Interlocked.Add(ref _visibleListClampHits, delta);
	public void IncrementMaterialFallbackDrawHits() => Interlocked.Increment(ref _materialFallbackDrawHits);
	public void AddMaterialFallbackDrawHits(long delta) => Interlocked.Add(ref _materialFallbackDrawHits, delta);
	public void IncrementIcbSlotStarvationStalls() => Interlocked.Increment(ref _icbSlotStarvationStalls);

	public void SetDeferredReleaseBacklog(long value) => Interlocked.Exchange(ref _deferredReleaseBacklog, value);

	public void ResetSubmissionDiagnostics()
	{
		for (var i = 0; i < _bucketDiagnostics.Length; i++)
		{
			_bucketDiagnostics[i].ResetSubmissionDiagnostics();
		}
	}

	public void SetSubmittedDrawCount(GpuDrawBucketId bucketId, long count)
	{
		GetBucketState(bucketId).SetSubmittedDrawCount(count);
	}

	public void AddMaterialFallbackIncident(GpuDrawBucketId bucketId, long delta = 1)
	{
		GetBucketState(bucketId).AddMaterialFallbackIncidents(delta);
	}

	public void SetVisibleDrawCount(GpuDrawBucketId bucketId, long count)
	{
		GetBucketState(bucketId).SetVisibleDrawCount(count);
	}

	public void SetExecutionRange(GpuDrawBucketId bucketId, long start, long endExclusive)
	{
		GetBucketState(bucketId).SetExecutionRange(start, endExclusive);
	}

	public GpuDrawHardeningSnapshot Snapshot()
	{
		var bucketDiagnostics = new GpuDrawBucketDiagnosticSnapshot[_bucketDiagnostics.Length];
		for (var i = 0; i < _bucketDiagnostics.Length; i++)
		{
			bucketDiagnostics[i] = _bucketDiagnostics[i].Snapshot();
		}

		return new GpuDrawHardeningSnapshot(
			Interlocked.Read(ref _staleHandleRejects),
			Interlocked.Read(ref _fallbackProxySubstitutions),
				Interlocked.Read(ref _updateOverflowRecoveries),
				Interlocked.Read(ref _packedCapacityFailures),
				Interlocked.Read(ref _visibleListClampHits),
				Interlocked.Read(ref _materialFallbackDrawHits),
				Interlocked.Read(ref _deferredReleaseBacklog),
				Interlocked.Read(ref _icbSlotStarvationStalls),
				bucketDiagnostics);
	}

	private BucketDiagnosticsState GetBucketState(GpuDrawBucketId bucketId)
	{
		for (var i = 0; i < _bucketDiagnostics.Length; i++)
		{
			if (_bucketDiagnostics[i].BucketId == bucketId)
			{
				return _bucketDiagnostics[i];
			}
		}

		throw new KeyNotFoundException($"Unknown draw bucket id '{bucketId}'.");
	}
}

public readonly record struct GpuDrawHardeningSnapshot(
	long StaleHandleRejects,
	long FallbackProxySubstitutions,
	long UpdateOverflowRecoveries,
	long PackedCapacityFailures,
	long VisibleListClampHits,
	long MaterialFallbackDrawHits,
	long DeferredReleaseBacklog,
	long IcbSlotStarvationStalls,
	IReadOnlyList<GpuDrawBucketDiagnosticSnapshot> BucketDiagnostics);

public readonly record struct GpuDrawBucketDiagnosticSnapshot(
	GpuDrawBucketId BucketId,
	string DebugName,
	int ExecutionIndex,
	long SubmittedDrawCount,
	long VisibleDrawCount,
	long ExecutionRangeStart,
	long ExecutionRangeEndExclusive,
	long MaterialFallbackIncidents)
{
	public long ExecutionRangeSpan => Math.Max(0, ExecutionRangeEndExclusive - ExecutionRangeStart);
}

internal sealed class BucketDiagnosticsState
{
	private long _submittedDrawCount;
	private long _visibleDrawCount;
	private long _executionRangeStart;
	private long _executionRangeEndExclusive;
	private long _materialFallbackIncidents;

	public BucketDiagnosticsState(GpuDrawBucketId bucketId, string debugName, int executionIndex)
	{
		BucketId = bucketId;
		DebugName = debugName;
		ExecutionIndex = executionIndex;
	}

	public GpuDrawBucketId BucketId { get; }
	public string DebugName { get; }
	public int ExecutionIndex { get; }

	public void ResetSubmissionDiagnostics()
	{
		Interlocked.Exchange(ref _submittedDrawCount, 0);
		Interlocked.Exchange(ref _materialFallbackIncidents, 0);
	}

	public void SetSubmittedDrawCount(long count) => Interlocked.Exchange(ref _submittedDrawCount, count);
	public void AddMaterialFallbackIncidents(long delta) => Interlocked.Add(ref _materialFallbackIncidents, delta);
	public void SetVisibleDrawCount(long count) => Interlocked.Exchange(ref _visibleDrawCount, count);
	public void SetExecutionRange(long start, long endExclusive)
	{
		Interlocked.Exchange(ref _executionRangeStart, start);
		Interlocked.Exchange(ref _executionRangeEndExclusive, endExclusive);
	}

	public GpuDrawBucketDiagnosticSnapshot Snapshot()
	{
		return new GpuDrawBucketDiagnosticSnapshot(
			BucketId,
			DebugName,
			ExecutionIndex,
			Interlocked.Read(ref _submittedDrawCount),
			Interlocked.Read(ref _visibleDrawCount),
			Interlocked.Read(ref _executionRangeStart),
			Interlocked.Read(ref _executionRangeEndExclusive),
			Interlocked.Read(ref _materialFallbackIncidents));
	}
}

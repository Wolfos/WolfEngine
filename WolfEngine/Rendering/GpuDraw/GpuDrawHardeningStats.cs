#nullable enable

using System.Threading;

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

	public GpuDrawHardeningSnapshot Snapshot()
	{
		return new GpuDrawHardeningSnapshot(
			Interlocked.Read(ref _staleHandleRejects),
			Interlocked.Read(ref _fallbackProxySubstitutions),
				Interlocked.Read(ref _updateOverflowRecoveries),
				Interlocked.Read(ref _packedCapacityFailures),
				Interlocked.Read(ref _visibleListClampHits),
				Interlocked.Read(ref _materialFallbackDrawHits),
				Interlocked.Read(ref _deferredReleaseBacklog),
				Interlocked.Read(ref _icbSlotStarvationStalls));
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
	long IcbSlotStarvationStalls);

namespace WolfEngine.Profiling;

/// <summary>
/// Retains only candidates for the maximum in a five-second window. A newer, slower
/// frame supersedes older, faster frames, so their complete sample trees can be released.
/// Callers synchronize access and supply monotonic timestamps from the same clock.
/// </summary>
internal sealed class SpikeFrameWindow<T>
{
	private readonly LinkedList<(T Frame, double DurationMs, long Timestamp)> _candidates = new();
	private readonly TimeProvider _clock;

	public SpikeFrameWindow(TimeProvider clock) => _clock = clock;

	public void Add(T frame, double durationMs, long timestamp)
	{
		Prune(timestamp);
		while (_candidates.Last is { } last && last.Value.DurationMs <= durationMs)
		{
			_candidates.RemoveLast();
		}
		_candidates.AddLast((frame, durationMs, timestamp));
	}

	public bool TryGet(long timestamp, out T frame)
	{
		Prune(timestamp);
		if (_candidates.First is { } first)
		{
			frame = first.Value.Frame;
			return true;
		}
		frame = default!;
		return false;
	}

	public void Clear() => _candidates.Clear();

	private void Prune(long timestamp)
	{
		while (_candidates.First is { } first &&
		       _clock.GetElapsedTime(first.Value.Timestamp, timestamp) >= TimeSpan.FromSeconds(5))
		{
			_candidates.RemoveFirst();
		}
	}
}

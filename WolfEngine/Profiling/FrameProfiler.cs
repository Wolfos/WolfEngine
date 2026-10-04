using System.Diagnostics;

namespace WolfEngine.Profiling;

public sealed class FrameProfiler
{
	private static readonly double TickToMs = 1000.0 / Stopwatch.Frequency;

	private readonly Dictionary<int, ThreadFrameData> _threadFrames = new();
	private readonly object _sync = new();
	private readonly TimeProvider _clock;
	private readonly ThreadLocal<ProfilerState> _state = new(() => new ProfilerState());
	private int _enabled = 1;
	private int _collectionGeneration;
	private long _pausedTimestamp;

	public FrameProfiler(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

	public bool Enabled
	{
		get => Volatile.Read(ref _enabled) != 0;
		set
		{
			lock (_sync)
			{
				if (Enabled == value) return;
				_pausedTimestamp = _clock.GetTimestamp();
				foreach (var data in _threadFrames.Values)
				{
					if (value) data.Spikes.Clear();
					else data.Spikes.TryGet(_pausedTimestamp, out _);
				}
				Interlocked.Increment(ref _collectionGeneration);
				Volatile.Write(ref _enabled, value ? 1 : 0);
			}
		}
	}

	public static FrameProfiler Instance { get; } = new();

	public void BeginFrame(string name = "Frame")
	{
		var generation = Volatile.Read(ref _collectionGeneration);
		if (!Enabled) return;
		var state = _state.Value!;
		state.CollectionGeneration = generation;
		state.FrameActive = true;
		state.Root = new ProfileNode(name)
		{
			StartTicks = Stopwatch.GetTimestamp(),
			StartAllocatedBytes = GC.GetAllocatedBytesForCurrentThread()
		};
		state.Stack.Clear();
		state.Stack.Push(state.Root);
	}

	public void EndFrame()
	{
		var state = _state.Value!;
		if (state.FrameActive == false)
		{
			return;
		}

		if (!Enabled || state.CollectionGeneration != Volatile.Read(ref _collectionGeneration))
		{
			state.Stack.Clear();
			state.FrameActive = false;
			return;
		}

		state.Root.EndTicks = Stopwatch.GetTimestamp();
		state.Root.EndAllocatedBytes = GC.GetAllocatedBytesForCurrentThread();
		state.Stack.Clear();
		state.FrameActive = false;

		var thread = Thread.CurrentThread;
		var threadId = thread.ManagedThreadId;
		var threadName = string.IsNullOrWhiteSpace(thread.Name) ? $"Thread {threadId}" : thread.Name;
		lock (_sync)
		{
			if (!Enabled || state.CollectionGeneration != _collectionGeneration) return;
			if (!_threadFrames.TryGetValue(threadId, out var data))
			{
				data = new(threadId, threadName, _clock);
				_threadFrames.Add(threadId, data);
			}
			data.LastFrameRoot = state.Root;
			data.Spikes.Add(new ThreadFrame(threadId, threadName, state.Root), state.Root.DurationMs, _clock.GetTimestamp());
		}
	}

	public Scope Measure(string name)
	{
		var state = _state.Value!;
		if (!Enabled || state.CollectionGeneration != Volatile.Read(ref _collectionGeneration) || !state.FrameActive)
		{
			return default;
		}

		var node = new ProfileNode(name)
		{
			StartTicks = Stopwatch.GetTimestamp(),
			StartAllocatedBytes = GC.GetAllocatedBytesForCurrentThread()
		};
		state.Stack.Peek().Children.Add(node);
		state.Stack.Push(node);
		return new Scope(this);
	}

    /// <summary>
    /// Records time accumulated over a batch without allocating one scope per item.
    /// Allocation deltas remain attributed to the enclosing measured scope.
    /// </summary>
    public void RecordElapsed(string name, long elapsedTicks)
    {
        var state = _state.Value!;
        if (!Enabled || state.CollectionGeneration != Volatile.Read(ref _collectionGeneration) || !state.FrameActive) return;
        var node = new ProfileNode(name) { EndTicks = elapsedTicks };
        state.Stack.Peek().Children.Add(node);
    }

	public IReadOnlyList<ThreadFrame> GetLastFrames() => GetFrames(spike: false);

	/// <summary>Returns the slowest completed frame per thread in the last five seconds.
	/// While disabled, the window stays frozen at the time collection stopped.</summary>
	public IReadOnlyList<ThreadFrame> GetSpikeFrames() => GetFrames(spike: true);

	private IReadOnlyList<ThreadFrame> GetFrames(bool spike)
	{
		lock (_sync)
		{
			var timestamp = Enabled ? _clock.GetTimestamp() : _pausedTimestamp;
			var frames = new List<ThreadFrame>(_threadFrames.Count);
			foreach (var data in _threadFrames.Values)
			{
				if (spike)
				{
					if (data.Spikes.TryGet(timestamp, out var frame)) frames.Add(frame);
				}
				else if (data.LastFrameRoot is { } root)
				{
					frames.Add(new ThreadFrame(data.ThreadId, data.ThreadName, root));
				}
			}
			return frames;
		}
	}

	private void EndSample()
	{
		var state = _state.Value!;
		if (!Enabled || state.CollectionGeneration != Volatile.Read(ref _collectionGeneration) ||
		    !state.FrameActive || state.Stack.Count <= 1)
		{
			return;
		}

		var node = state.Stack.Pop();
		node.EndTicks = Stopwatch.GetTimestamp();
		node.EndAllocatedBytes = GC.GetAllocatedBytesForCurrentThread();
	}

	private sealed class ProfilerState
	{
		public bool FrameActive;
		public int CollectionGeneration;
		public ProfileNode Root = new("Frame");
		public Stack<ProfileNode> Stack = new();
	}

	private sealed class ThreadFrameData
	{
		public ThreadFrameData(int threadId, string threadName, TimeProvider clock)
		{
			ThreadId = threadId;
			ThreadName = threadName;
			Spikes = new SpikeFrameWindow<ThreadFrame>(clock);
		}

		public int ThreadId { get; }
		public string ThreadName { get; }
		public ProfileNode? LastFrameRoot { get; set; }
		public SpikeFrameWindow<ThreadFrame> Spikes { get; }
	}

	public sealed class ProfileNode
	{
		public ProfileNode(string name)
		{
			Name = name;
			Children = new List<ProfileNode>();
		}

		public string Name { get; }
		public long StartTicks { get; set; }
		public long EndTicks { get; set; }
		public long StartAllocatedBytes { get; set; }
		public long EndAllocatedBytes { get; set; }
		public List<ProfileNode> Children { get; }
		public double DurationMs => (EndTicks - StartTicks) * TickToMs;
		public long AllocatedBytes => Math.Max(0, EndAllocatedBytes - StartAllocatedBytes);
	}

	public readonly struct Scope : IDisposable
	{
		private readonly FrameProfiler? _profiler;

		public Scope(FrameProfiler profiler)
		{
			_profiler = profiler;
		}

		public void Dispose()
		{
			_profiler?.EndSample();
		}
	}

	public readonly struct ThreadFrame
	{
		public ThreadFrame(int threadId, string threadName, ProfileNode root)
		{
			ThreadId = threadId;
			ThreadName = threadName;
			Root = root;
		}

		public int ThreadId { get; }
		public string ThreadName { get; }
		public ProfileNode Root { get; }
	}
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace WolfEngine.Profiling;

public sealed class FrameProfiler
{
	private static readonly double TickToMs = 1000.0 / Stopwatch.Frequency;

	private readonly ConcurrentDictionary<int, ThreadFrameData> _threadFrames = new();
	private readonly ThreadLocal<ProfilerState> _state = new(() => new ProfilerState());

	public static FrameProfiler Instance { get; } = new();

	public void BeginFrame(string name = "Frame")
	{
		var state = _state.Value!;
		state.FrameActive = true;
		state.Root = new ProfileNode(name)
		{
			StartTicks = Stopwatch.GetTimestamp()
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

		state.Root.EndTicks = Stopwatch.GetTimestamp();
		state.Stack.Clear();
		state.FrameActive = false;

		var thread = Thread.CurrentThread;
		var threadId = thread.ManagedThreadId;
		var threadName = string.IsNullOrWhiteSpace(thread.Name) ? $"Thread {threadId}" : thread.Name;
		_threadFrames[threadId] = new ThreadFrameData(threadId, threadName, state.Root);
	}

	public Scope Measure(string name)
	{
		var state = _state.Value!;
		if (state.FrameActive == false)
		{
			return default;
		}

		var node = new ProfileNode(name)
		{
			StartTicks = Stopwatch.GetTimestamp()
		};
		state.Stack.Peek().Children.Add(node);
		state.Stack.Push(node);
		return new Scope(this);
	}

	public IReadOnlyList<ThreadFrame> GetLastFrames()
	{
		var frames = new List<ThreadFrame>();
		foreach (var entry in _threadFrames)
		{
			var data = entry.Value;
			if (data.LastFrameRoot != null)
			{
				frames.Add(new ThreadFrame(data.ThreadId, data.ThreadName, data.LastFrameRoot));
			}
		}
		return frames;
	}

	private void EndSample()
	{
		var state = _state.Value!;
		if (state.FrameActive == false || state.Stack.Count <= 1)
		{
			return;
		}

		var node = state.Stack.Pop();
		node.EndTicks = Stopwatch.GetTimestamp();
	}

	private sealed class ProfilerState
	{
		public bool FrameActive;
		public ProfileNode Root = new("Frame");
		public Stack<ProfileNode> Stack = new();
	}

	private sealed class ThreadFrameData
	{
		public ThreadFrameData(int threadId, string threadName, ProfileNode? lastFrameRoot)
		{
			ThreadId = threadId;
			ThreadName = threadName;
			LastFrameRoot = lastFrameRoot;
		}

		public int ThreadId { get; }
		public string ThreadName { get; }
		public ProfileNode? LastFrameRoot { get; set; }
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
		public List<ProfileNode> Children { get; }
		public double DurationMs => (EndTicks - StartTicks) * TickToMs;
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

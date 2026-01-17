using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace WolfEngine.Editor.Profiling;

public sealed class FrameProfiler
{
	private static readonly double TickToMs = 1000.0 / Stopwatch.Frequency;

	private readonly Stack<ProfileNode> _stack = new();
	private ProfileNode _currentRoot = new("Frame");
	private ProfileNode _lastFrameRoot = new("Frame");
	private bool _frameActive;

	public static FrameProfiler Instance { get; } = new();

	public void BeginFrame()
	{
		_frameActive = true;
		_currentRoot = new ProfileNode("Frame")
		{
			StartTicks = Stopwatch.GetTimestamp()
		};
		_stack.Clear();
		_stack.Push(_currentRoot);
	}

	public void EndFrame()
	{
		if (_frameActive == false)
		{
			return;
		}

		_currentRoot.EndTicks = Stopwatch.GetTimestamp();
		_lastFrameRoot = _currentRoot;
		_stack.Clear();
		_frameActive = false;
	}

	public Scope Measure(string name)
	{
		if (_frameActive == false)
		{
			return default;
		}

		var node = new ProfileNode(name)
		{
			StartTicks = Stopwatch.GetTimestamp()
		};
		_stack.Peek().Children.Add(node);
		_stack.Push(node);
		return new Scope(this);
	}

	public ProfileNode? LastFrameRoot => _lastFrameRoot;

	private void EndSample()
	{
		if (_frameActive == false || _stack.Count <= 1)
		{
			return;
		}

		var node = _stack.Pop();
		node.EndTicks = Stopwatch.GetTimestamp();
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
}

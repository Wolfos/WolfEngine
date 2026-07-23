#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Profiling;

public sealed class GpuProfiler
{
	private const int MaxRetainedCompletedFrames = 256;
	private sealed record CollectionWaiter(
		GpuProfileCollectionMarker Marker,
		int FrameCount,
		TaskCompletionSource<IReadOnlyList<GpuProfileFrame>> Completion);

	private int _enabled;
	private GpuProfileFrame? _latestFrame;
	private string? _unsupportedReason;
	private readonly object _publishSync = new();
	private readonly SortedDictionary<ulong, GpuProfileFrame> _completedFrames = [];
	private readonly List<CollectionWaiter> _collectionWaiters = [];
	private ulong _lastStartedFrameIndex;
	private bool _hasStartedFrame;

	public bool Enabled
	{
		get => Volatile.Read(ref _enabled) != 0;
		set => Volatile.Write(ref _enabled, value ? 1 : 0);
	}

	public GpuProfileFrame? LatestFrame => Volatile.Read(ref _latestFrame);

	public string? UnsupportedReason => Volatile.Read(ref _unsupportedReason);

	/// <summary>Marks a collection boundary and enables GPU profiling for future render frames.</summary>
	public GpuProfileCollectionMarker BeginCollection()
	{
		Enabled = true;
		lock (_publishSync)
		{
			return new GpuProfileCollectionMarker(_hasStartedFrame ? _lastStartedFrameIndex : null);
		}
	}

	/// <summary>
	/// Returns completed GPU-profile frames recorded after <paramref name="marker"/>. The task only
	/// completes once every requested frame has received timestamp results from the GPU backend.
	/// </summary>
	public Task<IReadOnlyList<GpuProfileFrame>> CollectCompletedFramesAsync(
		GpuProfileCollectionMarker marker,
		int frameCount,
		CancellationToken cancellationToken = default)
	{
		if (frameCount <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(frameCount), "Frame count must be positive.");
		}

		TaskCompletionSource<IReadOnlyList<GpuProfileFrame>>? completion = null;
		lock (_publishSync)
		{
			var frames = GetFramesAfterLocked(marker, frameCount);
			if (frames.Count >= frameCount)
			{
				return Task.FromResult<IReadOnlyList<GpuProfileFrame>>(frames);
			}

			completion = new TaskCompletionSource<IReadOnlyList<GpuProfileFrame>>(
				TaskCreationOptions.RunContinuationsAsynchronously);
			_collectionWaiters.Add(new CollectionWaiter(marker, frameCount, completion));
		}

		if (cancellationToken.CanBeCanceled)
		{
			cancellationToken.Register(() => CancelCollection(completion!, cancellationToken));
		}

		return completion.Task;
	}

	/// <summary>Copies retained completed frames after an optional render-frame index boundary.</summary>
	public IReadOnlyList<GpuProfileFrame> GetCompletedFramesAfter(ulong? exclusiveFrameIndex = null)
	{
		lock (_publishSync)
		{
			return GetFramesAfterLocked(new GpuProfileCollectionMarker(exclusiveFrameIndex), int.MaxValue);
		}
	}

	internal void SetBackendAvailability(bool supported, string? unsupportedReason)
	{
		Volatile.Write(ref _unsupportedReason, supported ? null : unsupportedReason ?? "GPU profiling is not supported by this graphics device.");
		if (!supported)
		{
			Enabled = false;
			FailCollections(UnsupportedReason!);
		}
	}

	internal GpuProfileFrameCapture? BeginFrame(ulong frameIndex)
	{
		lock (_publishSync)
		{
			_lastStartedFrameIndex = frameIndex;
			_hasStartedFrame = true;
		}
		return Enabled && UnsupportedReason is null
			? new GpuProfileFrameCapture(frameIndex, Publish)
			: null;
	}

	private void Publish(GpuProfileFrame frame)
	{
		List<(TaskCompletionSource<IReadOnlyList<GpuProfileFrame>> Completion, IReadOnlyList<GpuProfileFrame> Frames)>? ready = null;
		lock (_publishSync)
		{
			_completedFrames[frame.FrameIndex] = frame;
			while (_completedFrames.Count > MaxRetainedCompletedFrames)
			{
				using var keys = _completedFrames.Keys.GetEnumerator();
				keys.MoveNext();
				_completedFrames.Remove(keys.Current);
			}
			if (_latestFrame is null || frame.FrameIndex >= _latestFrame.FrameIndex)
			{
				Volatile.Write(ref _latestFrame, frame);
			}

			for (var index = _collectionWaiters.Count - 1; index >= 0; index--)
			{
				var waiter = _collectionWaiters[index];
				var frames = GetFramesAfterLocked(waiter.Marker, waiter.FrameCount);
				if (frames.Count < waiter.FrameCount)
				{
					continue;
				}

				ready ??= [];
				ready.Add((waiter.Completion, frames));
				_collectionWaiters.RemoveAt(index);
			}
		}

		if (ready is not null)
		{
			for (var index = 0; index < ready.Count; index++)
			{
				ready[index].Completion.TrySetResult(ready[index].Frames);
			}
		}
	}

	private List<GpuProfileFrame> GetFramesAfterLocked(GpuProfileCollectionMarker marker, int maximumCount)
	{
		var frames = new List<GpuProfileFrame>();
		foreach (var (frameIndex, frame) in _completedFrames)
		{
			if (marker.ExclusiveFrameIndex.HasValue && frameIndex <= marker.ExclusiveFrameIndex.Value)
			{
				continue;
			}

			frames.Add(frame);
			if (frames.Count == maximumCount)
			{
				break;
			}
		}

		return frames;
	}

	private void CancelCollection(
		TaskCompletionSource<IReadOnlyList<GpuProfileFrame>> completion,
		CancellationToken cancellationToken)
	{
		lock (_publishSync)
		{
			for (var index = _collectionWaiters.Count - 1; index >= 0; index--)
			{
				if (ReferenceEquals(_collectionWaiters[index].Completion, completion))
				{
					_collectionWaiters.RemoveAt(index);
					break;
				}
			}
		}

		completion.TrySetCanceled(cancellationToken);
	}

	private void FailCollections(string reason)
	{
		List<TaskCompletionSource<IReadOnlyList<GpuProfileFrame>>>? pending = null;
		lock (_publishSync)
		{
			if (_collectionWaiters.Count == 0)
			{
				return;
			}

			pending = _collectionWaiters.Select(waiter => waiter.Completion).ToList();
			_collectionWaiters.Clear();
		}

		for (var index = 0; index < pending.Count; index++)
		{
			pending[index].TrySetException(new PlatformNotSupportedException(reason));
		}
	}
}

/// <summary>A boundary before which completed GPU frames are excluded from a collection.</summary>
public readonly record struct GpuProfileCollectionMarker(ulong? ExclusiveFrameIndex);

public sealed class GpuProfileFrame
{
	public GpuProfileFrame(ulong frameIndex, IReadOnlyList<GpuProfilePass> passes)
	{
		FrameIndex = frameIndex;
		Passes = Copy(passes);
		double total = 0.0;
		for (var i = 0; i < passes.Count; i++)
		{
			total += passes[i].DurationMs;
		}
		DurationMs = total;
	}

	public ulong FrameIndex { get; }
	public double DurationMs { get; }
	public IReadOnlyList<GpuProfilePass> Passes { get; }

	private static GpuProfilePass[] Copy(IReadOnlyList<GpuProfilePass> source)
	{
		var result = new GpuProfilePass[source.Count];
		for (var i = 0; i < source.Count; i++)
		{
			result[i] = source[i];
		}
		return result;
	}
}

public sealed class GpuProfilePass
{
	public GpuProfilePass(string name, IReadOnlyList<GpuProfileScope> scopes)
	{
		Name = name;
		Scopes = Copy(scopes);
		double total = 0.0;
		for (var i = 0; i < scopes.Count; i++)
		{
			total += scopes[i].DurationMs;
		}
		DurationMs = total;
	}

	public string Name { get; }
	public double DurationMs { get; }
	public IReadOnlyList<GpuProfileScope> Scopes { get; }

	private static GpuProfileScope[] Copy(IReadOnlyList<GpuProfileScope> source)
	{
		var result = new GpuProfileScope[source.Count];
		for (var i = 0; i < source.Count; i++)
		{
			result[i] = source[i];
		}
		return result;
	}
}

public readonly record struct GpuProfileScope(string Name, double DurationMs);

internal sealed class GpuProfileFrameCapture
{
	private readonly object _sync = new();
	private readonly Action<GpuProfileFrame> _publish;
	private readonly List<GpuProfilePassCapture> _passes = new();
	private bool _sealed;
	private int _completedPassCount;

	public GpuProfileFrameCapture(ulong frameIndex, Action<GpuProfileFrame> publish)
	{
		FrameIndex = frameIndex;
		_publish = publish;
	}

	public ulong FrameIndex { get; }

	public GpuProfilePassCapture AddPass(string name)
	{
		lock (_sync)
		{
			var pass = new GpuProfilePassCapture(FrameIndex, name, _passes.Count, OnPassCompleted);
			_passes.Add(pass);
			return pass;
		}
	}

	public void Seal()
	{
		GpuProfileFrame? completed = null;
		lock (_sync)
		{
			_sealed = true;
			completed = TryBuildCompletedFrame();
		}
		if (completed is not null)
		{
			_publish(completed);
		}
	}

	private void OnPassCompleted()
	{
		GpuProfileFrame? completed = null;
		lock (_sync)
		{
			_completedPassCount++;
			completed = TryBuildCompletedFrame();
		}
		if (completed is not null)
		{
			_publish(completed);
		}
	}

	private GpuProfileFrame? TryBuildCompletedFrame()
	{
		if (!_sealed || _completedPassCount != _passes.Count)
		{
			return null;
		}

		var passes = new List<GpuProfilePass>(_passes.Count);
		for (var i = 0; i < _passes.Count; i++)
		{
			var pass = _passes[i];
			if (pass.Scopes.Count > 0)
			{
				passes.Add(new GpuProfilePass(pass.Name, pass.Scopes));
			}
		}
		return new GpuProfileFrame(FrameIndex, passes);
	}
}

internal sealed class GpuProfilePassCapture
{
	private readonly Action _completed;
	private int _isCompleted;

	public GpuProfilePassCapture(ulong frameIndex, string name, int sequence, Action completed)
	{
		FrameIndex = frameIndex;
		Name = name;
		Sequence = sequence;
		_completed = completed;
	}

	public ulong FrameIndex { get; }
	public string Name { get; }
	public int Sequence { get; }
	public IReadOnlyList<GpuProfileScope> Scopes { get; private set; } = Array.Empty<GpuProfileScope>();

	public void Complete(IReadOnlyList<GpuProfileScope> scopes)
	{
		if (Interlocked.Exchange(ref _isCompleted, 1) != 0)
		{
			return;
		}
		Scopes = scopes;
		_completed();
	}
}

internal static class GpuProfileNames
{
	public static string FromPipeline(in PipelineKey key)
	{
		if (!string.IsNullOrWhiteSpace(key.ShaderVariant))
		{
			return key.ShaderVariant;
		}
		if (!string.IsNullOrWhiteSpace(key.ComputeEntryPoint))
		{
			return key.ComputeEntryPoint;
		}

		var vertex = string.IsNullOrWhiteSpace(key.VertexEntryPoint) ? "Vertex" : key.VertexEntryPoint;
		var pixel = string.IsNullOrWhiteSpace(key.PixelEntryPoint) ? "Pixel" : key.PixelEntryPoint;
		return $"{vertex} + {pixel}";
	}
}

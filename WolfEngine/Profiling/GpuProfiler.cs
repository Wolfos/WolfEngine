#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Profiling;

public sealed class GpuProfiler
{
	private int _enabled;
	private GpuProfileFrame? _latestFrame;
	private string? _unsupportedReason;
	private readonly object _publishSync = new();

	public bool Enabled
	{
		get => Volatile.Read(ref _enabled) != 0;
		set => Volatile.Write(ref _enabled, value ? 1 : 0);
	}

	public GpuProfileFrame? LatestFrame => Volatile.Read(ref _latestFrame);

	public string? UnsupportedReason => Volatile.Read(ref _unsupportedReason);

	internal void SetBackendAvailability(bool supported, string? unsupportedReason)
	{
		Volatile.Write(ref _unsupportedReason, supported ? null : unsupportedReason ?? "GPU profiling is not supported by this graphics device.");
		if (!supported)
		{
			Enabled = false;
		}
	}

	internal GpuProfileFrameCapture? BeginFrame(ulong frameIndex)
	{
		return Enabled && UnsupportedReason is null
			? new GpuProfileFrameCapture(frameIndex, Publish)
			: null;
	}

	private void Publish(GpuProfileFrame frame)
	{
		lock (_publishSync)
		{
			if (_latestFrame is null || frame.FrameIndex >= _latestFrame.FrameIndex)
			{
				Volatile.Write(ref _latestFrame, frame);
			}
		}
	}
}

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

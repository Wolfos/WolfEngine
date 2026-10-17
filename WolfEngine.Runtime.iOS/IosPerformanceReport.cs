using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using WolfEngine.Profiling;

namespace WolfEngine.Runtime.iOS;

/// <summary>
/// Diagnostic: every few seconds, prints the frame rate, the process's memory footprint against what iOS still
/// allows it, and GPU time per render pass. Off unless <c>WOLF_PERF_REPORT</c> is set at launch, for example
/// with <c>devicectl device process launch --environment-variables '{"WOLF_PERF_REPORT":"1"}'</c>: <c>1</c>
/// reports everything, <c>basic</c> leaves the GPU profiler off and reports only frame rate and memory.
/// </summary>
internal sealed class IosPerformanceReport
{
	private const double ReportIntervalSeconds = 5.0;
	private const int ReportedPassCount = 12;

	// Offsets into task_vm_info_data_t, which is packed to 4 bytes.
	private const int TaskVmInfo = 22;
	private const int TaskVmInfoNaturalCount = 93;
	private const int PhysFootprintOffset = 144;
	private const int PhysFootprintPeakOffset = 168;

	// mach_task_self() is a C macro that reads this global, not an exported function.
	private static readonly Lazy<uint> TaskSelf = new(() =>
	{
		var system = NativeLibrary.Load("/usr/lib/libSystem.dylib");

		return (uint)Marshal.ReadInt32(NativeLibrary.GetExport(system, "mach_task_self_"));
	});

	private readonly GpuProfiler? _gpuProfiler;
	private readonly List<double> _frameIntervals = [];
	private double _previousTimestamp;
	private double _windowStart;
	private ulong? _lastGpuFrameIndex;

	private IosPerformanceReport(GpuProfiler? gpuProfiler)
	{
		_gpuProfiler = gpuProfiler;
	}

	public static IosPerformanceReport? CreateIfRequested(GpuProfiler gpuProfiler)
	{
		var mode = Environment.GetEnvironmentVariable("WOLF_PERF_REPORT");
		if (mode == "basic")
		{
			return new IosPerformanceReport(null);
		}

		if (mode != "1")
		{
			return null;
		}

		var marker = gpuProfiler.BeginCollection();
		var report = new IosPerformanceReport(gpuProfiler)
		{
			_lastGpuFrameIndex = marker.ExclusiveFrameIndex
		};

		return report;
	}

	/// <summary>Call once per presented frame with the display link's timestamp, in seconds.</summary>
	public void OnFrame(double timestamp)
	{
		if (_previousTimestamp > 0.0)
		{
			_frameIntervals.Add(timestamp - _previousTimestamp);
		}
		else
		{
			Console.WriteLine("[perf] first frame presented");
			_windowStart = timestamp;
		}

		_previousTimestamp = timestamp;
		if (timestamp - _windowStart < ReportIntervalSeconds)
		{
			return;
		}

		Console.WriteLine(Format(timestamp - _windowStart));
		_frameIntervals.Clear();
		_windowStart = timestamp;
	}

	private string Format(double windowSeconds)
	{
		var report = new StringBuilder();
		var frameMs = _frameIntervals.Select(interval => interval * 1000.0).Order().ToArray();
		report.Append($"[perf] {frameMs.Length / windowSeconds:F1} fps, frame {Percentile(frameMs, 0.5):F1} ms median, ");
		report.Append($"{Percentile(frameMs, 0.95):F1} ms p95, {(frameMs.Length > 0 ? frameMs[^1] : 0.0):F1} ms max");
		report.AppendLine();

		var (footprint, footprintPeak) = ReadFootprint();
		var available = os_proc_available_memory();
		report.Append($"[perf] memory: footprint {Megabytes(footprint)}, peak {Megabytes(footprintPeak)}, ");
		report.Append($"{Megabytes(available)} left before the limit (~{Megabytes(footprint + available)})");
		report.AppendLine();

		AppendGpuTimes(report);

		return report.ToString().TrimEnd();
	}

	private void AppendGpuTimes(StringBuilder report)
	{
		if (_gpuProfiler is null)
		{
			return;
		}

		if (_gpuProfiler.UnsupportedReason is { } reason)
		{
			report.Append($"[perf] GPU timing unavailable: {reason}");

			return;
		}

		var frames = _gpuProfiler.GetCompletedFramesAfter(_lastGpuFrameIndex);
		if (frames.Count == 0)
		{
			report.Append("[perf] GPU: no completed frames");

			return;
		}

		_lastGpuFrameIndex = frames[^1].FrameIndex;
		var frameMs = frames.Select(frame => frame.DurationMs).Order().ToArray();
		report.Append($"[perf] GPU frame {Percentile(frameMs, 0.5):F2} ms median, {Percentile(frameMs, 0.95):F2} ms p95 over {frames.Count} frames");
		report.AppendLine();

		var passes = frames
			.SelectMany(frame => frame.Passes)
			.GroupBy(pass => pass.Name)
			.Select(group => (Name: group.Key, MedianMs: Percentile(group.Select(pass => pass.DurationMs).Order().ToArray(), 0.5)))
			.OrderByDescending(pass => pass.MedianMs)
			.Take(ReportedPassCount);
		foreach (var (name, medianMs) in passes)
		{
			report.AppendLine($"[perf]   {medianMs,6:F2} ms  {name}");
		}
	}

	private static double Percentile(double[] sorted, double fraction)
	{
		if (sorted.Length == 0)
		{
			return 0.0;
		}

		var index = (int)Math.Clamp(Math.Ceiling(fraction * sorted.Length) - 1, 0, sorted.Length - 1);

		return sorted[index];
	}

	private static (ulong Footprint, ulong Peak) ReadFootprint()
	{
		Span<byte> info = stackalloc byte[TaskVmInfoNaturalCount * sizeof(uint)];
		var count = (uint)TaskVmInfoNaturalCount;
		unsafe
		{
			fixed (byte* buffer = info)
			{
				if (task_info(TaskSelf.Value, TaskVmInfo, buffer, ref count) != 0)
				{
					return (0, 0);
				}
			}
		}

		return (
			BinaryPrimitives.ReadUInt64LittleEndian(info[PhysFootprintOffset..]),
			BinaryPrimitives.ReadUInt64LittleEndian(info[PhysFootprintPeakOffset..]));
	}

	private static string Megabytes(ulong bytes)
	{
		return $"{bytes / (1024.0 * 1024.0):F0} MB";
	}

	[DllImport("/usr/lib/libSystem.dylib")]
	private static extern unsafe int task_info(uint task, int flavor, byte* info, ref uint count);

	[DllImport("/usr/lib/libSystem.dylib")]
	private static extern ulong os_proc_available_memory();
}

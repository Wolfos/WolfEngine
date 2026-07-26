using System.Runtime.Versioning;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using WolfEngine.Profiling;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Backend.Metal;

[SupportedOSPlatform("macos")]
internal sealed class MetalGpuProfilerBackend : IGpuProfilerCaptureBackend
{
	internal const ulong SamplesPerBlock = 64;

	private readonly MTLDevice _device;
	private readonly MTLCounterSet _timestampCounterSet;
	private readonly Stack<TimestampBlock> _pool = new();
	private readonly object _poolSync = new();
	private TimestampBlock? _activeBlock;
	private ulong _activeFrameIndex = ulong.MaxValue;
	private string? _unsupportedReason;

	public MetalGpuProfilerBackend(MTLDevice device)
	{
		_device = device;
		if (!_device.SupportsCounterSampling(MTLCounterSamplingPoint.AtStageBoundary))
		{
			_unsupportedReason = "This Metal device does not support timestamp sampling at shader stage boundaries.";
			return;
		}

		var counterSets = _device.CounterSets;
		for (ulong i = 0; i < counterSets.Count; i++)
		{
			var candidate = new MTLCounterSet(counterSets.Object(i));
			if (candidate.Name.ToString()?.Contains("timestamp", StringComparison.OrdinalIgnoreCase) == true)
			{
				_timestampCounterSet = candidate;
				break;
			}
		}
		if (_timestampCounterSet.NativePtr == IntPtr.Zero)
		{
			_unsupportedReason = "This Metal device does not expose the timestamp counter set.";
		}
	}

	public bool IsSupported => _timestampCounterSet.NativePtr != IntPtr.Zero && UnsupportedReason is null;
	public string? UnsupportedReason => Volatile.Read(ref _unsupportedReason);

	public void Attach(IGfxCommandList commandList, GpuProfilePassCapture passCapture)
	{
		if (!IsSupported)
		{
			passCapture.Complete(Array.Empty<GpuProfileScope>());
			return;
		}
		if (commandList is not MetalCommandList metalCommandList)
		{
			throw new InvalidOperationException("GPU profiler received a command list from another backend.");
		}
		metalCommandList.AttachGpuProfiler(this, passCapture);
	}

	internal SampleReservation ReserveSamples(ulong frameIndex, ulong sampleCount)
	{
		lock (_poolSync)
		{
			if (_activeFrameIndex != frameIndex)
			{
				SealActiveBlock();
				_activeFrameIndex = frameIndex;
			}

			if (_activeBlock is null || _activeBlock.UsedSamples + sampleCount > SamplesPerBlock)
			{
				SealActiveBlock();
				_activeBlock = RentOrCreateBlock();
			}

			var startIndex = _activeBlock.UsedSamples;
			_activeBlock.UsedSamples += sampleCount;
			return new SampleReservation(_activeBlock, startIndex);
		}
	}

	internal void RetainBlock(TimestampBlock block)
	{
		lock (_poolSync)
		{
			block.ReferenceCount++;
		}
	}

	internal void ReleaseBlocks(List<TimestampBlock> blocks)
	{
		lock (_poolSync)
		{
			for (var i = 0; i < blocks.Count; i++)
			{
				var block = blocks[i];
				block.ReferenceCount--;
				if (block.IsSealed && block.ReferenceCount == 0)
				{
					ReturnBlock(block);
				}
			}
		}
		blocks.Clear();
	}

	internal void ReportFailure(Exception exception)
	{
		Volatile.Write(ref _unsupportedReason, $"Metal GPU timestamp profiling failed: {exception.Message}");
	}

	private TimestampBlock RentOrCreateBlock()
	{
		if (_pool.Count > 0)
		{
			var pooled = _pool.Pop();
			pooled.Reset();
			return pooled;
		}

		using var descriptor = new MTLCounterSampleBufferDescriptor
		{
			CounterSet = _timestampCounterSet,
			SampleCount = SamplesPerBlock,
			StorageMode = MTLStorageMode.Shared
		};
		NSError error = default;
		var sampleBuffer = _device.NewCounterSampleBuffer(descriptor, ref error);
		if (sampleBuffer.NativePtr == IntPtr.Zero)
		{
			var reason = error.NativePtr == IntPtr.Zero ? "Unknown Metal error." : error.LocalizedDescription.ToString();
			throw new InvalidOperationException($"Failed to allocate a Metal timestamp sample buffer: {reason}");
		}
		return new TimestampBlock(sampleBuffer);
	}

	private void SealActiveBlock()
	{
		if (_activeBlock is null)
		{
			return;
		}
		_activeBlock.IsSealed = true;
		if (_activeBlock.ReferenceCount == 0)
		{
			ReturnBlock(_activeBlock);
		}
		_activeBlock = null;
	}

	private void ReturnBlock(TimestampBlock block)
	{
		block.Reset();
		_pool.Push(block);
	}

	internal static double TicksToMilliseconds(ulong start, ulong end)
	{
		// Metal timestamp counter values are nanoseconds.
		return end >= start ? (end - start) / 1_000_000.0 : 0.0;
	}

	internal readonly record struct SampleReservation(TimestampBlock Block, ulong StartIndex);

	internal sealed unsafe class TimestampBlock
	{
		public TimestampBlock(MTLCounterSampleBuffer sampleBuffer)
		{
			SampleBuffer = sampleBuffer;
		}

		public MTLCounterSampleBuffer SampleBuffer { get; }
		public ulong UsedSamples { get; set; }
		public int ReferenceCount { get; set; }
		public bool IsSealed { get; set; }

		public void Reset()
		{
			UsedSamples = 0;
			ReferenceCount = 0;
			IsSealed = false;
		}

		public (ulong Start, ulong End) ReadPair(ulong startIndex, ulong endIndex)
		{
			if (endIndex < startIndex)
			{
				throw new ArgumentOutOfRangeException(nameof(endIndex));
			}
			var length = endIndex - startIndex + 1;
			using var data = SampleBuffer.ResolveCounterRange(new NSRange { location = startIndex, length = length });
			if (data.MutableBytes == IntPtr.Zero || data.Length < length * sizeof(ulong))
			{
				throw new InvalidOperationException("Metal returned an incomplete timestamp counter result.");
			}
			var values = new ReadOnlySpan<ulong>((void*)data.MutableBytes, checked((int)length));
			return (values[0], values[checked((int)(endIndex - startIndex))]);
		}
	}
}

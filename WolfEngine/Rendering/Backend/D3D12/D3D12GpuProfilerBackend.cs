#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using WolfEngine.Profiling;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Backend.D3D12;

internal sealed unsafe class D3D12GpuProfilerBackend : IGpuProfilerCaptureBackend, IDisposable
{
	internal const uint SamplesPerBlock = 256;

	private readonly D3D12Device _owner;
	private readonly ComPtr<ID3D12Device> _device;
	private readonly Stack<TimestampBlock> _pool = new();
	private readonly object _poolSync = new();
	private readonly ulong _timestampFrequency;
	private string? _unsupportedReason;
	private bool _disposed;

	public D3D12GpuProfilerBackend(
		D3D12Device owner,
		ComPtr<ID3D12Device> device,
		ComPtr<ID3D12CommandQueue> graphicsQueue)
	{
		_owner = owner;
		_device = device;
		ulong frequency = 0;
		var result = graphicsQueue.Handle is null ? -1 : graphicsQueue.Handle->GetTimestampFrequency(&frequency);
		if (result < 0 || frequency == 0)
		{
			_unsupportedReason = "The D3D12 graphics queue does not expose a valid timestamp frequency.";
			return;
		}
		_timestampFrequency = frequency;
	}

	public bool IsSupported => _timestampFrequency != 0 && UnsupportedReason is null;
	public string? UnsupportedReason => Volatile.Read(ref _unsupportedReason);

	public void Attach(IGfxCommandList commandList, GpuProfilePassCapture passCapture)
	{
		if (!IsSupported)
		{
			passCapture.Complete(Array.Empty<GpuProfileScope>());
			return;
		}
		if (commandList is not D3D12CommandList d3dCommandList)
		{
			throw new InvalidOperationException("GPU profiler received a command list from another backend.");
		}
		d3dCommandList.AttachGpuProfiler(this, passCapture);
	}

	internal TimestampBlock RentBlock()
	{
		lock (_poolSync)
		{
			if (_pool.Count > 0)
			{
				var pooled = _pool.Pop();
				pooled.Reset();
				return pooled;
			}
		}

		var heapDescriptor = new QueryHeapDesc
		{
			Type = QueryHeapType.Timestamp,
			Count = SamplesPerBlock,
			NodeMask = 0
		};
		SilkMarshal.ThrowHResult(_device.CreateQueryHeap(in heapDescriptor, out ComPtr<ID3D12QueryHeap> heap));
		var readback = (D3D12Buffer)_owner.CreateBuffer(
			new BufferDescriptor(SamplesPerBlock * sizeof(ulong), BufferUsage.Staging));
		return new TimestampBlock(heap, readback);
	}

	internal void ReturnBlocks(List<TimestampBlock> blocks)
	{
		lock (_poolSync)
		{
			for (var i = 0; i < blocks.Count; i++)
			{
				blocks[i].Reset();
				_pool.Push(blocks[i]);
			}
		}
		blocks.Clear();
	}

	internal double TicksToMilliseconds(ulong start, ulong end)
	{
		return end >= start ? (end - start) * 1000.0 / _timestampFrequency : 0.0;
	}

	internal void ReportFailure(Exception exception)
	{
		Volatile.Write(ref _unsupportedReason, $"D3D12 GPU timestamp profiling failed: {exception.Message}");
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		lock (_poolSync)
		{
			while (_pool.Count > 0)
			{
				_pool.Pop().Dispose();
			}
		}
	}

	internal sealed class TimestampBlock : IDisposable
	{
		public TimestampBlock(ComPtr<ID3D12QueryHeap> heap, D3D12Buffer readback)
		{
			Heap = heap;
			Readback = readback;
		}

		public ComPtr<ID3D12QueryHeap> Heap { get; private set; }
		public D3D12Buffer Readback { get; }
		public uint UsedSamples { get; set; }

		public void Reset() => UsedSamples = 0;

		public ulong[] ReadResults()
		{
			var values = new ulong[UsedSamples];
			Readback.Read(MemoryMarshal.AsBytes(values.AsSpan()));
			return values;
		}

		public void Dispose()
		{
			Readback.Resource.Dispose();
			Heap.Dispose();
			Heap = default;
		}
	}
}

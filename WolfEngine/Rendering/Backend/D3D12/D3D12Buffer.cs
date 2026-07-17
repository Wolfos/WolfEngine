using System;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Backend.D3D12;

internal sealed unsafe class D3D12Buffer : IWritableGpuBuffer, IReadableGpuBuffer, IDisposable
{
	private readonly BufferDescriptor _descriptor;
	private readonly bool _cpuWritableDirect;
	private readonly bool _cpuReadableDirect;
	private readonly Action<D3D12Buffer, ulong, ulong>? _flushUploadRange;
	private readonly Func<int>? _getDeviceRemovedReason;
	private void* _writeMappedPtr;
	private void* _readMappedPtr;
	private bool _disposed;

	public D3D12Buffer(
		string? name,
		BufferDescriptor descriptor,
		ComPtr<ID3D12Resource> resource,
		ulong sizeInBytes,
		ComPtr<ID3D12Resource> uploadResource = default,
		bool cpuWritableDirect = false,
		bool cpuReadableDirect = false,
		Action<D3D12Buffer, ulong, ulong>? flushUploadRange = null,
		Func<int>? getDeviceRemovedReason = null,
		ResourceStates initialState = ResourceStates.Common)
	{
		Name = name;
		_descriptor = descriptor;
		Resource = resource;
		UploadResource = uploadResource;
		SizeInBytes = sizeInBytes;
		_cpuWritableDirect = cpuWritableDirect;
		_cpuReadableDirect = cpuReadableDirect;
		_flushUploadRange = flushUploadRange;
		_getDeviceRemovedReason = getDeviceRemovedReason;
		CurrentState = initialState;
	}

	public string? Name { get; }

	public BufferDescriptor Descriptor => _descriptor;

	public ComPtr<ID3D12Resource> Resource { get; private set; }

	public ComPtr<ID3D12Resource> UploadResource { get; private set; }

	public ulong SizeInBytes { get; }

	internal ResourceStates CurrentState { get; set; }

	internal bool IsCpuWritableDirect => _cpuWritableDirect;

	internal bool IsCpuReadableDirect => _cpuReadableDirect;

	internal uint GetConstantBufferViewSizeInBytes()
	{
		var alignment = (ulong)Silk.NET.Direct3D12.D3D12.ConstantBufferDataPlacementAlignment;
		var alignedSize = AlignDown(SizeInBytes, alignment);
		if (alignedSize < alignment)
		{
			throw new InvalidOperationException(
				$"Cannot create a CBV for buffer '{Name ?? "<unnamed>"}' with size {SizeInBytes} bytes. " +
				$"CBVs require at least {alignment} bytes.");
		}

		// D3D12 CBV descriptors are limited to 64 KiB.
		var maxCbvSize = 64UL * 1024UL;
		if (alignedSize > maxCbvSize)
		{
			alignedSize = maxCbvSize;
		}

		return (uint)alignedSize;
	}

	public void Write<T>(ReadOnlySpan<T> source, ulong elementOffset = 0) where T : unmanaged
	{
		ObjectDisposedException.ThrowIf(_disposed, this);

		if (source.IsEmpty)
		{
			return;
		}

		var elementSize = (ulong)sizeof(T);
		var byteOffset = elementOffset * elementSize;
		var byteCount = (ulong)source.Length * elementSize;
		if (byteOffset + byteCount > SizeInBytes)
		{
			throw new ArgumentOutOfRangeException(nameof(elementOffset), "Write range exceeds buffer size.");
		}

		var target = _cpuWritableDirect ? Resource : UploadResource;
		if (target.Handle is null)
		{
			throw new InvalidOperationException("Buffer does not support CPU writes on this backend.");
		}

		if (_writeMappedPtr is null)
		{
			void* mapped = null;
			var mapResult = target.Map(0, (Silk.NET.Direct3D12.Range*)null, &mapped);
			if (mapResult < 0)
			{
				var mapResultCode = unchecked((uint)mapResult);
				var message =
					$"ID3D12Resource::Map failed while writing buffer '{Name ?? "<unnamed>"}' " +
					$"(offset={byteOffset}, size={byteCount}, cpuWritableDirect={_cpuWritableDirect}) with HRESULT 0x{mapResultCode:X8}.";

				if (_getDeviceRemovedReason is not null)
				{
					var removedReason = _getDeviceRemovedReason();
					if (removedReason < 0)
					{
						var reasonCode = unchecked((uint)removedReason);
						message += $" DeviceRemovedReason=0x{reasonCode:X8}.";
					}
				}

				throw new COMException(message, mapResult);
			}

			_writeMappedPtr = mapped;
		}

		fixed (T* src = source)
		{
			var dest = (byte*)_writeMappedPtr + (nint)byteOffset;
			Buffer.MemoryCopy(src, dest, byteCount, byteCount);
		}

		if (_cpuWritableDirect == false)
		{
			_flushUploadRange?.Invoke(this, byteOffset, byteCount);
		}
	}

	public void Read(Span<byte> destination, ulong sourceOffset = 0)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);

		if (destination.IsEmpty)
		{
			return;
		}

		if (_cpuReadableDirect == false)
		{
			throw new InvalidOperationException("Buffer does not support CPU reads on this backend.");
		}

		var byteCount = (ulong)destination.Length;
		if (sourceOffset + byteCount > SizeInBytes)
		{
			throw new ArgumentOutOfRangeException(nameof(destination), "Read range exceeds buffer size.");
		}

		if (_readMappedPtr is null)
		{
			void* mapped = null;
			var mapResult = Resource.Map(0, (Silk.NET.Direct3D12.Range*)null, &mapped);
			if (mapResult < 0)
			{
				throw new COMException("ID3D12Resource::Map failed while reading a GPU buffer.", mapResult);
			}

			_readMappedPtr = mapped;
		}

		fixed (byte* dst = destination)
		{
			var src = (byte*)_readMappedPtr + (nint)sourceOffset;
			Buffer.MemoryCopy(src, dst, byteCount, byteCount);
		}
	}

	private static ulong AlignDown(ulong size, ulong alignment)
	{
		return size & ~(alignment - 1UL);
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		if (_writeMappedPtr is not null)
		{
			var mappedResource = _cpuWritableDirect ? Resource : UploadResource;
			if (mappedResource.Handle is not null)
			{
				mappedResource.Unmap(0, (Silk.NET.Direct3D12.Range*)null);
			}

			_writeMappedPtr = null;
		}

		if (_readMappedPtr is not null)
		{
			if (Resource.Handle is not null)
			{
				Resource.Unmap(0, (Silk.NET.Direct3D12.Range*)null);
			}

			_readMappedPtr = null;
		}

		if (UploadResource.Handle is not null)
		{
			UploadResource.Dispose();
			UploadResource = default;
		}

		if (Resource.Handle is not null)
		{
			Resource.Dispose();
			Resource = default;
		}
	}
}

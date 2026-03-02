using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Backend.D3D12;

internal sealed unsafe class D3D12Buffer : IWritableGpuBuffer
{
	private readonly BufferDescriptor _descriptor;
	private readonly bool _cpuWritableDirect;
	private readonly Action<D3D12Buffer, ulong, ulong>? _flushUploadRange;
	private readonly Func<int>? _getDeviceRemovedReason;
	private void* _mappedPtr;

	public D3D12Buffer(
		string? name,
		BufferDescriptor descriptor,
		ComPtr<ID3D12Resource> resource,
		ulong sizeInBytes,
		ComPtr<ID3D12Resource> uploadResource = default,
		bool cpuWritableDirect = false,
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
		_flushUploadRange = flushUploadRange;
		_getDeviceRemovedReason = getDeviceRemovedReason;
		CurrentState = initialState;
	}

	public string? Name { get; }

	public BufferDescriptor Descriptor => _descriptor;

	public ComPtr<ID3D12Resource> Resource { get; private set; }

	public ComPtr<ID3D12Resource> UploadResource { get; }

	public ulong SizeInBytes { get; }

	internal ResourceStates CurrentState { get; set; }

	internal bool IsCpuWritableDirect => _cpuWritableDirect;

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

		if (_mappedPtr is null)
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

			_mappedPtr = mapped;
		}

		fixed (T* src = source)
		{
			var dest = (byte*)_mappedPtr + (nint)byteOffset;
			Buffer.MemoryCopy(src, dest, byteCount, byteCount);
		}

		if (_cpuWritableDirect == false)
		{
			_flushUploadRange?.Invoke(this, byteOffset, byteCount);
		}
	}

	private static ulong AlignDown(ulong size, ulong alignment)
	{
		return size & ~(alignment - 1UL);
	}
}

using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Backend.D3D12;

internal sealed unsafe class D3D12Buffer : IWritableGpuBuffer
{
	private readonly BufferDescriptor _descriptor;
	private readonly bool _cpuWritableDirect;
	private readonly Action<D3D12Buffer, ulong, ulong>? _flushUploadRange;
	private void* _mappedPtr;

	public D3D12Buffer(
		string? name,
		BufferDescriptor descriptor,
		ComPtr<ID3D12Resource> resource,
		ulong sizeInBytes,
		ComPtr<ID3D12Resource> uploadResource = default,
		bool cpuWritableDirect = false,
		Action<D3D12Buffer, ulong, ulong>? flushUploadRange = null,
		ResourceStates initialState = ResourceStates.Common)
	{
		Name = name;
		_descriptor = descriptor;
		Resource = resource;
		UploadResource = uploadResource;
		SizeInBytes = sizeInBytes;
		_cpuWritableDirect = cpuWritableDirect;
		_flushUploadRange = flushUploadRange;
		CurrentState = initialState;
	}

	public string? Name { get; }

	public BufferDescriptor Descriptor => _descriptor;

	public ComPtr<ID3D12Resource> Resource { get; private set; }

	public ComPtr<ID3D12Resource> UploadResource { get; }

	public ulong SizeInBytes { get; }

	internal ResourceStates CurrentState { get; set; }

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
			SilkMarshal.ThrowHResult(target.Map(0, (Silk.NET.Direct3D12.Range*)null, &mapped));
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
}

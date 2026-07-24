using System;
using System.Runtime.InteropServices;
using SharpMetal.Metal;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Backend.Metal;

internal sealed class MetalBuffer : IWritableGpuBuffer, IReadableGpuBuffer, IDisposable
{
	// Metal indirect commands cache native buffer pointers without retaining them.
	// Keep disposal pending until every ICB that captured this buffer has been reset.
	private readonly object _lifetimeSync = new();
	private int _indirectReferenceCount;
	private bool _disposeRequested;
	private bool _nativeDisposed;

	public MetalBuffer(string name, BufferDescriptor descriptor, MTLBuffer buffer)
	{
		Name = name;
		Descriptor = descriptor;
		Buffer = buffer;
	}

	public string Name { get; }

	public BufferDescriptor Descriptor { get; }

	public MTLBuffer Buffer { get; }

	internal bool IsDisposed
	{
		get
		{
			lock (_lifetimeSync)
			{
				return _disposeRequested;
			}
		}
	}

	internal int IndirectReferenceCount
	{
		get
		{
			lock (_lifetimeSync)
			{
				return _indirectReferenceCount;
			}
		}
	}

	public unsafe void Write<T>(ReadOnlySpan<T> source, ulong elementOffset = 0) where T : unmanaged
	{
		ThrowIfDisposed();
		if (source.IsEmpty)
		{
			return;
		}

		var byteOffset = checked(elementOffset * (ulong)sizeof(T));
		var byteCount = checked((ulong)source.Length * (ulong)sizeof(T));
		var end = checked(byteOffset + byteCount);
		if (end > Buffer.Length)
		{
			throw new ArgumentOutOfRangeException(nameof(source), "Write exceeds destination buffer size.");
		}

		var destination = new Span<byte>(
			(byte*)Buffer.Contents.ToPointer() + (nint)byteOffset,
			(int)byteCount);
		MemoryMarshal.AsBytes(source).CopyTo(destination);
	}

	public unsafe void Read(Span<byte> destination, ulong sourceOffset = 0)
	{
		ThrowIfDisposed();
		if (destination.IsEmpty)
		{
			return;
		}

		var byteCount = (ulong)destination.Length;
		var end = checked(sourceOffset + byteCount);
		if (end > Buffer.Length)
		{
			throw new ArgumentOutOfRangeException(nameof(destination), "Read exceeds source buffer size.");
		}

		var source = new ReadOnlySpan<byte>(
			(byte*)Buffer.Contents.ToPointer() + (nint)sourceOffset,
			destination.Length);
		source.CopyTo(destination);
	}

	public void Dispose()
	{
		var releaseNativeBuffer = false;
		lock (_lifetimeSync)
		{
			if (_disposeRequested)
			{
				return;
			}

			_disposeRequested = true;
			if (_indirectReferenceCount == 0 && _nativeDisposed == false)
			{
				_nativeDisposed = true;
				releaseNativeBuffer = true;
			}
		}

		if (releaseNativeBuffer && Buffer.NativePtr != IntPtr.Zero)
		{
			Buffer.Dispose();
		}
	}

	internal void RetainForIndirectUse()
	{
		lock (_lifetimeSync)
		{
			if (_disposeRequested)
			{
				throw new ObjectDisposedException(Name);
			}

			_indirectReferenceCount = checked(_indirectReferenceCount + 1);
		}
	}

	internal void ReleaseFromIndirectUse()
	{
		var releaseNativeBuffer = false;
		lock (_lifetimeSync)
		{
			if (_indirectReferenceCount <= 0)
			{
				throw new InvalidOperationException($"Metal buffer '{Name}' has no indirect reference to release.");
			}

			_indirectReferenceCount--;
			if (_indirectReferenceCount == 0 && _disposeRequested && _nativeDisposed == false)
			{
				_nativeDisposed = true;
				releaseNativeBuffer = true;
			}
		}

		if (releaseNativeBuffer && Buffer.NativePtr != IntPtr.Zero)
		{
			Buffer.Dispose();
		}
	}

	private void ThrowIfDisposed()
	{
		if (IsDisposed)
		{
			throw new ObjectDisposedException(Name);
		}
	}
}

using System;
using System.Runtime.InteropServices;
using SharpMetal.Metal;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Backend.Metal;

internal sealed class MetalBuffer : IWritableGpuBuffer, IDisposable
{
	public MetalBuffer(string name, BufferDescriptor descriptor, MTLBuffer buffer)
	{
		Name = name;
		Descriptor = descriptor;
		Buffer = buffer;
	}

	public string Name { get; }

	public BufferDescriptor Descriptor { get; }

	public MTLBuffer Buffer { get; }

	public unsafe void Write<T>(ReadOnlySpan<T> source, ulong elementOffset = 0) where T : unmanaged
	{
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

	public void Dispose()
	{
		if (Buffer.NativePtr != IntPtr.Zero)
		{
			Buffer.Dispose();
		}
	}
}

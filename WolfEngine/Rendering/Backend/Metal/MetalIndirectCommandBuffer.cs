#nullable enable

using System;
using System.Runtime.Versioning;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Backend.Metal;

public sealed class MetalIndirectCommandBuffer : IIndirectCommandBuffer, IDisposable
{
	private readonly MTLIndirectCommandBuffer _buffer;
	private readonly uint _maxCommandCount;

	public MetalIndirectCommandBuffer(MTLIndirectCommandBuffer buffer, uint maxCommandCount)
	{
		_buffer = buffer;
		_maxCommandCount = maxCommandCount;
	}

	public uint MaxCommandCount => _maxCommandCount;

	public MTLIndirectCommandBuffer Buffer => _buffer;

	public void Reset(uint commandCount)
	{
		var count = (ulong)Math.Min(commandCount, _maxCommandCount);
		var range = new NSRange { location = 0, length = count };
		_buffer.Reset(range);
	}

	public MTLIndirectRenderCommand GetRenderCommand(uint index)
	{
		return _buffer.IndirectRenderCommand(index);
	}

	public void Dispose()
	{
		if (_buffer.NativePtr != IntPtr.Zero)
		{
			_buffer.Dispose();
		}
	}
}

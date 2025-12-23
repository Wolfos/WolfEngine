using SharpMetal.Metal;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Backend.Metal;

internal sealed class MetalBuffer : IGfxBuffer, IDisposable
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

	public void Dispose()
	{
		if (Buffer.NativePtr != IntPtr.Zero)
		{
			Buffer.Dispose();
		}
	}
}

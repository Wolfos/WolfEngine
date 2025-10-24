using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Backend.D3D12;

internal sealed class D3D12Buffer : IGfxBuffer
{
	private readonly BufferDescriptor _descriptor;

	public D3D12Buffer(string? name, BufferDescriptor descriptor, ComPtr<ID3D12Resource> resource,
		ulong sizeInBytes)
	{
		Name = name;
		_descriptor = descriptor;
		Resource = resource;
		SizeInBytes = sizeInBytes;
	}

	public string? Name { get; }

	public BufferDescriptor Descriptor => _descriptor;

	public ComPtr<ID3D12Resource> Resource { get; private set; }

	public ulong SizeInBytes { get; }
}
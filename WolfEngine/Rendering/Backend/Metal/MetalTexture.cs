using SharpMetal.Metal;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Backend.Metal;

internal sealed class MetalTexture : IGfxTexture, IDisposable
{
	private bool _isDisposed;
	private readonly MetalDescriptorTable _descriptorTable;

	public MetalTexture(string? name, TextureDescriptor descriptor, MTLTexture texture, MetalDescriptorTable descriptorTable)
	{
		Name = name;
		Descriptor = descriptor;
		Texture = texture;
		_descriptorTable = descriptorTable;
		ShaderResourceView = DescriptorHandle.Invalid;
		UnorderedAccessView = DescriptorHandle.Invalid;
	}

	public string? Name { get; }

	public TextureDescriptor Descriptor { get; }

	public DescriptorHandle ShaderResourceView { get; private set; }

	public DescriptorHandle UnorderedAccessView { get; private set; }

	public MTLTexture Texture { get; }

	public bool IsDisposed => _isDisposed;

	public void SetHandles(DescriptorHandle srvHandle, DescriptorHandle uavHandle)
	{
		ShaderResourceView = srvHandle;
		UnorderedAccessView = uavHandle;
	}

	public void Dispose()
	{
		if (_isDisposed)
		{
			return;
		}

		_isDisposed = true;
		if (ShaderResourceView.IsValid)
		{
			_descriptorTable.Free(ShaderResourceView);
			ShaderResourceView = DescriptorHandle.Invalid;
		}

		if (UnorderedAccessView.IsValid)
		{
			_descriptorTable.Free(UnorderedAccessView);
			UnorderedAccessView = DescriptorHandle.Invalid;
		}
		if (Texture.NativePtr != IntPtr.Zero)
		{
			Texture.Dispose();
		}
	}
}

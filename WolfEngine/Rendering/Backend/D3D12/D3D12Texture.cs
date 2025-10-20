using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Backend.D3D12;

public class D3D12Texture: IGfxTexture
{
	public string Name { get; }
	public TextureDescriptor Descriptor { get; }
}
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Backend.D3D12;

internal sealed class D3D12TextureResources : ITextureResources
{
	public required IGfxTexture Texture { get; init; }
	public required IGfxDescriptorSet DescriptorSet { get; init; }
}

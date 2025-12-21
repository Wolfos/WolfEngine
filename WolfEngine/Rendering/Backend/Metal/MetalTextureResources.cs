using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Backend.Metal;

internal sealed class MetalTextureResources : ITextureResources
{
	public required IGfxTexture Texture { get; init; }
	public required DescriptorHandle ShaderResourceView { get; init; }
}

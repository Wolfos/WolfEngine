namespace WolfEngine.Rendering.Abstraction;

public interface ITextureResources
{
    IGfxTexture Texture { get; }
    DescriptorHandle ShaderResourceView { get; }
}

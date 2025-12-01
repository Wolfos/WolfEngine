namespace WolfEngine.Rendering.Abstraction;

public interface ITextureResources
{
    IGfxTexture Texture { get; }
    IGfxDescriptorSet DescriptorSet { get; }
}

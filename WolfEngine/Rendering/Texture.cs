using WolfEngine.Rendering.Abstraction;
using WolfEngine.AssetPipeline;

namespace WolfEngine;

[RuntimeAsset(AssetType.Texture2D, typeof(TextureAsset), typeof(ITextureRuntimeAssetResolver))]
public sealed class Texture
{
    public Texture(string name, int width, int height, bool isSrgb, byte[] pixelData)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Width = width;
        Height = height;
        IsSrgb = isSrgb;
        PixelData = pixelData ?? throw new ArgumentNullException(nameof(pixelData));
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Texture dimensions must be positive.");
        }
    }

    public string Name { get; }
    public int Width { get; }
    public int Height { get; }
    public bool IsSrgb { get; }
    public byte[] PixelData { get; }

    internal ITextureResources Resources { get; set; } = null!;
}

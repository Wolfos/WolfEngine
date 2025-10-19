namespace WolfEngine.Rendering;

/// <summary>
/// Backend-specific resource factory used by the render graph to realise abstract textures.
/// </summary>
public interface IRenderGraphBackend
{
	IRenderGraphTexture CreateTexture(in TextureDescriptor descriptor);
}

/// <summary>
/// Represents a concrete GPU texture created by the render graph backend.
/// </summary>
public interface IRenderGraphTexture : IDisposable
{
	TextureDescriptor Descriptor { get; }
}

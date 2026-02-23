using SharpMetal.QuartzCore;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Backend.Metal;

internal sealed class MetalBackbufferTexture : IGfxTexture
{
	public MetalBackbufferTexture(CAMetalDrawable drawable, TextureDescriptor descriptor)
	{
		Drawable = drawable;
		Descriptor = descriptor;
	}

	public string Name => "MetalBackbuffer";

	public TextureDescriptor Descriptor { get; }

	public DescriptorHandle ShaderResourceView => DescriptorHandle.Invalid;

	public DescriptorHandle DepthShaderResourceView => DescriptorHandle.Invalid;

	public DescriptorHandle UnorderedAccessView => DescriptorHandle.Invalid;

	public CAMetalDrawable Drawable { get; }
}

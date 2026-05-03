using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

public readonly struct GBufferDecalSeedPassConfig
{
	public required IGfxPipeline Pipeline { get; init; }
	public required DescriptorHandle SourceAlbedoHandle { get; init; }
	public required DescriptorHandle SourceNormalHandle { get; init; }
	public required DescriptorHandle SourceMaterialHandle { get; init; }
	public required DescriptorHandle SourceEmissiveHandle { get; init; }
	public required DescriptorHandle TargetAlbedoHandle { get; init; }
	public required DescriptorHandle TargetNormalHandle { get; init; }
	public required DescriptorHandle TargetMaterialHandle { get; init; }
	public required DescriptorHandle TargetEmissiveHandle { get; init; }
	public required Int2 RenderSize { get; init; }
}

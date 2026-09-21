using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

public readonly struct CopyToFinalPassConfig
{
	public required IGfxPipeline Pipeline { get; init; }
	public required DescriptorHandle InputHandle { get; init; }
	public required DescriptorHandle OutputHandle { get; init; }
	public required DescriptorHandle EncodedSceneOutputHandle { get; init; }
	public required Int2 RenderSize { get; init; }

	/// <summary>Whether to write the shared final target as well as the view's own encoded image.</summary>
	public required bool WriteFinalOutput { get; init; }
}

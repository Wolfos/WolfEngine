using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Backend.D3D12;

internal class D3D12MaterialResources: IMaterialResources
{
	public required IGfxPipeline Pipeline { get; init; }
	public required IGfxBuffer? ConstantBuffer { get; init; }
}

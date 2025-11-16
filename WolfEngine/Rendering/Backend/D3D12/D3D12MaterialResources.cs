using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Backend.D3D12;

internal class D3D12MaterialResources: IMaterialResources
{
	public required IGfxPipeline Pipeline { get; init; }
	public required IGfxBuffer? ConstantBuffer { get; init; }

	// Internal D3D12-specific properties (for backwards compatibility during transition)
	internal ComPtr<ID3D12PipelineState> PipelineState { get; init; }
	internal ComPtr<ID3D12Resource> ColorBuffer { get; init; }
}
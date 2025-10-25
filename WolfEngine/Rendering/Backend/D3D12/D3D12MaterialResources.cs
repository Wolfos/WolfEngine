using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Backend.D3D12;

internal class D3D12MaterialResources: IMaterialResources
{
	public ComPtr<ID3D12PipelineState> PipelineState { get; set; }

	public ComPtr<ID3D12Resource> ColorBuffer { get; set;  }
}
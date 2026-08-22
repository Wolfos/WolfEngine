using Silk.NET.Direct3D12;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Backend.D3D12;

/// <summary>
/// Exposes Direct3D12-specific handles for textures created or imported by the backend.
/// </summary>
public unsafe interface ID3D12BackendTexture : IGfxTexture
{
	ID3D12Resource* Resource { get; }

	CpuDescriptorHandle? RenderTargetView { get; }

	CpuDescriptorHandle? DepthStencilView { get; }
}

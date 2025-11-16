#nullable enable

using WolfEngine.Rendering.Abstraction;
using WolfEngine.Mathematics;
using Silk.NET.Direct3D12;
using Silk.NET.Core.Native;

namespace WolfEngine.Rendering.Passes;

/// <summary>
/// Describes the API-agnostic parameters required to record the deferred lighting compute pass.
/// </summary>
public sealed class DeferredLightingPassConfig
{
	public required IGfxPipeline Pipeline { get; init; }

	public required IGfxDescriptorTable? DescriptorTable { get; init; }

	public required Int2 DispatchSize { get; init; }

	// TODO: These D3D12-specific properties should be abstracted away in the future
	public ComPtr<ID3D12DescriptorHeap>? D3D12DescriptorHeap { get; init; }
	public GpuDescriptorHandle? D3D12GpuDescriptorHandle { get; init; }
	public uint D3D12DescriptorSize { get; init; }
}


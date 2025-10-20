#nullable enable

using Silk.NET.Direct3D12;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Backend.D3D12;

/// <summary>
/// Exposes the native D3D12 command list for callers that still need backend-specific access.
/// </summary>
public unsafe interface ID3D12BackendCommandList : IGfxCommandList
{
    ID3D12GraphicsCommandList* NativeCommandList { get; }
}

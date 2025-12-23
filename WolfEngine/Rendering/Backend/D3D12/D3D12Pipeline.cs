using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Backend.D3D12;

internal sealed class D3D12Pipeline : IGfxPipeline
{
	public D3D12Pipeline(PipelineKey key, PassKind kind, ComPtr<ID3D12PipelineState> pipelineState,
		ComPtr<ID3D12RootSignature> rootSignature)
	{
		Key = key;
		Kind = kind;
		PipelineState = pipelineState;
		RootSignature = rootSignature;
	}

	public string Name => null;

	public PipelineKey Key { get; }

	public PassKind Kind { get; }

	public ComPtr<ID3D12PipelineState> PipelineState { get; }

	public ComPtr<ID3D12RootSignature> RootSignature { get; }
}

using SharpMetal.Metal;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Backend.Metal;

internal sealed class MetalPipeline : IGfxPipeline
{
	public MetalPipeline(PipelineKey key, PassKind kind, MTLRenderPipelineState renderState,
		MTLComputePipelineState computeState, MTLDepthStencilState depthState,
		MTLArgumentEncoder textureEncoder, MTLArgumentEncoder rwTextureEncoder, MTLArgumentEncoder samplerEncoder,
		RenderStateDescriptor renderStateDescriptor,
		ComputeThreadGroupSize? computeThreadGroupSize = null)
	{
		Key = key;
		Kind = kind;
		RenderPipelineState = renderState;
		ComputePipelineState = computeState;
		DepthStencilState = depthState;
		TextureEncoder = textureEncoder;
		RWTextureEncoder = rwTextureEncoder;
		SamplerEncoder = samplerEncoder;
		RenderState = renderStateDescriptor;
		ComputeThreadGroupSize = computeThreadGroupSize;
	}

	public string? Name => null;

	public PipelineKey Key { get; }

	public PassKind Kind { get; }

	public MTLRenderPipelineState RenderPipelineState { get; }

	public MTLComputePipelineState ComputePipelineState { get; }

	public MTLDepthStencilState DepthStencilState { get; }

	public MTLArgumentEncoder TextureEncoder { get; }

	public MTLArgumentEncoder RWTextureEncoder { get; }

	public MTLArgumentEncoder SamplerEncoder { get; }

	public RenderStateDescriptor RenderState { get; }

	public ComputeThreadGroupSize? ComputeThreadGroupSize { get; }
}

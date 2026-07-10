using System;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Rendering;

internal sealed class RenderGraphPassSet
{
	public RenderGraphPassSet(
		IRenderer renderer,
		IShaderCompiler shaderCompiler,
		BindlessResourceRegistry bindlessResourceRegistry,
		GpuDrawResources gpuDrawResources,
		GpuDrawHardeningStats gpuDrawHardeningStats,
		IGpuDrawBackendBridge gpuDrawBackendBridge)
	{
		ArgumentNullException.ThrowIfNull(renderer);
		ArgumentNullException.ThrowIfNull(shaderCompiler);
		ArgumentNullException.ThrowIfNull(bindlessResourceRegistry);
		ArgumentNullException.ThrowIfNull(gpuDrawResources);
		ArgumentNullException.ThrowIfNull(gpuDrawHardeningStats);
		ArgumentNullException.ThrowIfNull(gpuDrawBackendBridge);

		AmbientOcclusionPass = new(shaderCompiler, bindlessResourceRegistry);
		AmbientOcclusionBlurPass = new(shaderCompiler, bindlessResourceRegistry);
		AmbientOcclusionUpsamplePass = new(shaderCompiler, bindlessResourceRegistry);
		DdgiPass = new(shaderCompiler, bindlessResourceRegistry);
		ClusteredLightingPass = new(shaderCompiler);
		GBufferDecalSeedPass = new(shaderCompiler, bindlessResourceRegistry);
		ScreenSpaceDecalPass = new(renderer, shaderCompiler, bindlessResourceRegistry);
		DeferredLightingPass = new(shaderCompiler, bindlessResourceRegistry);
		TemporalAntiAliasingPass = new(shaderCompiler, bindlessResourceRegistry);
		TemporalHistoryStorePass = new(shaderCompiler, bindlessResourceRegistry);
		TransparentForwardPass = new(shaderCompiler, bindlessResourceRegistry);
		BloomPass = new(shaderCompiler, bindlessResourceRegistry);
		TonemappingPass = new(shaderCompiler, bindlessResourceRegistry);
		CasSharpenPass = new(shaderCompiler, bindlessResourceRegistry);
		CopyToFinalPass = new(shaderCompiler, bindlessResourceRegistry);
		ShadowMapPass = new(shaderCompiler);
		GpuDrawPass = new(
			shaderCompiler,
			bindlessResourceRegistry,
			gpuDrawResources,
			gpuDrawHardeningStats,
			renderer,
			gpuDrawBackendBridge);
		SkyboxPass = new(renderer, shaderCompiler, bindlessResourceRegistry);
	}

	public AmbientOcclusionPass AmbientOcclusionPass { get; }
	public AmbientOcclusionBlurPass AmbientOcclusionBlurPass { get; }
	public AmbientOcclusionUpsamplePass AmbientOcclusionUpsamplePass { get; }
	public DdgiPass DdgiPass { get; }
	public ClusteredLightingPass ClusteredLightingPass { get; }
	public GBufferDecalSeedPass GBufferDecalSeedPass { get; }
	public ScreenSpaceDecalPass ScreenSpaceDecalPass { get; }
	public DeferredLightingPass DeferredLightingPass { get; }
	public TemporalAntiAliasingPass TemporalAntiAliasingPass { get; }
	public TemporalHistoryStorePass TemporalHistoryStorePass { get; }
	public TransparentForwardPass TransparentForwardPass { get; }
	public BloomPass BloomPass { get; }
	public TonemappingPass TonemappingPass { get; }
	public CasSharpenPass CasSharpenPass { get; }
	public CopyToFinalPass CopyToFinalPass { get; }
	public ShadowMapPass ShadowMapPass { get; }
	public GpuDrawPass GpuDrawPass { get; }
	public SkyboxPass SkyboxPass { get; }
}

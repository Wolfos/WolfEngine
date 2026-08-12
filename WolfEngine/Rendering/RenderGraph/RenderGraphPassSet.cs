using System;
using WolfEngine.Rendering.Passes;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering;

internal sealed class RenderGraphPassSet
{
	public RenderGraphPassSet(
		IRenderer renderer,
		IShaderProvider shaderCompiler,
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
		ReflectionsPass = new(shaderCompiler, bindlessResourceRegistry);
		ReflectionsUpsamplePass = new(shaderCompiler, bindlessResourceRegistry);
		TemporalAntiAliasingPass = new(shaderCompiler, bindlessResourceRegistry);
		TemporalHistoryStorePass = new(shaderCompiler, bindlessResourceRegistry);
		Fsr3ClearPass = new(shaderCompiler, bindlessResourceRegistry);
		Fsr3PrepareInputsPass = new(shaderCompiler, bindlessResourceRegistry);
		Fsr3LumaPyramidPass = new(shaderCompiler, bindlessResourceRegistry);
		Fsr3ShadingChangePyramidPass = new(shaderCompiler, bindlessResourceRegistry);
		Fsr3ShadingChangePass = new(shaderCompiler, bindlessResourceRegistry);
		Fsr3PrepareReactivityPass = new(shaderCompiler, bindlessResourceRegistry);
		Fsr3LumaInstabilityPass = new(shaderCompiler, bindlessResourceRegistry);
		Fsr3AccumulatePass = new(shaderCompiler, bindlessResourceRegistry);
		Fsr3RcasPass = new(shaderCompiler, bindlessResourceRegistry);
		Fsr3DebugViewPass = new(shaderCompiler, bindlessResourceRegistry);
		TransparentForwardPass = new(shaderCompiler, bindlessResourceRegistry);
		BloomPass = new(shaderCompiler, bindlessResourceRegistry);
		ColorPyramidPass = new(shaderCompiler, bindlessResourceRegistry);
		TonemappingPass = new(shaderCompiler, bindlessResourceRegistry);
		CasSharpenPass = new(shaderCompiler, bindlessResourceRegistry);
		CopyToFinalPass = new(shaderCompiler, bindlessResourceRegistry);
		MotionVectorDebugPass = new(shaderCompiler, bindlessResourceRegistry);
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
	public ReflectionsPass ReflectionsPass { get; }
	public ReflectionsUpsamplePass ReflectionsUpsamplePass { get; }
	public TemporalAntiAliasingPass TemporalAntiAliasingPass { get; }
	public TemporalHistoryStorePass TemporalHistoryStorePass { get; }
	public Fsr3ClearPass Fsr3ClearPass { get; }
	public Fsr3PrepareInputsPass Fsr3PrepareInputsPass { get; }
	public Fsr3LumaPyramidPass Fsr3LumaPyramidPass { get; }
	public Fsr3ShadingChangePyramidPass Fsr3ShadingChangePyramidPass { get; }
	public Fsr3ShadingChangePass Fsr3ShadingChangePass { get; }
	public Fsr3PrepareReactivityPass Fsr3PrepareReactivityPass { get; }
	public Fsr3LumaInstabilityPass Fsr3LumaInstabilityPass { get; }
	public Fsr3AccumulatePass Fsr3AccumulatePass { get; }
	public Fsr3RcasPass Fsr3RcasPass { get; }
	public Fsr3DebugViewPass Fsr3DebugViewPass { get; }
	public TransparentForwardPass TransparentForwardPass { get; }
	public BloomPass BloomPass { get; }
	public ColorPyramidPass ColorPyramidPass { get; }
	public TonemappingPass TonemappingPass { get; }
	public CasSharpenPass CasSharpenPass { get; }
	public CopyToFinalPass CopyToFinalPass { get; }
	public MotionVectorDebugPass MotionVectorDebugPass { get; }
	public ShadowMapPass ShadowMapPass { get; }
	public GpuDrawPass GpuDrawPass { get; }
	public SkyboxPass SkyboxPass { get; }

	public void InvalidateShaderPipelines()
	{
		foreach (var property in GetType().GetProperties())
		{
			if (property.GetValue(this) is { } pass) ShaderPipelineInvalidation.Invalidate(pass);
		}
	}
}

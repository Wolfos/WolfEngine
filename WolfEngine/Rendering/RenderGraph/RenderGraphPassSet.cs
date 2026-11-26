using WolfEngine.Rendering.Clouds;
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
		IDrawRecordEncoder drawRecordEncoder,
		IGpuDrawDiagnosticsSampler gpuDrawDiagnosticsSampler)
	{
		ArgumentNullException.ThrowIfNull(renderer);
		ArgumentNullException.ThrowIfNull(shaderCompiler);
		ArgumentNullException.ThrowIfNull(bindlessResourceRegistry);
		ArgumentNullException.ThrowIfNull(gpuDrawResources);
		ArgumentNullException.ThrowIfNull(gpuDrawHardeningStats);
		ArgumentNullException.ThrowIfNull(drawRecordEncoder);
		ArgumentNullException.ThrowIfNull(gpuDrawDiagnosticsSampler);

		RayTracingScene = new RayTracingScene(shaderCompiler);
		var batches = new GpuDrawBatches(renderer.EnsureMeshResources);
		var stalenessInputs = new GpuDrawStalenessInputSource(gpuDrawResources, renderer);
		var drawRecords = new IndirectDrawRecords(drawRecordEncoder, batches, stalenessInputs);

		// Built first: lighting, fog and the final gather read its shadow data when their nodes execute.
		ShadowMapPass = new(shaderCompiler, drawRecords, gpuDrawResources);
		AmbientOcclusionPass = new(shaderCompiler, bindlessResourceRegistry, renderer, gpuDrawResources, RayTracingScene);
		AmbientOcclusionBlurPass = new(shaderCompiler, bindlessResourceRegistry);
		AmbientOcclusionUpsamplePass = new(shaderCompiler, bindlessResourceRegistry);
		DdgiPass = new(shaderCompiler, bindlessResourceRegistry, renderer, gpuDrawResources, RayTracingScene);
		ClusteredLightingPass = new(shaderCompiler, gpuDrawResources);
		GBufferDecalSeedPass = new(shaderCompiler, bindlessResourceRegistry);
		ScreenSpaceDecalPass = new(renderer, shaderCompiler, bindlessResourceRegistry, gpuDrawResources);
		SelectionOutlinePass = new(shaderCompiler, bindlessResourceRegistry);
		DeferredLightingPass = new(shaderCompiler, bindlessResourceRegistry, gpuDrawResources, ShadowMapPass);
		VolumetricFogPass = new(shaderCompiler, bindlessResourceRegistry, gpuDrawResources, ShadowMapPass);
		CloudPass = new(renderer, shaderCompiler, bindlessResourceRegistry);
		ReflectionsPass = new(shaderCompiler, bindlessResourceRegistry, renderer, gpuDrawResources, RayTracingScene);
		ReflectionsUpsamplePass = new(shaderCompiler, bindlessResourceRegistry);
		DdgiFinalGatherPass = new(
			shaderCompiler,
			bindlessResourceRegistry,
			renderer,
			gpuDrawResources,
			RayTracingScene,
			ShadowMapPass);
		DdgiFinalGatherSvgfPass = new(shaderCompiler, bindlessResourceRegistry);
		TemporalAntiAliasingPass = new(shaderCompiler, bindlessResourceRegistry);
		TemporalHistoryStorePass = new(shaderCompiler, bindlessResourceRegistry);
		Fsr3Pass = new(shaderCompiler, bindlessResourceRegistry);
		TransparentForwardPass = new(shaderCompiler, bindlessResourceRegistry, drawRecords, gpuDrawResources);
		BloomPass = new(shaderCompiler, bindlessResourceRegistry);
		ColorPyramidPass = new(shaderCompiler, bindlessResourceRegistry);
		TonemappingPass = new(shaderCompiler, bindlessResourceRegistry);
		CasSharpenPass = new(shaderCompiler, bindlessResourceRegistry);
		CopyToFinalPass = new(shaderCompiler, bindlessResourceRegistry);
		MotionVectorDebugPass = new(shaderCompiler, bindlessResourceRegistry);
		GpuDrawPass = new(
			shaderCompiler,
			bindlessResourceRegistry,
			gpuDrawResources,
			gpuDrawHardeningStats,
			renderer,
			drawRecords,
			batches,
			stalenessInputs,
			gpuDrawDiagnosticsSampler);
		SkyboxPass = new(renderer, shaderCompiler, bindlessResourceRegistry);
		VfxPass = new(shaderCompiler, bindlessResourceRegistry);
		BindlessResourceRegistry = bindlessResourceRegistry;
	}

	public BindlessResourceRegistry BindlessResourceRegistry { get; }

	/// <summary>The scene's acceleration structures, which the ray-traced passes read when their nodes execute.</summary>
	public RayTracingScene RayTracingScene { get; }

	public AmbientOcclusionPass AmbientOcclusionPass { get; }
	public AmbientOcclusionBlurPass AmbientOcclusionBlurPass { get; }
	public AmbientOcclusionUpsamplePass AmbientOcclusionUpsamplePass { get; }
	public DdgiPass DdgiPass { get; }
	public ClusteredLightingPass ClusteredLightingPass { get; }
	public GBufferDecalSeedPass GBufferDecalSeedPass { get; }
	public ScreenSpaceDecalPass ScreenSpaceDecalPass { get; }

	public SelectionOutlinePass SelectionOutlinePass { get; }
	public DeferredLightingPass DeferredLightingPass { get; }
	public VolumetricFogPass VolumetricFogPass { get; }
	public CloudPass CloudPass { get; }
	public ReflectionsPass ReflectionsPass { get; }
	public ReflectionsUpsamplePass ReflectionsUpsamplePass { get; }
	public DdgiFinalGatherPass DdgiFinalGatherPass { get; }
	public DdgiFinalGatherSvgfPass DdgiFinalGatherSvgfPass { get; }
	public TemporalAntiAliasingPass TemporalAntiAliasingPass { get; }
	public TemporalHistoryStorePass TemporalHistoryStorePass { get; }
	public Fsr3Pass Fsr3Pass { get; }
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
	public VfxPass VfxPass { get; }

	public void InvalidateShaderPipelines()
	{
		foreach (var property in GetType().GetProperties())
		{
			if (property.GetValue(this) is { } pass) ShaderPipelineInvalidation.Invalidate(pass);
		}

		// Invalidation only clears a pass's own pipeline fields, and FSR3's kernels are its own.
		Fsr3Pass.InvalidateShaderPipelines();
	}
}

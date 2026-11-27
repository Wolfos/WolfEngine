using WolfEngine.Rendering.Clouds;
using WolfEngine.Rendering.Passes;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering;

internal sealed class RenderGraphPassSet
{
	public RenderGraphPassSet(
		IRenderer renderer,
		ShaderSource shaderSource,
		BindlessResourceRegistry bindlessResourceRegistry,
		GpuDrawResources gpuDrawResources,
		GpuDrawHardeningStats gpuDrawHardeningStats,
		IDrawRecordEncoder drawRecordEncoder,
		IGpuDrawDiagnosticsSampler gpuDrawDiagnosticsSampler)
	{
		ArgumentNullException.ThrowIfNull(renderer);
		ArgumentNullException.ThrowIfNull(shaderSource);
		ArgumentNullException.ThrowIfNull(bindlessResourceRegistry);
		ArgumentNullException.ThrowIfNull(gpuDrawResources);
		ArgumentNullException.ThrowIfNull(gpuDrawHardeningStats);
		ArgumentNullException.ThrowIfNull(drawRecordEncoder);
		ArgumentNullException.ThrowIfNull(gpuDrawDiagnosticsSampler);

		RayTracingScene = new RayTracingScene(shaderSource);
		var batches = new GpuDrawBatches(renderer.EnsureMeshResources);
		var stalenessInputs = new GpuDrawStalenessInputSource(gpuDrawResources, renderer);
		var drawRecords = new IndirectDrawRecords(drawRecordEncoder, batches, stalenessInputs);

		// Built first: lighting, fog and the final gather read its shadow data when their nodes execute.
		ShadowMapPass = new(shaderSource, drawRecords, gpuDrawResources);
		AmbientOcclusionPass = new(shaderSource, bindlessResourceRegistry, renderer, gpuDrawResources, RayTracingScene);
		AmbientOcclusionBlurPass = new(shaderSource, bindlessResourceRegistry);
		AmbientOcclusionUpsamplePass = new(shaderSource, bindlessResourceRegistry);
		DdgiPass = new(shaderSource, bindlessResourceRegistry, renderer, gpuDrawResources, RayTracingScene);
		ClusteredLightingPass = new(shaderSource, gpuDrawResources);
		GBufferDecalSeedPass = new(shaderSource, bindlessResourceRegistry);
		ScreenSpaceDecalPass = new(renderer, shaderSource, bindlessResourceRegistry, gpuDrawResources);
		SelectionOutlinePass = new(shaderSource, bindlessResourceRegistry);
		DeferredLightingPass = new(shaderSource, bindlessResourceRegistry, gpuDrawResources, ShadowMapPass);
		VolumetricFogPass = new(shaderSource, bindlessResourceRegistry, gpuDrawResources, ShadowMapPass);
		CloudPass = new(renderer, shaderSource, bindlessResourceRegistry);
		ReflectionsPass = new(shaderSource, bindlessResourceRegistry, renderer, gpuDrawResources, RayTracingScene);
		ReflectionsUpsamplePass = new(shaderSource, bindlessResourceRegistry);
		DdgiFinalGatherPass = new(
			shaderSource,
			bindlessResourceRegistry,
			renderer,
			gpuDrawResources,
			RayTracingScene,
			ShadowMapPass);
		DdgiFinalGatherSvgfPass = new(shaderSource, bindlessResourceRegistry);
		TemporalAntiAliasingPass = new(shaderSource, bindlessResourceRegistry);
		TemporalHistoryStorePass = new(shaderSource, bindlessResourceRegistry);
		Fsr3Pass = new(shaderSource, bindlessResourceRegistry);
		TransparentForwardPass = new(shaderSource, bindlessResourceRegistry, drawRecords, gpuDrawResources);
		BloomPass = new(shaderSource, bindlessResourceRegistry);
		ColorPyramidPass = new(shaderSource, bindlessResourceRegistry);
		TonemappingPass = new(shaderSource, bindlessResourceRegistry);
		CasSharpenPass = new(shaderSource, bindlessResourceRegistry);
		CopyToFinalPass = new(shaderSource, bindlessResourceRegistry);
		MotionVectorDebugPass = new(shaderSource, bindlessResourceRegistry);
		GpuDrawPass = new(
			shaderSource,
			bindlessResourceRegistry,
			gpuDrawResources,
			gpuDrawHardeningStats,
			renderer,
			drawRecords,
			batches,
			stalenessInputs,
			gpuDrawDiagnosticsSampler);
		SkyboxPass = new(renderer, shaderSource, bindlessResourceRegistry);
		VfxPass = new(shaderSource, bindlessResourceRegistry);
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
}

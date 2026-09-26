using System.Runtime.InteropServices;
﻿using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;
using WolfEngine.Rendering.UI;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering;

public readonly struct Fsr3FrameResources
{
	public RenderGraphResourceHandle TransparencyMask { get; init; }
	public RenderGraphResourceHandle DilatedMotionVectors { get; init; }
	public RenderGraphResourceHandle DilatedDepth { get; init; }
	public RenderGraphResourceHandle FarthestDepth { get; init; }
	public RenderGraphResourceHandle ReconstructedPrevNearestDepth { get; init; }
	public RenderGraphResourceHandle CurrentLumaRead { get; init; }
	public RenderGraphResourceHandle CurrentLumaWrite { get; init; }
	public RenderGraphResourceHandle FarthestDepthMip1 { get; init; }
	public RenderGraphResourceHandle FrameInfo { get; init; }
	public RenderGraphResourceHandle LumaSpdAtomic { get; init; }
	public RenderGraphResourceHandle[] LumaSpdMips { get; init; }
	public RenderGraphResourceHandle ShadingSpdAtomic { get; init; }
	public RenderGraphResourceHandle[] ShadingSpdMips { get; init; }
	public RenderGraphResourceHandle ShadingChange { get; init; }
	public RenderGraphResourceHandle AccumulationRead { get; init; }
	public RenderGraphResourceHandle AccumulationWrite { get; init; }
	public RenderGraphResourceHandle DilatedReactiveMasks { get; init; }
	public RenderGraphResourceHandle NewLocks { get; init; }
	public RenderGraphResourceHandle LumaHistoryRead { get; init; }
	public RenderGraphResourceHandle LumaHistoryWrite { get; init; }
	public RenderGraphResourceHandle LumaInstability { get; init; }
	public RenderGraphResourceHandle InternalHistoryRead { get; init; }
	public RenderGraphResourceHandle InternalHistoryWrite { get; init; }
	public bool HistoryValid { get; init; }
}

/// <summary>Transient and imported resources owned by one rendered view for the current frame.</summary>
public readonly struct RenderViewResources
{
	public Int2 FramebufferSize { get; init; }
	public Int2 SceneFramebufferSize { get; init; }
	public bool SceneEnabled { get; init; }
	public RenderGraphResourceHandle TonemappedLinearSceneColor { get; init; }
	public RenderGraphResourceHandle DisplayLinearSceneColor { get; init; }
	public RenderGraphResourceHandle EncodedSceneColor { get; init; }
	public RenderGraphResourceHandle GBufferAlbedo { get; init; }
	public RenderGraphResourceHandle GBufferNormal { get; init; }
	public RenderGraphResourceHandle GBufferMaterial { get; init; }
	public RenderGraphResourceHandle GBufferEmissive { get; init; }
	public RenderGraphResourceHandle DecalSourceGBufferAlbedo { get; init; }
	public RenderGraphResourceHandle DecalSourceGBufferNormal { get; init; }
	public RenderGraphResourceHandle DecalSourceGBufferMaterial { get; init; }
	public RenderGraphResourceHandle DecalSourceGBufferEmissive { get; init; }
	public RenderGraphResourceHandle GBufferDepth { get; init; }
	public RenderGraphResourceHandle GBufferVelocity { get; init; }
	public RenderGraphResourceHandle MotionVectorDebugColor { get; init; }
	public RenderGraphResourceHandle AmbientOcclusionRaw { get; init; }
	public RenderGraphResourceHandle AmbientOcclusionTemp { get; init; }
	public RenderGraphResourceHandle AmbientOcclusionFinal { get; init; }
	public RenderGraphResourceHandle RayTracingHitMask { get; init; }
	public RenderGraphResourceHandle RayTracingHitDistance { get; init; }
	public RenderGraphResourceHandle RayTracingAlbedo { get; init; }
	public RenderGraphResourceHandle DdgiTraceIrradiance { get; init; }
	public RenderGraphResourceHandle DdgiTraceVisibility { get; init; }
	public RenderGraphResourceHandle DdgiIrradianceEstimator { get; init; }
	public RenderGraphResourceHandle DdgiIrradianceL0HistoryRead { get; init; }
	public RenderGraphResourceHandle DdgiIrradianceL0HistoryWrite { get; init; }
	public RenderGraphResourceHandle DdgiIrradianceLyHistoryRead { get; init; }
	public RenderGraphResourceHandle DdgiIrradianceLyHistoryWrite { get; init; }
	public RenderGraphResourceHandle DdgiIrradianceLzHistoryRead { get; init; }
	public RenderGraphResourceHandle DdgiIrradianceLzHistoryWrite { get; init; }
	public RenderGraphResourceHandle DdgiIrradianceLxHistoryRead { get; init; }
	public RenderGraphResourceHandle DdgiIrradianceLxHistoryWrite { get; init; }
	public RenderGraphResourceHandle DdgiVisibilityHistoryRead { get; init; }
	public RenderGraphResourceHandle DdgiVisibilityHistoryWrite { get; init; }
	public RenderGraphResourceHandle DdgiProbeStateRead { get; init; }
	public RenderGraphResourceHandle DdgiProbeStateWrite { get; init; }
	public RenderGraphResourceHandle DdgiProbeActivity { get; init; }
	public RenderGraphResourceHandle DdgiProbeActivityMarks { get; init; }
	public Vector3 DdgiRuntimeOrigin { get; init; }
	public Int3 DdgiStorageOffset { get; init; }
	public Int3 DdgiScrollDelta { get; init; }
	public RenderGraphResourceHandle DdgiFinalContribution { get; init; }
	public RenderGraphResourceHandle DdgiProbeBaseWeightDebug { get; init; }
	public RenderGraphResourceHandle DdgiWeightedVisibilityDebug { get; init; }
	public RenderGraphResourceHandle DdgiDominantProbeDebug { get; init; }
	public RenderGraphResourceHandle DdgiDominantProbeCoordDebug { get; init; }
	public RenderGraphResourceHandle DdgiProbeRelocationDebug { get; init; }
	public RenderGraphResourceHandle DdgiProbeRelocationDecision { get; init; }
	public RenderGraphResourceHandle DdgiProbeRelocationDecisionDebug { get; init; }
	public bool WriteDdgiFinalContributionDebug { get; init; }
	public bool WriteDdgiProbeDebug { get; init; }
	public RenderGraphResourceHandle ShadowMapDepth0 { get; init; }
	public RenderGraphResourceHandle ShadowMapDepth1 { get; init; }
	public RenderGraphResourceHandle ShadowMapDepth2 { get; init; }
	public RenderGraphResourceHandle LightingBuffer { get; init; }
	public RenderGraphResourceHandle FogCurrent { get; init; }
	public RenderGraphResourceHandle FogHistoryRead { get; init; }
	public RenderGraphResourceHandle FogHistoryWrite { get; init; }
	public RenderGraphResourceHandle FogIntegrated { get; init; }
	public bool FogHistoryValid { get; init; }
	public RenderGraphResourceHandle ReflectionsTrace { get; init; }
	public RenderGraphResourceHandle ReflectionsRadiance { get; init; }
	public RenderGraphResourceHandle ResolvedSceneColor { get; init; }
	/// <summary>
	/// Mip chain of the resolved scene color, built at the end of the frame and read by the
	/// next frame's reflection tracing. Level 0 is full scene resolution.
	/// </summary>
	public RenderGraphResourceHandle[] ColorPyramidLevels { get; init; }
	public bool ColorPyramidHistoryValid { get; init; }
	public RenderGraphResourceHandle[] BloomDownsampleLevels { get; init; }
	public RenderGraphResourceHandle[] BloomUpsampleLevels { get; init; }
	public RenderGraphResourceHandle BloomCompositeSceneColor { get; init; }
	public RenderGraphResourceHandle HistoryColorRead { get; init; }
	public RenderGraphResourceHandle HistoryColorWrite { get; init; }
	public RenderGraphResourceHandle HistoryDepthRead { get; init; }
	public RenderGraphResourceHandle HistoryDepthWrite { get; init; }
	public Fsr3FrameResources Fsr3 { get; init; }
	public RenderConfig Config { get; init; }
}

/// <summary>
/// Resources recorded once for the whole rendered frame, regardless of how many views it contains.
/// </summary>
public readonly struct RenderFrameSharedResources
{
	public Int2 FramebufferSize { get; init; }
	public RenderGraphResourceHandle FinalColor { get; init; }
	public RenderGraphResourceHandle SkyboxEnvironment { get; init; }
	public RenderGraphResourceHandle SkyboxIrradiance { get; init; }
	public RenderGraphResourceHandle SkyboxPrefilter { get; init; }
	public RenderGraphResourceHandle SkyboxBrdfLut { get; init; }
}

internal sealed class RenderGraphFrameBuilder
{
	private readonly RenderGraphPassSet _passSet;
	internal readonly struct SceneDebugViewRegistration
	{
		public SceneDebugViewRegistration(
			string id,
			string label,
			RenderGraphResourceHandle handle,
			SceneDebugViewKind kind)
		{
			Id = id;
			Label = label;
			Handle = handle;
			Kind = kind;
		}

		public string Id { get; }
		public string Label { get; }
		public RenderGraphResourceHandle Handle { get; }
		public SceneDebugViewKind Kind { get; }
	}

	private readonly RenderGraphResourceRegistry _resources;
	private readonly IRenderer _renderer;
	private readonly AmbientOcclusionPass _ambientOcclusionPass;
	private readonly AmbientOcclusionBlurPass _ambientOcclusionBlurPass;
	private readonly AmbientOcclusionUpsamplePass _ambientOcclusionUpsamplePass;
	private readonly DdgiPass _ddgiPass;
	private readonly ClusteredLightingPass _clusteredLightingPass;
	private readonly GBufferDecalSeedPass _gBufferDecalSeedPass;
	private readonly ScreenSpaceDecalPass _screenSpaceDecalPass;
	private readonly SelectionOutlinePass _selectionOutlinePass;
	private readonly DeferredLightingPass _deferredLightingPass;
	private readonly VolumetricFogPass _volumetricFogPass;
	private readonly ReflectionsPass _reflectionsPass;
	private readonly ReflectionsUpsamplePass _reflectionsUpsamplePass;
	private readonly TemporalAntiAliasingPass _temporalAntiAliasingPass;
	private readonly TemporalHistoryStorePass _temporalHistoryStorePass;
	private readonly TransparentForwardPass _transparentForwardPass;
	private readonly BloomPass _bloomPass;
	private readonly ColorPyramidPass _colorPyramidPass;
	private readonly TonemappingPass _tonemappingPass;
	private readonly CasSharpenPass _casSharpenPass;
	private readonly CopyToFinalPass _copyToFinalPass;
	private readonly MotionVectorDebugPass _motionVectorDebugPass;
	private readonly ShadowMapPass _shadowMapPass;
	private readonly GpuDrawPass _gpuDrawPass;
	private readonly GpuDrawResources _gpuDrawResources;
	private readonly RayTracingSceneResources _rayTracingSceneResources;
	private readonly SkinningPass _skinningPass;
	private readonly SkyboxPass _skyboxPass;
	private readonly IImGuiRenderer _imGuiRenderer;
	private readonly GameplayUiGpuRenderer _gameplayUiRenderer;
	private SkyboxResources? _externalSkybox;
	private RenderFrameSharedResources _sharedResources;
	private UiFrameData _uiFrame = UiFrameData.Empty;
	private GameplayUiRenderFrame _gameplayUiFrame = GameplayUiRenderFrame.Empty;
	private readonly List<GameplayTextureTarget> _gameplayTextureTargets = [];

	private readonly record struct GameplayTextureTarget(
		GameplayUiTextureSurfaceFrame Surface,
		RenderGraphResourceHandle Handle,
		IGfxTexture Texture);
	// Output texture id per view index, rebuilt each frame and consumed when the UI frame's viewport
	// sentinels are rewritten. Zero means the view produced nothing this frame.
	private readonly nint[] _viewportTextureIds = new nint[UiTextureIds.MaxViewports];
	// Views set up this frame with their scene enabled, in setup order. Their databases feed the shared draw
	// update, which runs once before any view's passes.
	private readonly List<RenderViewId> _frameViews = [];
	private readonly List<GpuDrawSource> _frameDrawSources = [];
	private const int DdgiShCoefficientCount = DdgiUtilities.ShCoefficientCount;

	// Per-view state that spans frames: history, fog, pyramid and DDGI. Shared with the render graph, which
	// needs the same views' output targets and render sizes.
	private readonly RenderViewRegistry _viewRegistry;
	// The view currently being recorded. Set by BeginFrame; while one view exists it is always the primary.
	private RenderViewState _view;
	
	private readonly Action<RenderGraphContext> _gbufferExecute;
	private readonly Action<RenderGraphContext> _ambientOcclusionExecute;
	private readonly Action<RenderGraphContext> _ambientOcclusionBlurHorizontalExecute;
	private readonly Action<RenderGraphContext> _ambientOcclusionBlurVerticalExecute;
	private readonly Action<RenderGraphContext> _ambientOcclusionUpsampleExecute;
	private readonly Action<RenderGraphContext> _ddgiClassifyExecute;
	private readonly Action<RenderGraphContext> _ddgiTraceExecute;
	private readonly Action<RenderGraphContext> _ddgiIrradianceIntegrateExecute;
	private readonly Action<RenderGraphContext> _ddgiVisibilityIntegrateExecute;
	private readonly Action<RenderGraphContext> _clusteredLightingBuildExecute;
	private readonly Action<RenderGraphContext> _clusteredLightingWriteExecute;
	private readonly Action<RenderGraphContext> _gBufferDecalSeedExecute;
	private readonly Action<RenderGraphContext> _screenSpaceDecalExecute;
	private readonly Action<RenderGraphContext> _selectionOutlineExecute;
	private readonly Action<RenderGraphContext> _deferredLightingExecute;
	private readonly Action<RenderGraphContext> _volumetricFogInjectExecute;
	private readonly Action<RenderGraphContext> _volumetricFogTemporalExecute;
	private readonly Action<RenderGraphContext> _volumetricFogIntegrateExecute;
	private readonly Action<RenderGraphContext> _reflectionsExecute;
	private readonly Action<RenderGraphContext> _reflectionsUpsampleExecute;
	private readonly Action<RenderGraphContext> _taaResolveExecute;
	private readonly Action<RenderGraphContext> _taaHistoryStoreExecute;
	private readonly Action<RenderGraphContext> _transparentForwardExecute;
	private readonly Action<RenderGraphContext> _bloomCompositeExecute;
	private readonly Action<RenderGraphContext> _tonemappingExecute;
	private readonly Action<RenderGraphContext> _casSharpenExecute;
	private readonly Action<RenderGraphContext> _copyToFinalExecute;
	private readonly Action<RenderGraphContext> _motionVectorDebugExecute;
	private readonly Action<RenderGraphContext> _gameplayScreenEncodedUiExecute;
	private readonly Action<RenderGraphContext> _gameplayScreenFinalUiExecute;
	private readonly Action<RenderGraphContext> _imguiExecute;
	private readonly Action<RenderGraphContext> _gpuDrawUpdateExecute;
	private readonly Action<RenderGraphContext> _gpuDrawViewUpdateExecute;
	private readonly Action<RenderGraphContext> _gpuDrawShadowCullExecute;
	private readonly Action<RenderGraphContext> _shadowMapExecute;
	private readonly Action<RenderGraphContext> _gpuDrawCameraCullExecute;
	private readonly Action<RenderGraphContext> _skyboxEnvironmentExecute;
	private readonly Action<RenderGraphContext> _skyboxIrradianceExecute;
	private readonly Action<RenderGraphContext> _skyboxPrefilterExecute;
	private readonly Action<RenderGraphContext> _skyboxBrdfExecute;
	private bool _useProceduralSkybox;
	private bool _recordProceduralSkyLighting;
	private bool _recordProceduralSkyBrdf;
	private ResourceState _proceduralSkyboxInitialState = ResourceState.ShaderResource;

	
	public RenderGraphFrameBuilder(
		RenderGraphResourceRegistry resources,
		IRenderer renderer,
		RenderGraphPassSet passSet,
		GpuDrawResources gpuDrawResources,
		IImGuiRenderer imGuiRenderer,
		GameplayUiGpuRenderer gameplayUiRenderer,
		IShaderProvider shaderProvider,
		RenderViewRegistry viewRegistry)
	{
		_viewRegistry = viewRegistry ?? throw new ArgumentNullException(nameof(viewRegistry));
		_passSet = passSet;
		_rayTracingSceneResources = new RayTracingSceneResources(shaderProvider);
		_skinningPass = new SkinningPass(shaderProvider);
		_resources = resources;
		_renderer = renderer;
		_ambientOcclusionPass = passSet.AmbientOcclusionPass;
		_ambientOcclusionBlurPass = passSet.AmbientOcclusionBlurPass;
		_ambientOcclusionUpsamplePass = passSet.AmbientOcclusionUpsamplePass;
		_ddgiPass = passSet.DdgiPass;
		_clusteredLightingPass = passSet.ClusteredLightingPass;
		_gBufferDecalSeedPass = passSet.GBufferDecalSeedPass;
		_screenSpaceDecalPass = passSet.ScreenSpaceDecalPass;
		_selectionOutlinePass = passSet.SelectionOutlinePass;
		_deferredLightingPass = passSet.DeferredLightingPass;
		_volumetricFogPass = passSet.VolumetricFogPass;
		_reflectionsPass = passSet.ReflectionsPass;
		_reflectionsUpsamplePass = passSet.ReflectionsUpsamplePass;
		_temporalAntiAliasingPass = passSet.TemporalAntiAliasingPass;
		_temporalHistoryStorePass = passSet.TemporalHistoryStorePass;
		_transparentForwardPass = passSet.TransparentForwardPass;
		_bloomPass = passSet.BloomPass;
		_colorPyramidPass = passSet.ColorPyramidPass;
		_tonemappingPass = passSet.TonemappingPass;
		_casSharpenPass = passSet.CasSharpenPass;
		_copyToFinalPass = passSet.CopyToFinalPass;
		_motionVectorDebugPass = passSet.MotionVectorDebugPass;
		_shadowMapPass = passSet.ShadowMapPass;
		_gpuDrawPass = passSet.GpuDrawPass;
		_gpuDrawResources = gpuDrawResources;
		_skyboxPass = passSet.SkyboxPass;
		_imGuiRenderer = imGuiRenderer;
		_gameplayUiRenderer = gameplayUiRenderer;
		_view = GetOrCreateViewState(RenderViewId.Primary);

		_gbufferExecute = ExecuteGBuffer;
		_ambientOcclusionExecute = ExecuteAmbientOcclusion;
		_ambientOcclusionBlurHorizontalExecute = ExecuteAmbientOcclusionBlurHorizontal;
		_ambientOcclusionBlurVerticalExecute = ExecuteAmbientOcclusionBlurVertical;
		_ambientOcclusionUpsampleExecute = ExecuteAmbientOcclusionUpsample;
		_ddgiClassifyExecute = ExecuteDdgiClassify;
		_ddgiTraceExecute = ExecuteDdgiTrace;
		_ddgiIrradianceIntegrateExecute = ExecuteDdgiIrradianceIntegrate;
		_ddgiVisibilityIntegrateExecute = ExecuteDdgiVisibilityIntegrate;
		_clusteredLightingBuildExecute = ExecuteClusteredLightingBuild;
		_clusteredLightingWriteExecute = ExecuteClusteredLightingWrite;
		_gBufferDecalSeedExecute = ExecuteGBufferDecalSeed;
		_screenSpaceDecalExecute = ExecuteScreenSpaceDecal;
		_selectionOutlineExecute = ExecuteSelectionOutline;
		_deferredLightingExecute = ExecuteDeferredLighting;
		_volumetricFogInjectExecute = context => ExecuteVolumetricFog(context, VolumetricFogStage.Inject);
		_volumetricFogTemporalExecute = context => ExecuteVolumetricFog(context, VolumetricFogStage.Temporal);
		_volumetricFogIntegrateExecute = context => ExecuteVolumetricFog(context, VolumetricFogStage.Integrate);
		_reflectionsExecute = ExecuteReflections;
		_reflectionsUpsampleExecute = ExecuteReflectionsUpsample;
		_taaResolveExecute = ExecuteTemporalResolve;
		_taaHistoryStoreExecute = ExecuteTemporalHistoryStore;
		_transparentForwardExecute = ExecuteTransparentForward;
		_bloomCompositeExecute = ExecuteBloomComposite;
		_tonemappingExecute = ExecuteTonemapping;
		_casSharpenExecute = ExecuteCasSharpen;
		_copyToFinalExecute = ExecuteCopyToFinal;
		_motionVectorDebugExecute = ExecuteMotionVectorDebug;
		_gameplayScreenEncodedUiExecute = ExecuteGameplayScreenEncodedUi;
		_gameplayScreenFinalUiExecute = ExecuteGameplayScreenFinalUi;
		_imguiExecute = ExecuteImGui;
		_gpuDrawUpdateExecute = ExecuteGpuDrawUpdate;
		_gpuDrawViewUpdateExecute = ExecuteGpuDrawViewUpdate;
		_gpuDrawShadowCullExecute = ExecuteGpuDrawCullShadow;
		_shadowMapExecute = ExecuteShadowMap;
		_gpuDrawCameraCullExecute = ExecuteGpuDrawCullCamera;
		_skyboxEnvironmentExecute = ExecuteSkyboxEnvironment;
		_skyboxIrradianceExecute = ExecuteSkyboxIrradiance;
		_skyboxPrefilterExecute = ExecuteSkyboxPrefilter;
		_skyboxBrdfExecute = ExecuteSkyboxBrdf;
	}

	public void InvalidateShaderPipelines()
	{
		_passSet.InvalidateShaderPipelines();
		ShaderPipelineInvalidation.Invalidate(_rayTracingSceneResources);
		_skinningPass.InvalidateShaders();
	}

	public RayTracingSceneState GetRayTracingSceneState() => _rayTracingSceneResources.GetState();

	public void SetSkybox(SkyboxResources skybox)
	{
		_externalSkybox = skybox;
	}

	/// <summary>
	/// Single-view convenience: the shared frame setup followed by the primary view's.
	/// </summary>
	public void BeginFrame(
		Int2 framebufferSize,
		Int2 sceneFramebufferSize,
		RenderGraphResourceHandle sceneColorHandle,
		bool sceneEnabled,
		bool hasActiveDecals,
		Vector3 sunDirection,
		float sunIntensityScale,
		RenderConfig config,
		Vector3 cameraPosition)
	{
		BeginSharedFrame(framebufferSize, sunDirection, sunIntensityScale, config.SkyboxConfig);
		BeginViewFrame(
			RenderViewId.Primary,
			framebufferSize,
			sceneFramebufferSize,
			sceneColorHandle,
			sceneEnabled,
			hasActiveDecals,
			config,
			cameraPosition);
	}

	/// <summary>
	/// Sets up what every view in the frame shares: gameplay UI texture targets, the sky chain, and the final
	/// presentation target. Once per frame, before any view.
	/// </summary>
	/// <remarks>
	/// The procedural sky is prepared from one sun. With several views it is the bound view's, so a second
	/// world with a different sun direction currently sees the first view's sky; keying the sky by its config
	/// is the fix when that matters.
	/// </remarks>
	public void BeginSharedFrame(Int2 framebufferSize, Vector3 sunDirection, float sunIntensityScale, SkyboxPass.Config skyboxConfig)
	{
		_frameViews.Clear();
		var device = _renderer.GetGfxDevice();
		_gameplayTextureTargets.Clear();
		for (var i = 0; i < _gameplayUiFrame.TextureSurfaces.Length; i++)
		{
			var surface = _gameplayUiFrame.TextureSurfaces[i];
			var texture = _gameplayUiRenderer.EnsureTarget(device, surface.Target);
			var handle = _resources.ImportTexture(texture, takeOwnership: false, initialState: ResourceState.ShaderResource);
			_gameplayTextureTargets.Add(new GameplayTextureTarget(surface, handle, texture));
		}
		_gameplayUiRenderer.PruneTargets(device, _gameplayUiFrame);
		_skyboxPass.PrepareFrame(_renderer.GetGfxDevice(), sunDirection, sunIntensityScale, skyboxConfig);
		var activeSkybox = _externalSkybox ?? _skyboxPass.GetProceduralResources();
		_useProceduralSkybox = ReferenceEquals(activeSkybox, _externalSkybox) == false;
		_recordProceduralSkyLighting = _useProceduralSkybox && _skyboxPass.ShouldRecordProceduralLightingUpdate;
		_recordProceduralSkyBrdf = _useProceduralSkybox && _skyboxPass.ShouldRecordBrdfLutUpdate;
		_proceduralSkyboxInitialState = _skyboxPass.ProceduralResourcesInitialState;

		var skyboxEnvHandle = default(RenderGraphResourceHandle);
		var skyboxIrrHandle = default(RenderGraphResourceHandle);
		var skyboxPrefilterHandle = default(RenderGraphResourceHandle);
		var skyboxBrdfHandle = default(RenderGraphResourceHandle);
		if (activeSkybox.EnvironmentTexture is IGfxTexture envTexture)
		{
			var initialState = _useProceduralSkybox
				? _proceduralSkyboxInitialState
				: ResourceState.ShaderResource;
			skyboxEnvHandle = _resources.ImportTexture(envTexture, takeOwnership: false, initialState: initialState);
			if (activeSkybox.IrradianceTexture is IGfxTexture irr)
			{
				skyboxIrrHandle = _resources.ImportTexture(irr, takeOwnership: false, initialState: initialState);
			}
			if (activeSkybox.PrefilteredEnvironment is IGfxTexture prefilter)
			{
				skyboxPrefilterHandle = _resources.ImportTexture(prefilter, takeOwnership: false, initialState: initialState);
			}
			if (activeSkybox.BrdfLut is IGfxTexture brdf)
			{
				skyboxBrdfHandle = _resources.ImportTexture(brdf, takeOwnership: false, initialState: initialState);
			}
		}

		_sharedResources = new()
		{
			FramebufferSize = framebufferSize,
			FinalColor = _resources.CreateTransientTexture(new TextureDescriptor(
				framebufferSize.X,
				framebufferSize.Y,
				TextureFormat.Bgra8Unorm,
				TextureUsage.RenderTarget | TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
				new ColorRGBA(0.05f, 0.05f, 0.05f, 1.0f))),
			SkyboxEnvironment = skyboxEnvHandle,
			SkyboxIrradiance = skyboxIrrHandle,
			SkyboxPrefilter = skyboxPrefilterHandle,
			SkyboxBrdfLut = skyboxBrdfHandle
		};
	}

	/// <summary>
	/// Sets up <paramref name="view"/> for recording: its history bookkeeping, its transient resources and its
	/// debug views. Once per view per frame, after <see cref="BeginSharedFrame"/>, and immediately before that
	/// view's passes are recorded.
	/// </summary>
	public void BeginViewFrame(
		RenderViewId view,
		Int2 framebufferSize,
		Int2 sceneFramebufferSize,
		RenderGraphResourceHandle sceneColorHandle,
		bool sceneEnabled,
		bool hasActiveDecals,
		RenderConfig config,
		Vector3 cameraPosition)
	{
		_view = GetOrCreateViewState(view);
		if (sceneEnabled && _frameViews.Contains(view) == false)
		{
			_frameViews.Add(view);
		}

		var device = _renderer.GetGfxDevice();
		if (RequiresRayTracingScene(config) && (device.SupportsRayTracing == false || _renderer.GetPackedMeshIndexBuffer() is null))
		{
			config = CreateRayTracingDisabledConfig(config);
		}

		var taaEnabled = config.AntiAliasing.Enabled;
		var frameShapeChanged = _view.HasPreviousFrameShape == false ||
		                        _view.PreviousFramebufferSize.X != framebufferSize.X ||
		                        _view.PreviousFramebufferSize.Y != framebufferSize.Y ||
		                        _view.PreviousSceneFramebufferSize.X != sceneFramebufferSize.X ||
		                        _view.PreviousSceneFramebufferSize.Y != sceneFramebufferSize.Y ||
		                        _view.PreviousSceneEnabled != sceneEnabled;
		var shadowMapResolution = config.ShadowMaps.Enabled
			? Math.Max(1, config.ShadowMaps.CascadeResolution)
			: 1;
		InvalidateTransientPoolIfFrameShapeChanged(framebufferSize, sceneFramebufferSize, shadowMapResolution, sceneEnabled);
		_view.SceneDebugViews.Clear();
		_view.SceneDebugViewOptions = Array.Empty<SceneDebugViewOption>();
		_view.ResolvedSceneViewportState = SceneViewportRenderState.Empty;
		_view.CurrentDdgiConfigValid = false;
		_view.ResetTaaHistoryThisFrame = frameShapeChanged || (taaEnabled &&
			(!_view.PreviousTaaEnabled || _view.PreviousAntiAliasingMode != config.AntiAliasing.Mode));
		_view.PreviousAntiAliasingMode = config.AntiAliasing.Mode;
		if (!taaEnabled || !sceneEnabled)
		{
			_view.ReleaseTemporalHistoryResources();
		}
		if (!config.VolumetricFog.Enabled || !sceneEnabled)
		{
			_view.ReleaseFogHistoryResources();
		}
		_view.PreviousTaaEnabled = taaEnabled;
		var lightingHandle = default(RenderGraphResourceHandle);
		var reflectionsTraceHandle = default(RenderGraphResourceHandle);
		var reflectionsRadianceHandle = default(RenderGraphResourceHandle);
		var colorPyramidLevelHandles = Array.Empty<RenderGraphResourceHandle>();
		var colorPyramidHistoryValid = false;
		var gbufferAlbedoHandle = default(RenderGraphResourceHandle);
		var gbufferNormalHandle = default(RenderGraphResourceHandle);
		var gbufferMaterialHandle = default(RenderGraphResourceHandle);
		var gbufferEmissiveHandle = default(RenderGraphResourceHandle);
		var decalSourceAlbedoHandle = default(RenderGraphResourceHandle);
		var decalSourceNormalHandle = default(RenderGraphResourceHandle);
		var decalSourceMaterialHandle = default(RenderGraphResourceHandle);
		var decalSourceEmissiveHandle = default(RenderGraphResourceHandle);
		var gbufferDepthHandle = default(RenderGraphResourceHandle);
		var gbufferVelocityHandle = default(RenderGraphResourceHandle);
		var motionVectorDebugHandle = default(RenderGraphResourceHandle);
		var shadowMapHandle0 = default(RenderGraphResourceHandle);
		var shadowMapHandle1 = default(RenderGraphResourceHandle);
		var shadowMapHandle2 = default(RenderGraphResourceHandle);
		var ambientOcclusionRawHandle = default(RenderGraphResourceHandle);
		var ambientOcclusionTempHandle = default(RenderGraphResourceHandle);
		var ambientOcclusionFinalHandle = default(RenderGraphResourceHandle);
		var rayTracingHitMaskHandle = default(RenderGraphResourceHandle);
		var rayTracingHitDistanceHandle = default(RenderGraphResourceHandle);
		var rayTracingAlbedoHandle = default(RenderGraphResourceHandle);
		var ddgiTraceIrradianceHandle = default(RenderGraphResourceHandle);
		var ddgiTraceVisibilityHandle = default(RenderGraphResourceHandle);
		var ddgiIrradianceEstimatorHandle = default(RenderGraphResourceHandle);
		var ddgiIrradianceL0ReadHandle = default(RenderGraphResourceHandle);
		var ddgiIrradianceL0WriteHandle = default(RenderGraphResourceHandle);
		var ddgiIrradianceLyReadHandle = default(RenderGraphResourceHandle);
		var ddgiIrradianceLyWriteHandle = default(RenderGraphResourceHandle);
		var ddgiIrradianceLzReadHandle = default(RenderGraphResourceHandle);
		var ddgiIrradianceLzWriteHandle = default(RenderGraphResourceHandle);
		var ddgiIrradianceLxReadHandle = default(RenderGraphResourceHandle);
		var ddgiIrradianceLxWriteHandle = default(RenderGraphResourceHandle);
		var ddgiVisibilityReadHandle = default(RenderGraphResourceHandle);
		var ddgiVisibilityWriteHandle = default(RenderGraphResourceHandle);
		var ddgiProbeStateReadHandle = default(RenderGraphResourceHandle);
		var ddgiProbeStateWriteHandle = default(RenderGraphResourceHandle);
		var ddgiProbeActivityHandle = default(RenderGraphResourceHandle);
		var ddgiProbeActivityMarksHandle = default(RenderGraphResourceHandle);
		var ddgiRuntimeOrigin = config.DiffuseGlobalIllumination.Origin;
		var ddgiStorageOffset = default(Int3);
		var ddgiScrollDelta = default(Int3);
		var ddgiFinalContributionHandle = default(RenderGraphResourceHandle);
		var ddgiProbeBaseWeightDebugHandle = default(RenderGraphResourceHandle);
		var ddgiWeightedVisibilityDebugHandle = default(RenderGraphResourceHandle);
		var ddgiDominantProbeDebugHandle = default(RenderGraphResourceHandle);
		var ddgiDominantProbeCoordDebugHandle = default(RenderGraphResourceHandle);
		var ddgiProbeRelocationDebugHandle = default(RenderGraphResourceHandle);
		var ddgiProbeRelocationDecisionHandle = default(RenderGraphResourceHandle);
		var ddgiProbeRelocationDecisionDebugHandle = default(RenderGraphResourceHandle);
		var resolvedSceneColorHandle = default(RenderGraphResourceHandle);
		var historyColorReadHandle = default(RenderGraphResourceHandle);
		var historyColorWriteHandle = default(RenderGraphResourceHandle);
		var historyDepthReadHandle = default(RenderGraphResourceHandle);
		var historyDepthWriteHandle = default(RenderGraphResourceHandle);
		var bloomDownsampleLevels = Array.Empty<RenderGraphResourceHandle>();
		var bloomUpsampleLevels = Array.Empty<RenderGraphResourceHandle>();
		var bloomCompositeSceneColorHandle = default(RenderGraphResourceHandle);
		var fogCurrentHandle = default(RenderGraphResourceHandle);
		var fogHistoryReadHandle = default(RenderGraphResourceHandle);
		var fogHistoryWriteHandle = default(RenderGraphResourceHandle);
		var fogIntegratedHandle = default(RenderGraphResourceHandle);
		var fogHistoryValid = false;
		var fsr3Resources = default(Fsr3FrameResources);
		if (sceneEnabled)
		{
			gbufferAlbedoHandle = _resources.CreateTransientTexture(new TextureDescriptor(
				sceneFramebufferSize.X,
				sceneFramebufferSize.Y,
				TextureFormat.Bgra8Unorm,
				TextureUsage.RenderTarget | TextureUsage.ShaderResource,
				new ColorRGBA(0.392f, 0.584f, 0.929f, 1.0f)));
			gbufferNormalHandle = _resources.CreateTransientTexture(new TextureDescriptor(
				sceneFramebufferSize.X,
				sceneFramebufferSize.Y,
				TextureFormat.Rgba16Float,
				TextureUsage.RenderTarget | TextureUsage.ShaderResource,
				new ColorRGBA(0.5f, 0.5f, 1.0f, 1.0f)));
			gbufferMaterialHandle = _resources.CreateTransientTexture(new TextureDescriptor(
				sceneFramebufferSize.X,
				sceneFramebufferSize.Y,
				TextureFormat.Rgba8Unorm,
				TextureUsage.RenderTarget | TextureUsage.ShaderResource,
				new ColorRGBA(0.0f, 0.0f, 0.0f, 1.0f)));
			gbufferEmissiveHandle = _resources.CreateTransientTexture(new TextureDescriptor(
				sceneFramebufferSize.X,
				sceneFramebufferSize.Y,
				TextureFormat.Rgba16Float,
				TextureUsage.RenderTarget | TextureUsage.ShaderResource,
				new ColorRGBA(0.0f, 0.0f, 0.0f, 1.0f)));
			gbufferVelocityHandle = _resources.CreateTransientTexture(new TextureDescriptor(
				sceneFramebufferSize.X,
				sceneFramebufferSize.Y,
				TextureFormat.Rgba16Float,
				TextureUsage.RenderTarget | TextureUsage.ShaderResource,
				new ColorRGBA(0.0f, 0.0f, 0.0f, 0.0f)));
			if (IsMotionVectorDebugView(_view.RequestedSceneDebugViewId))
			{
				motionVectorDebugHandle = _resources.CreateTransientTexture(new TextureDescriptor(
					sceneFramebufferSize.X,
					sceneFramebufferSize.Y,
					TextureFormat.Rgba16Float,
					TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
					new ColorRGBA(0.0f, 0.0f, 0.0f, 1.0f)));
			}
			gbufferDepthHandle = _resources.CreateTransientTexture(new TextureDescriptor(
				sceneFramebufferSize.X,
				sceneFramebufferSize.Y,
				TextureFormat.D32Float,
				TextureUsage.DepthStencil | TextureUsage.ShaderResource,
				default(ColorRGBA),
				1.0f));
			if (config.Decals.Enabled && hasActiveDecals)
			{
				decalSourceAlbedoHandle = gbufferAlbedoHandle;
				decalSourceNormalHandle = gbufferNormalHandle;
				decalSourceMaterialHandle = gbufferMaterialHandle;
				decalSourceEmissiveHandle = gbufferEmissiveHandle;
				gbufferAlbedoHandle = _resources.CreateTransientTexture(new TextureDescriptor(
					sceneFramebufferSize.X,
					sceneFramebufferSize.Y,
					TextureFormat.Bgra8Unorm,
					TextureUsage.RenderTarget | TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
					new ColorRGBA(0.392f, 0.584f, 0.929f, 1.0f)));
				gbufferNormalHandle = _resources.CreateTransientTexture(new TextureDescriptor(
					sceneFramebufferSize.X,
					sceneFramebufferSize.Y,
					TextureFormat.Rgba16Float,
					TextureUsage.RenderTarget | TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
					new ColorRGBA(0.5f, 0.5f, 1.0f, 1.0f)));
				gbufferMaterialHandle = _resources.CreateTransientTexture(new TextureDescriptor(
					sceneFramebufferSize.X,
					sceneFramebufferSize.Y,
					TextureFormat.Rgba8Unorm,
					TextureUsage.RenderTarget | TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
					new ColorRGBA(0.0f, 0.0f, 0.0f, 1.0f)));
				gbufferEmissiveHandle = _resources.CreateTransientTexture(new TextureDescriptor(
					sceneFramebufferSize.X,
					sceneFramebufferSize.Y,
					TextureFormat.Rgba16Float,
					TextureUsage.RenderTarget | TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
					new ColorRGBA(0.0f, 0.0f, 0.0f, 1.0f)));
			}
			shadowMapHandle0 = _resources.CreateTransientTexture(new TextureDescriptor(
				shadowMapResolution,
				shadowMapResolution,
				TextureFormat.D32Float,
				TextureUsage.DepthStencil | TextureUsage.ShaderResource,
				default(ColorRGBA),
				1.0f));
			if (config.ShadowMaps.Enabled)
			{
				shadowMapHandle1 = _resources.CreateTransientTexture(new TextureDescriptor(
					shadowMapResolution,
					shadowMapResolution,
					TextureFormat.D32Float,
					TextureUsage.DepthStencil | TextureUsage.ShaderResource,
					default(ColorRGBA),
					1.0f));
				shadowMapHandle2 = _resources.CreateTransientTexture(new TextureDescriptor(
					shadowMapResolution,
					shadowMapResolution,
					TextureFormat.D32Float,
					TextureUsage.DepthStencil | TextureUsage.ShaderResource,
					default(ColorRGBA),
					1.0f));
			}
			else
			{
				// Lighting bindings still expect three depth handles even when shadow sampling is off.
				shadowMapHandle1 = shadowMapHandle0;
				shadowMapHandle2 = shadowMapHandle0;
			}
			resolvedSceneColorHandle = sceneColorHandle.IsValid
				? sceneColorHandle
				: _resources.CreateTransientTexture(new TextureDescriptor(
					sceneFramebufferSize.X,
					sceneFramebufferSize.Y,
					TextureFormat.Rgba16Float,
					TextureUsage.RenderTarget | TextureUsage.ShaderResource | TextureUsage.UnorderedAccess));
			var transparencyMaskHandle = _resources.CreateTransientTexture(new TextureDescriptor(
				sceneFramebufferSize.X, sceneFramebufferSize.Y, TextureFormat.Rgba8Unorm,
				TextureUsage.RenderTarget | TextureUsage.ShaderResource,
				new ColorRGBA(0.0f, 0.0f, 0.0f, 0.0f)));
			var reflectionsEnabled = HasReflections(config);
			lightingHandle = taaEnabled
				? _resources.CreateTransientTexture(new TextureDescriptor(
					sceneFramebufferSize.X,
					sceneFramebufferSize.Y,
					TextureFormat.Rgba16Float,
					TextureUsage.RenderTarget | TextureUsage.ShaderResource | TextureUsage.UnorderedAccess))
				: resolvedSceneColorHandle;
			if (reflectionsEnabled)
			{
				reflectionsRadianceHandle = _resources.CreateTransientTexture(new TextureDescriptor(
					sceneFramebufferSize.X,
					sceneFramebufferSize.Y,
					TextureFormat.Rgba16Float,
					TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
					new ColorRGBA(0.0f, 0.0f, 0.0f, 0.0f)));
				var reflectionTraceSize = GetReflectionTraceSize(sceneFramebufferSize, config.Reflections);
				reflectionsTraceHandle =
					reflectionTraceSize.X == sceneFramebufferSize.X &&
					reflectionTraceSize.Y == sceneFramebufferSize.Y
						? reflectionsRadianceHandle
						: _resources.CreateTransientTexture(new TextureDescriptor(
							reflectionTraceSize.X,
							reflectionTraceSize.Y,
							TextureFormat.Rgba16Float,
							TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
							new ColorRGBA(0.0f, 0.0f, 0.0f, 0.0f)));
				EnsureColorPyramidResources(_renderer.GetGfxDevice(), sceneFramebufferSize);
				if (_view.ColorPyramidTextures.Length > 0)
				{
					colorPyramidLevelHandles = new RenderGraphResourceHandle[_view.ColorPyramidTextures.Length];
					for (var level = 0; level < _view.ColorPyramidTextures.Length; level++)
					{
						colorPyramidLevelHandles[level] = _resources.ImportTexture(
							_view.ColorPyramidTextures[level],
							takeOwnership: false,
							initialState: _view.ColorPyramidStates[level]);
					}
					colorPyramidHistoryValid = _view.ColorPyramidValid;
				}
				else
				{
					_view.ColorPyramidValid = false;
				}
			}

			if (config.VolumetricFog.Enabled)
			{
				var fogGrid = VolumetricFogPass.ComputeGrid(sceneFramebufferSize, config.VolumetricFog);
				var maxDistance = Math.Max(config.VolumetricFog.MaxDistance, 0.001f);
				EnsureFogHistoryResources(device, fogGrid, maxDistance);
				var fogWriteIndex = 1 - _view.FogHistoryReadIndex;
				fogCurrentHandle = _resources.CreateTransientTexture(CreateFogTextureDescriptor(fogGrid));
				fogIntegratedHandle = _resources.CreateTransientTexture(CreateFogTextureDescriptor(fogGrid));
				if (_view.FogHistoryTextures[_view.FogHistoryReadIndex] is IGfxTexture fogHistoryRead &&
				    _view.FogHistoryTextures[fogWriteIndex] is IGfxTexture fogHistoryWrite)
				{
					fogHistoryReadHandle = _resources.ImportTexture(fogHistoryRead, false, _view.FogHistoryStates[_view.FogHistoryReadIndex]);
					fogHistoryWriteHandle = _resources.ImportTexture(fogHistoryWrite, false, _view.FogHistoryStates[fogWriteIndex]);
					fogHistoryValid = _view.FogHistoryValid && !frameShapeChanged;
				}
			}
			else
			{
				_view.ReleaseFogHistoryResources();
			}

			fsr3Resources = new Fsr3FrameResources { TransparencyMask = transparencyMaskHandle };
			if (taaEnabled)
			{
				EnsureTemporalHistoryResources(_renderer.GetGfxDevice(), sceneFramebufferSize, config.AntiAliasing.Mode);
				var historyWriteIndex = 1 - _view.HistoryReadIndex;
				if (_view.HistoryColorTextures[_view.HistoryReadIndex] is IGfxTexture historyColorRead &&
				    _view.HistoryColorTextures[historyWriteIndex] is IGfxTexture historyColorWrite &&
				    _view.HistoryDepthTextures[_view.HistoryReadIndex] is IGfxTexture historyDepthRead &&
				    _view.HistoryDepthTextures[historyWriteIndex] is IGfxTexture historyDepthWrite)
				{
					historyColorReadHandle = _resources.ImportTexture(
						historyColorRead,
						takeOwnership: false,
						initialState: _view.HistoryColorStates[_view.HistoryReadIndex]);
					historyColorWriteHandle = _resources.ImportTexture(
						historyColorWrite,
						takeOwnership: false,
						initialState: _view.HistoryColorStates[historyWriteIndex]);
					historyDepthReadHandle = _resources.ImportTexture(
						historyDepthRead,
						takeOwnership: false,
						initialState: _view.HistoryDepthStates[_view.HistoryReadIndex]);
					historyDepthWriteHandle = _resources.ImportTexture(
						historyDepthWrite,
						takeOwnership: false,
						initialState: _view.HistoryDepthStates[historyWriteIndex]);
				}
				else
				{
					_view.ResetTaaHistoryThisFrame = true;
					_view.HistoryValid = false;
				}

				var writeIndex = 1 - _view.HistoryReadIndex;
				if (config.AntiAliasing.UsesFsr3 &&
				    _view.Fsr3CurrentLumaTextures[_view.HistoryReadIndex] is IGfxTexture currentLumaRead &&
				    _view.Fsr3CurrentLumaTextures[writeIndex] is IGfxTexture currentLumaWrite &&
				    _view.Fsr3AccumulationTextures[_view.HistoryReadIndex] is IGfxTexture accumulationRead &&
				    _view.Fsr3AccumulationTextures[writeIndex] is IGfxTexture accumulationWrite)
				{
					var currentLumaReadHandle = _resources.ImportTexture(currentLumaRead, false,
						_view.Fsr3CurrentLumaStates[_view.HistoryReadIndex]);
					var currentLumaWriteHandle = _resources.ImportTexture(currentLumaWrite, false,
						_view.Fsr3CurrentLumaStates[writeIndex]);
					var accumulationReadHandle = _resources.ImportTexture(accumulationRead, false,
						_view.Fsr3AccumulationStates[_view.HistoryReadIndex]);
					var accumulationWriteHandle = _resources.ImportTexture(accumulationWrite, false,
						_view.Fsr3AccumulationStates[writeIndex]);
					var frameInfoHandle = _resources.ImportTexture(
						_view.Fsr3FrameInfoTexture ?? throw new InvalidOperationException("FSR3 frame info was not allocated."),
						false, _view.Fsr3FrameInfoState);
					var lumaSpdMips = CreateFsr3SpdMips(sceneFramebufferSize);
					var shadingSpdMips = CreateFsr3SpdMips(new Int2(
						Math.Max(sceneFramebufferSize.X / 2, 1), Math.Max(sceneFramebufferSize.Y / 2, 1)));
					fsr3Resources = new Fsr3FrameResources
					{
						TransparencyMask = transparencyMaskHandle,
						DilatedMotionVectors = CreateFsr3Texture(sceneFramebufferSize),
						DilatedDepth = CreateFsr3Texture(sceneFramebufferSize),
						FarthestDepth = CreateFsr3Texture(sceneFramebufferSize),
						ReconstructedPrevNearestDepth = CreateFsr3UintTexture(sceneFramebufferSize),
						CurrentLumaRead = currentLumaReadHandle,
						CurrentLumaWrite = currentLumaWriteHandle,
						FarthestDepthMip1 = CreateFsr3Texture(new Int2(
							Math.Max(sceneFramebufferSize.X / 2, 1), Math.Max(sceneFramebufferSize.Y / 2, 1))),
						FrameInfo = frameInfoHandle,
						LumaSpdAtomic = CreateFsr3UintTexture(new Int2(1, 1)),
						LumaSpdMips = lumaSpdMips,
						ShadingSpdAtomic = CreateFsr3UintTexture(new Int2(1, 1)),
						ShadingSpdMips = shadingSpdMips,
						// This target is physically half-resolution. Allocating it at full resolution makes
						// FSR3's half-resolution UV clamp sample the unwritten portion of the resource.
						ShadingChange = CreateFsr3Texture(GetFsr3ShadingChangeSize(sceneFramebufferSize)),
						AccumulationRead = accumulationReadHandle,
						AccumulationWrite = accumulationWriteHandle,
						DilatedReactiveMasks = CreateFsr3Texture(sceneFramebufferSize),
						NewLocks = CreateFsr3Texture(sceneFramebufferSize),
						LumaHistoryRead = historyDepthReadHandle,
						LumaHistoryWrite = historyDepthWriteHandle,
						LumaInstability = CreateFsr3Texture(sceneFramebufferSize),
						InternalHistoryRead = historyColorReadHandle,
						InternalHistoryWrite = historyColorWriteHandle,
						HistoryValid = _view.HistoryValid
					};
				}
			}

			if (HasAmbientOcclusion(config))
			{
				var aoSize = GetAmbientOcclusionInternalSize(sceneFramebufferSize, config.AmbientOcclusion.Resolution);
				ambientOcclusionRawHandle = _resources.CreateTransientTexture(new TextureDescriptor(
					aoSize.X,
					aoSize.Y,
					TextureFormat.Rgba16Float,
					TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
					new ColorRGBA(1.0f, 1.0f, 1.0f, 1.0f)));
				ambientOcclusionTempHandle = _resources.CreateTransientTexture(new TextureDescriptor(
					aoSize.X,
					aoSize.Y,
					TextureFormat.Rgba16Float,
					TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
					new ColorRGBA(1.0f, 1.0f, 1.0f, 1.0f)));
				ambientOcclusionFinalHandle = _resources.CreateTransientTexture(new TextureDescriptor(
					sceneFramebufferSize.X,
					sceneFramebufferSize.Y,
					TextureFormat.Rgba16Float,
					TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
					new ColorRGBA(1.0f, 1.0f, 1.0f, 1.0f)));
				if (config.AmbientOcclusion.Mode == AmbientOcclusionMode.RayTraced)
				{
					rayTracingHitMaskHandle = _resources.CreateTransientTexture(new TextureDescriptor(
						aoSize.X,
						aoSize.Y,
						TextureFormat.Rgba16Float,
						TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
						new ColorRGBA(0.0f, 0.0f, 0.0f, 1.0f)));
					rayTracingHitDistanceHandle = _resources.CreateTransientTexture(new TextureDescriptor(
						aoSize.X,
						aoSize.Y,
						TextureFormat.Rgba16Float,
						TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
						new ColorRGBA(1.0f, 1.0f, 1.0f, 1.0f)));
					rayTracingAlbedoHandle = _resources.CreateTransientTexture(new TextureDescriptor(
						sceneFramebufferSize.X,
						sceneFramebufferSize.Y,
						TextureFormat.Rgba16Float,
						TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
						new ColorRGBA(0.0f, 0.0f, 0.0f, 1.0f)));
				}
			}

			if (HasRayTracedDdgi(config))
			{
				var ddgiConfig = config.DiffuseGlobalIllumination;
				var ddgiGridShape = DdgiUtilities.GetGridShape(ddgiConfig);
				var ddgiProbeSpacing = Math.Max(ddgiConfig.ProbeSpacing, 0.001f);
				var irradianceAtlasSize = DdgiUtilities.GetAtlasSize(ddgiGridShape, DdgiUtilities.IrradianceTileInteriorSize);
				var visibilityAtlasSize = DdgiUtilities.GetAtlasSize(ddgiGridShape, DdgiUtilities.VisibilityTileInteriorSize);
				EnsureDdgiHistoryResources(
					_renderer.GetGfxDevice(),
					ddgiGridShape,
					ddgiConfig.Origin,
					ddgiProbeSpacing);
				ddgiRuntimeOrigin = DdgiUtilities.GetRuntimeOrigin(
					ddgiConfig.Origin,
					ddgiGridShape,
					ddgiProbeSpacing,
					cameraPosition);
				if (_view.DdgiHistoryValid && _view.DdgiCommittedPlacementValid)
				{
					ddgiScrollDelta = DdgiUtilities.GetScrollDelta(
						_view.DdgiCommittedRuntimeOrigin,
						ddgiRuntimeOrigin,
						ddgiProbeSpacing);
					ddgiStorageOffset = DdgiUtilities.AdvanceStorageOffset(
						_view.DdgiCommittedStorageOffset,
						ddgiScrollDelta,
						ddgiGridShape);
				}
				var ddgiWriteIndex = 1 - _view.DdgiHistoryReadIndex;
				if (_view.DdgiIrradianceTextures[0, _view.DdgiHistoryReadIndex] is IGfxTexture ddgiIrradianceL0Read &&
				    _view.DdgiIrradianceTextures[0, ddgiWriteIndex] is IGfxTexture ddgiIrradianceL0Write &&
				    _view.DdgiIrradianceTextures[1, _view.DdgiHistoryReadIndex] is IGfxTexture ddgiIrradianceLyRead &&
				    _view.DdgiIrradianceTextures[1, ddgiWriteIndex] is IGfxTexture ddgiIrradianceLyWrite &&
				    _view.DdgiIrradianceTextures[2, _view.DdgiHistoryReadIndex] is IGfxTexture ddgiIrradianceLzRead &&
				    _view.DdgiIrradianceTextures[2, ddgiWriteIndex] is IGfxTexture ddgiIrradianceLzWrite &&
				    _view.DdgiIrradianceTextures[3, _view.DdgiHistoryReadIndex] is IGfxTexture ddgiIrradianceLxRead &&
				    _view.DdgiIrradianceTextures[3, ddgiWriteIndex] is IGfxTexture ddgiIrradianceLxWrite &&
				    _view.DdgiVisibilityTextures[_view.DdgiHistoryReadIndex] is IGfxTexture ddgiVisibilityRead &&
				    _view.DdgiVisibilityTextures[ddgiWriteIndex] is IGfxTexture ddgiVisibilityWrite &&
				    _view.DdgiProbeStateTextures[_view.DdgiHistoryReadIndex] is IGfxTexture ddgiProbeStateRead &&
				    _view.DdgiProbeStateTextures[ddgiWriteIndex] is IGfxTexture ddgiProbeStateWrite &&
				    _view.DdgiProbeActivityTexture is IGfxTexture ddgiProbeActivity &&
				    _view.DdgiIrradianceEstimatorBuffer is IGfxBuffer ddgiIrradianceEstimator)
				{
					ddgiTraceIrradianceHandle = _resources.CreateTransientTexture(new TextureDescriptor(
						irradianceAtlasSize.X,
						irradianceAtlasSize.Y,
						TextureFormat.Rgba16Float,
						TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
						new ColorRGBA(0.0f, 0.0f, 0.0f, 1.0f)));
					ddgiTraceVisibilityHandle = _resources.CreateTransientTexture(new TextureDescriptor(
						visibilityAtlasSize.X,
						visibilityAtlasSize.Y,
						TextureFormat.Rgba16Float,
						TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
						new ColorRGBA(1.0f, 1.0f, 0.0f, 1.0f)));
					ddgiIrradianceEstimatorHandle = _resources.ImportBuffer(
						ddgiIrradianceEstimator,
						takeOwnership: false,
						initialState: _view.DdgiIrradianceEstimatorState);
					ddgiIrradianceL0ReadHandle = _resources.ImportTexture(
						ddgiIrradianceL0Read,
						takeOwnership: false,
						initialState: _view.DdgiIrradianceStates[0, _view.DdgiHistoryReadIndex]);
					ddgiIrradianceL0WriteHandle = _resources.ImportTexture(
						ddgiIrradianceL0Write,
						takeOwnership: false,
						initialState: _view.DdgiIrradianceStates[0, ddgiWriteIndex]);
					ddgiIrradianceLyReadHandle = _resources.ImportTexture(
						ddgiIrradianceLyRead,
						takeOwnership: false,
						initialState: _view.DdgiIrradianceStates[1, _view.DdgiHistoryReadIndex]);
					ddgiIrradianceLyWriteHandle = _resources.ImportTexture(
						ddgiIrradianceLyWrite,
						takeOwnership: false,
						initialState: _view.DdgiIrradianceStates[1, ddgiWriteIndex]);
					ddgiIrradianceLzReadHandle = _resources.ImportTexture(
						ddgiIrradianceLzRead,
						takeOwnership: false,
						initialState: _view.DdgiIrradianceStates[2, _view.DdgiHistoryReadIndex]);
					ddgiIrradianceLzWriteHandle = _resources.ImportTexture(
						ddgiIrradianceLzWrite,
						takeOwnership: false,
						initialState: _view.DdgiIrradianceStates[2, ddgiWriteIndex]);
					ddgiIrradianceLxReadHandle = _resources.ImportTexture(
						ddgiIrradianceLxRead,
						takeOwnership: false,
						initialState: _view.DdgiIrradianceStates[3, _view.DdgiHistoryReadIndex]);
					ddgiIrradianceLxWriteHandle = _resources.ImportTexture(
						ddgiIrradianceLxWrite,
						takeOwnership: false,
						initialState: _view.DdgiIrradianceStates[3, ddgiWriteIndex]);
					ddgiVisibilityReadHandle = _resources.ImportTexture(
						ddgiVisibilityRead,
						takeOwnership: false,
						initialState: _view.DdgiVisibilityStates[_view.DdgiHistoryReadIndex]);
					ddgiVisibilityWriteHandle = _resources.ImportTexture(
						ddgiVisibilityWrite,
						takeOwnership: false,
						initialState: _view.DdgiVisibilityStates[ddgiWriteIndex]);
					ddgiProbeStateReadHandle = _resources.ImportTexture(
						ddgiProbeStateRead,
						takeOwnership: false,
						initialState: _view.DdgiProbeStateStates[_view.DdgiHistoryReadIndex]);
					ddgiProbeStateWriteHandle = _resources.ImportTexture(
						ddgiProbeStateWrite,
						takeOwnership: false,
						initialState: _view.DdgiProbeStateStates[ddgiWriteIndex]);
					ddgiProbeActivityHandle = _resources.ImportTexture(
						ddgiProbeActivity,
						takeOwnership: false,
						initialState: _view.DdgiProbeActivityState);
					ddgiProbeActivityMarksHandle = _resources.CreateTransientTexture(new TextureDescriptor(
						ddgiGridShape.AtlasColumns,
						ddgiGridShape.AtlasRows,
						TextureFormat.R32Uint,
						TextureUsage.UnorderedAccess,
						new ColorRGBA(0.0f, 0.0f, 0.0f, 0.0f)));
					ddgiFinalContributionHandle = _resources.CreateTransientTexture(new TextureDescriptor(
						sceneFramebufferSize.X,
						sceneFramebufferSize.Y,
						TextureFormat.Rgba16Float,
						TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
						new ColorRGBA(0.0f, 0.0f, 0.0f, 1.0f)));
					ddgiProbeBaseWeightDebugHandle = CreateDdgiDebugTexture(sceneFramebufferSize);
					ddgiWeightedVisibilityDebugHandle = CreateDdgiDebugTexture(sceneFramebufferSize);
					ddgiDominantProbeDebugHandle = CreateDdgiDebugTexture(sceneFramebufferSize);
					ddgiDominantProbeCoordDebugHandle = CreateDdgiDebugTexture(sceneFramebufferSize);
					ddgiProbeRelocationDebugHandle = CreateDdgiDebugTexture(sceneFramebufferSize);
					ddgiProbeRelocationDecisionHandle = _resources.CreateTransientTexture(new TextureDescriptor(
						ddgiGridShape.AtlasColumns,
						ddgiGridShape.AtlasRows,
						TextureFormat.Rgba16Float,
						TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
						new ColorRGBA(0.0f, 0.0f, 0.0f, 1.0f)));
					ddgiProbeRelocationDecisionDebugHandle = CreateDdgiDebugTexture(sceneFramebufferSize);
				}
				else
				{
					_view.DdgiHistoryValid = false;
				}
			}
		}

		if (sceneEnabled && config.Bloom.Enabled)
		{
			var levelCount = GetBloomLevelCount(sceneFramebufferSize, config.Bloom.Quality);
			bloomDownsampleLevels = new RenderGraphResourceHandle[levelCount];
			bloomUpsampleLevels = new RenderGraphResourceHandle[Math.Max(levelCount - 1, 0)];
			var levelSize = new Int2(Math.Max(1, (sceneFramebufferSize.X + 1) / 2), Math.Max(1, (sceneFramebufferSize.Y + 1) / 2));
			for (var level = 0; level < levelCount; level++)
			{
				bloomDownsampleLevels[level] = CreateBloomTexture(levelSize);
				if (level < bloomUpsampleLevels.Length)
				{
					bloomUpsampleLevels[level] = CreateBloomTexture(levelSize);
				}
				levelSize = new Int2(Math.Max(1, (levelSize.X + 1) / 2), Math.Max(1, (levelSize.Y + 1) / 2));
			}
			bloomCompositeSceneColorHandle = CreateBloomTexture(sceneFramebufferSize);
		}

		var tonemappedLinearSceneColorHandle = sceneEnabled
			? _resources.CreateTransientTexture(new TextureDescriptor(
				framebufferSize.X,
				framebufferSize.Y,
				TextureFormat.Rgba16Float,
				TextureUsage.RenderTarget | TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
				new ColorRGBA(0.05f, 0.05f, 0.05f, 1.0f)))
			: default;

		var displayLinearSceneColorHandle = sceneEnabled && config.AntiAliasing.UsesCasSharpening
			? _resources.CreateTransientTexture(new TextureDescriptor(
				framebufferSize.X,
				framebufferSize.Y,
				TextureFormat.Rgba16Float,
				TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
				new ColorRGBA(0.05f, 0.05f, 0.05f, 1.0f)))
			: tonemappedLinearSceneColorHandle;

		var encodedSceneColorHandle = sceneEnabled
			? _resources.CreateTransientTexture(new TextureDescriptor(
				framebufferSize.X,
				framebufferSize.Y,
				TextureFormat.Bgra8Unorm,
				TextureUsage.RenderTarget | TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
				new ColorRGBA(0.05f, 0.05f, 0.05f, 1.0f)))
			: default;

		_view.FrameResources = new()
		{
			FramebufferSize = framebufferSize,
			SceneFramebufferSize = sceneFramebufferSize,
			SceneEnabled = sceneEnabled,
			TonemappedLinearSceneColor = tonemappedLinearSceneColorHandle,
			DisplayLinearSceneColor = displayLinearSceneColorHandle,
			EncodedSceneColor = encodedSceneColorHandle,
			GBufferAlbedo = gbufferAlbedoHandle,
			GBufferNormal = gbufferNormalHandle,
			GBufferMaterial = gbufferMaterialHandle,
			GBufferEmissive = gbufferEmissiveHandle,
			DecalSourceGBufferAlbedo = decalSourceAlbedoHandle,
			DecalSourceGBufferNormal = decalSourceNormalHandle,
			DecalSourceGBufferMaterial = decalSourceMaterialHandle,
			DecalSourceGBufferEmissive = decalSourceEmissiveHandle,
			GBufferDepth = gbufferDepthHandle,
			GBufferVelocity = gbufferVelocityHandle,
			MotionVectorDebugColor = motionVectorDebugHandle,
			AmbientOcclusionRaw = ambientOcclusionRawHandle,
			AmbientOcclusionTemp = ambientOcclusionTempHandle,
			AmbientOcclusionFinal = ambientOcclusionFinalHandle,
			RayTracingHitMask = rayTracingHitMaskHandle,
			RayTracingHitDistance = rayTracingHitDistanceHandle,
			RayTracingAlbedo = rayTracingAlbedoHandle,
			DdgiTraceIrradiance = ddgiTraceIrradianceHandle,
			DdgiTraceVisibility = ddgiTraceVisibilityHandle,
			DdgiIrradianceEstimator = ddgiIrradianceEstimatorHandle,
			DdgiIrradianceL0HistoryRead = ddgiIrradianceL0ReadHandle,
			DdgiIrradianceL0HistoryWrite = ddgiIrradianceL0WriteHandle,
			DdgiIrradianceLyHistoryRead = ddgiIrradianceLyReadHandle,
			DdgiIrradianceLyHistoryWrite = ddgiIrradianceLyWriteHandle,
			DdgiIrradianceLzHistoryRead = ddgiIrradianceLzReadHandle,
			DdgiIrradianceLzHistoryWrite = ddgiIrradianceLzWriteHandle,
			DdgiIrradianceLxHistoryRead = ddgiIrradianceLxReadHandle,
			DdgiIrradianceLxHistoryWrite = ddgiIrradianceLxWriteHandle,
			DdgiVisibilityHistoryRead = ddgiVisibilityReadHandle,
			DdgiVisibilityHistoryWrite = ddgiVisibilityWriteHandle,
			DdgiProbeStateRead = ddgiProbeStateReadHandle,
			DdgiProbeStateWrite = ddgiProbeStateWriteHandle,
			DdgiProbeActivity = ddgiProbeActivityHandle,
			DdgiProbeActivityMarks = ddgiProbeActivityMarksHandle,
			DdgiRuntimeOrigin = ddgiRuntimeOrigin,
			DdgiStorageOffset = ddgiStorageOffset,
			DdgiScrollDelta = ddgiScrollDelta,
			DdgiFinalContribution = ddgiFinalContributionHandle,
			DdgiProbeBaseWeightDebug = ddgiProbeBaseWeightDebugHandle,
			DdgiWeightedVisibilityDebug = ddgiWeightedVisibilityDebugHandle,
			DdgiDominantProbeDebug = ddgiDominantProbeDebugHandle,
			DdgiDominantProbeCoordDebug = ddgiDominantProbeCoordDebugHandle,
			DdgiProbeRelocationDebug = ddgiProbeRelocationDebugHandle,
			DdgiProbeRelocationDecision = ddgiProbeRelocationDecisionHandle,
			DdgiProbeRelocationDecisionDebug = ddgiProbeRelocationDecisionDebugHandle,
			WriteDdgiFinalContributionDebug =
				ddgiFinalContributionHandle.IsValid &&
				IsDdgiFinalContributionDebugView(_view.RequestedSceneDebugViewId),
			WriteDdgiProbeDebug =
				ddgiProbeBaseWeightDebugHandle.IsValid &&
				ddgiWeightedVisibilityDebugHandle.IsValid &&
				ddgiDominantProbeDebugHandle.IsValid &&
				ddgiDominantProbeCoordDebugHandle.IsValid &&
				ddgiProbeRelocationDebugHandle.IsValid &&
				ddgiProbeRelocationDecisionDebugHandle.IsValid &&
				IsDdgiProbeDebugView(_view.RequestedSceneDebugViewId),
			ShadowMapDepth0 = shadowMapHandle0,
			ShadowMapDepth1 = shadowMapHandle1,
			ShadowMapDepth2 = shadowMapHandle2,
			LightingBuffer = lightingHandle,
			FogCurrent = fogCurrentHandle,
			FogHistoryRead = fogHistoryReadHandle,
			FogHistoryWrite = fogHistoryWriteHandle,
			FogIntegrated = fogIntegratedHandle,
			FogHistoryValid = fogHistoryValid,
			ReflectionsTrace = reflectionsTraceHandle,
			ReflectionsRadiance = reflectionsRadianceHandle,
			ResolvedSceneColor = resolvedSceneColorHandle,
			ColorPyramidLevels = colorPyramidLevelHandles,
			ColorPyramidHistoryValid = colorPyramidHistoryValid,
			HistoryColorRead = historyColorReadHandle,
			HistoryColorWrite = historyColorWriteHandle,
			HistoryDepthRead = historyDepthReadHandle,
			HistoryDepthWrite = historyDepthWriteHandle,
			Fsr3 = fsr3Resources,
			BloomDownsampleLevels = bloomDownsampleLevels,
			BloomUpsampleLevels = bloomUpsampleLevels,
			BloomCompositeSceneColor = bloomCompositeSceneColorHandle,
			Config = config
		};

		if (sceneEnabled)
		{
			RegisterSceneDebugView(SceneDebugViewIds.FinalColor, "Final Color", _view.FrameResources.EncodedSceneColor, SceneDebugViewKind.Color);
			if (bloomDownsampleLevels.Length > 0)
			{
				RegisterSceneDebugView(SceneDebugViewIds.BloomPrefilter, "Bloom Prefilter", bloomDownsampleLevels[0], SceneDebugViewKind.Color);
				RegisterSceneDebugView(
					SceneDebugViewIds.BloomContribution,
					"Bloom Contribution",
					bloomUpsampleLevels.Length > 0 ? bloomUpsampleLevels[0] : bloomDownsampleLevels[0],
					SceneDebugViewKind.Color);
			}
			if (ambientOcclusionFinalHandle.IsValid)
			{
				RegisterSceneDebugView(SceneDebugViewIds.AmbientOcclusion, "Ambient Occlusion", ambientOcclusionFinalHandle, SceneDebugViewKind.Color);
			}
			if (reflectionsRadianceHandle.IsValid)
			{
				RegisterSceneDebugView(SceneDebugViewIds.Reflections, "Reflection Radiance", reflectionsRadianceHandle, SceneDebugViewKind.Color);
			}
			if (colorPyramidLevelHandles.Length > 0)
			{
				RegisterSceneDebugView(SceneDebugViewIds.ColorPyramid, "Color Pyramid (Previous Frame)", colorPyramidLevelHandles[0], SceneDebugViewKind.Color);
			}
			if (rayTracingHitMaskHandle.IsValid)
			{
				RegisterSceneDebugView(SceneDebugViewIds.RayTracingHitMask, "Ray Tracing Hit Mask", rayTracingHitMaskHandle, SceneDebugViewKind.Color);
			}
			if (rayTracingHitDistanceHandle.IsValid)
			{
				RegisterSceneDebugView(SceneDebugViewIds.RayTracingHitDistance, "Ray Tracing Hit Distance", rayTracingHitDistanceHandle, SceneDebugViewKind.Color);
			}
			if (rayTracingAlbedoHandle.IsValid)
			{
				RegisterSceneDebugView(SceneDebugViewIds.RayTracingAlbedo, "Ray Tracing Albedo", rayTracingAlbedoHandle, SceneDebugViewKind.Color);
			}
			if (ddgiIrradianceL0WriteHandle.IsValid)
			{
				RegisterSceneDebugView(SceneDebugViewIds.DdgiIrradiance, "DDGI Irradiance L0", ddgiIrradianceL0WriteHandle, SceneDebugViewKind.Color);
			}
			if (ddgiVisibilityWriteHandle.IsValid)
			{
				RegisterSceneDebugView(SceneDebugViewIds.DdgiVisibility, "DDGI Visibility", ddgiVisibilityWriteHandle, SceneDebugViewKind.Color);
			}
			if (ddgiFinalContributionHandle.IsValid)
			{
				RegisterSceneDebugView(SceneDebugViewIds.DdgiFinalContribution, "DDGI Final Contribution", ddgiFinalContributionHandle, SceneDebugViewKind.Color);
			}
			if (ddgiProbeBaseWeightDebugHandle.IsValid)
			{
				RegisterSceneDebugView(SceneDebugViewIds.DdgiProbeBaseWeight, "DDGI Probe Base Weight", ddgiProbeBaseWeightDebugHandle, SceneDebugViewKind.Color);
				RegisterSceneDebugView(SceneDebugViewIds.DdgiWeightedVisibility, "DDGI Weighted Visibility", ddgiWeightedVisibilityDebugHandle, SceneDebugViewKind.Color);
				RegisterSceneDebugView(SceneDebugViewIds.DdgiDominantProbe, "DDGI Dominant Probe (Color) / Weight (Brightness)", ddgiDominantProbeDebugHandle, SceneDebugViewKind.Color);
				RegisterSceneDebugView(SceneDebugViewIds.DdgiDominantProbeCoord, "DDGI Dominant Probe Coord", ddgiDominantProbeCoordDebugHandle, SceneDebugViewKind.Color);
				RegisterSceneDebugView(SceneDebugViewIds.DdgiProbeRelocation, "DDGI Probe Relocation", ddgiProbeRelocationDebugHandle, SceneDebugViewKind.Color);
				RegisterSceneDebugView(SceneDebugViewIds.DdgiProbeRelocationDecision, "DDGI Probe Relocation Decision", ddgiProbeRelocationDecisionDebugHandle, SceneDebugViewKind.Color);
			}
			RegisterSceneDebugView(SceneDebugViewIds.GBufferAlbedo, "GBuffer Albedo", gbufferAlbedoHandle, SceneDebugViewKind.Color);
			RegisterSceneDebugView(SceneDebugViewIds.GBufferNormal, "GBuffer Normal", gbufferNormalHandle, SceneDebugViewKind.Color);
			// The flow-field encoding only exists while this view is selected; the option itself
			// has to stay in the dropdown so it can be selected in the first place.
			RegisterSceneDebugView(
				SceneDebugViewIds.MotionVectors,
				"Motion Vectors (Flow Field)",
				motionVectorDebugHandle.IsValid ? motionVectorDebugHandle : gbufferVelocityHandle,
				SceneDebugViewKind.Color);
			_view.SceneDebugViewOptions = BuildSceneDebugViewOptions();
		}
	}

	public void SetUiFrame(UiFrameData uiFrame)
	{
		_uiFrame = uiFrame;
	}

	public void SetGameplayUiFrame(GameplayUiRenderFrame frame)
	{
		_gameplayUiFrame = frame ?? GameplayUiRenderFrame.Empty;
	}

	public void SetSceneViewportSelection(string requestedDebugViewId)
	{
		_view.RequestedSceneDebugViewId = NormalizeSceneDebugViewId(requestedDebugViewId);
	}

	public SceneViewportRenderState GetSceneViewportRenderState() => _view.ResolvedSceneViewportState;
	

	[SuppressMessage("ReSharper", "RedundantArgumentDefaultValue")]
	public void Build(RenderGraph graph)
	{
		RecordSharedPreparation(graph);
		RecordBoundView(graph);
		RecordSharedPresentation(graph);
	}

	/// <summary>Records what every view depends on — gameplay UI textures and the sky chain. Once per frame, first.</summary>
	public void RecordSharedPreparation(RenderGraph graph) => RecordSharedPreparationPasses(graph);

	/// <summary>
	/// Records the bound view's passes, tagged with that view so execution can rebind to it. Once per view, after
	/// <see cref="BeginViewFrame"/> for that view.
	/// </summary>
	public void RecordBoundView(RenderGraph graph)
	{
		graph.BeginViewRecording(_view.View);
		try
		{
			RecordViewPasses(graph);
		}
		finally
		{
			graph.EndViewRecording();
		}
	}

	/// <summary>Records presentation shared by every view. Once per frame, after every view.</summary>
	public void RecordSharedPresentation(RenderGraph graph) => RecordSharedPresentationPasses(graph);

	private void RecordSharedPreparationPasses(RenderGraph graph)
	{
		for (var i = 0; i < _gameplayTextureTargets.Count; i++)
		{
			var target = _gameplayTextureTargets[i];
			if (target.Surface.IsDirty == false)
			{
				continue;
			}

			graph.AddPass($"Gameplay UI Texture {target.Surface.SurfaceId}", PassKind.Graphics)
				.WriteTexture(target.Handle, ResourceState.RenderTarget)
				.SetExecute(context => ExecuteGameplayTextureUi(context, target));
		}

		if (_useProceduralSkybox && _recordProceduralSkyLighting)
		{
			graph.AddPass("Skybox Environment", PassKind.Compute)
				.WriteTexture(_sharedResources.SkyboxEnvironment, ResourceState.UnorderedAccess)
				.SetExecute(_skyboxEnvironmentExecute);

			graph.AddPass("Skybox Irradiance", PassKind.Compute)
				.ReadTexture(_sharedResources.SkyboxEnvironment, ResourceState.ShaderResource)
				.WriteTexture(_sharedResources.SkyboxIrradiance, ResourceState.UnorderedAccess)
				.SetExecute(_skyboxIrradianceExecute);

			graph.AddPass("Skybox Prefilter", PassKind.Compute)
				.ReadTexture(_sharedResources.SkyboxEnvironment, ResourceState.ShaderResource)
				.WriteTexture(_sharedResources.SkyboxPrefilter, ResourceState.UnorderedAccess)
				.SetExecute(_skyboxPrefilterExecute);
		}

		if (_useProceduralSkybox && _recordProceduralSkyBrdf)
		{
			graph.AddPass("Skybox BRDF LUT", PassKind.Compute)
				.WriteTexture(_sharedResources.SkyboxBrdfLut, ResourceState.UnorderedAccess)
				.SetExecute(_skyboxBrdfExecute);
		}

		// The draw tables are shared by every view, so they are updated once, from every view's database,
		// before any view culls or draws.
		if (_frameViews.Count > 0)
		{
			graph.AddPass("GpuDraw Update", PassKind.Compute)
				.SetExecute(_gpuDrawUpdateExecute);
		}
	}

	[SuppressMessage("ReSharper", "RedundantArgumentDefaultValue")]
	private void RecordViewPasses(RenderGraph graph)
	{
		if (_view.FrameResources.SceneEnabled)
		{
			graph.AddPass("GpuDraw View Update", PassKind.Compute)
				.SetExecute(_gpuDrawViewUpdateExecute);

			if (_view.FrameResources.Config.ShadowMaps.Enabled)
			{
				graph.AddPass("GpuDraw Cull (Shadow View)", PassKind.Compute)
					.SetExecute(_gpuDrawShadowCullExecute);

				graph.AddPass("Shadow Map", PassKind.Graphics)
					.WriteTexture(_view.FrameResources.ShadowMapDepth0, ResourceState.DepthWrite)
					.WriteTexture(_view.FrameResources.ShadowMapDepth1, ResourceState.DepthWrite)
					.WriteTexture(_view.FrameResources.ShadowMapDepth2, ResourceState.DepthWrite)
					.SetExecute(_shadowMapExecute);
			}

			graph.AddPass("GpuDraw Cull (Camera View)", PassKind.Compute)
				.SetExecute(_gpuDrawCameraCullExecute);

			var gbufferBuilder = graph.AddPass("GBuffer", PassKind.Graphics)
				.WriteTexture(_view.FrameResources.DecalSourceGBufferAlbedo.IsValid ? _view.FrameResources.DecalSourceGBufferAlbedo : _view.FrameResources.GBufferAlbedo, ResourceState.RenderTarget)
				.WriteTexture(_view.FrameResources.DecalSourceGBufferNormal.IsValid ? _view.FrameResources.DecalSourceGBufferNormal : _view.FrameResources.GBufferNormal, ResourceState.RenderTarget)
				.WriteTexture(_view.FrameResources.DecalSourceGBufferMaterial.IsValid ? _view.FrameResources.DecalSourceGBufferMaterial : _view.FrameResources.GBufferMaterial, ResourceState.RenderTarget)
				.WriteTexture(_view.FrameResources.DecalSourceGBufferEmissive.IsValid ? _view.FrameResources.DecalSourceGBufferEmissive : _view.FrameResources.GBufferEmissive, ResourceState.RenderTarget)
				.WriteTexture(_view.FrameResources.GBufferVelocity, ResourceState.RenderTarget)
				.WriteTexture(_view.FrameResources.GBufferDepth, ResourceState.DepthWrite);
			for (var i = 0; i < _gameplayTextureTargets.Count; i++)
			{
				gbufferBuilder.ReadTexture(_gameplayTextureTargets[i].Handle, ResourceState.ShaderResource);
			}
			gbufferBuilder.SetExecute(_gbufferExecute);

			if (_view.FrameResources.DecalSourceGBufferAlbedo.IsValid)
			{
				graph.AddPass("GBuffer Decal Seed", PassKind.Compute)
					.ReadTexture(_view.FrameResources.DecalSourceGBufferAlbedo, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.DecalSourceGBufferNormal, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.DecalSourceGBufferMaterial, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.DecalSourceGBufferEmissive, ResourceState.ShaderResource)
					.WriteTexture(_view.FrameResources.GBufferAlbedo, ResourceState.UnorderedAccess)
					.WriteTexture(_view.FrameResources.GBufferNormal, ResourceState.UnorderedAccess)
					.WriteTexture(_view.FrameResources.GBufferMaterial, ResourceState.UnorderedAccess)
					.WriteTexture(_view.FrameResources.GBufferEmissive, ResourceState.UnorderedAccess)
					.SetExecute(_gBufferDecalSeedExecute);

				graph.AddPass("ScreenSpaceDecal", PassKind.Graphics)
					.ReadTexture(_view.FrameResources.DecalSourceGBufferAlbedo, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.DecalSourceGBufferNormal, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.DecalSourceGBufferMaterial, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.DecalSourceGBufferEmissive, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.GBufferDepth, ResourceState.ShaderResource)
					.WriteTexture(_view.FrameResources.GBufferAlbedo, ResourceState.RenderTarget)
					.WriteTexture(_view.FrameResources.GBufferNormal, ResourceState.RenderTarget)
					.WriteTexture(_view.FrameResources.GBufferMaterial, ResourceState.RenderTarget)
					.WriteTexture(_view.FrameResources.GBufferEmissive, ResourceState.RenderTarget)
					.SetExecute(_screenSpaceDecalExecute);
			}

			if (_view.FrameResources.AmbientOcclusionRaw.IsValid)
			{
				var ambientOcclusionEvaluateBuilder = graph.AddPass("Ambient Occlusion Evaluate", PassKind.Compute)
					.ReadTexture(_view.FrameResources.GBufferDepth, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.GBufferNormal, ResourceState.ShaderResource)
					.WriteTexture(_view.FrameResources.AmbientOcclusionRaw, ResourceState.UnorderedAccess);
				if (_view.FrameResources.RayTracingHitMask.IsValid)
				{
					ambientOcclusionEvaluateBuilder.WriteTexture(_view.FrameResources.RayTracingHitMask, ResourceState.UnorderedAccess);
				}
				if (_view.FrameResources.RayTracingHitDistance.IsValid)
				{
					ambientOcclusionEvaluateBuilder.WriteTexture(_view.FrameResources.RayTracingHitDistance, ResourceState.UnorderedAccess);
				}
				if (_view.FrameResources.RayTracingAlbedo.IsValid)
				{
					ambientOcclusionEvaluateBuilder.WriteTexture(_view.FrameResources.RayTracingAlbedo, ResourceState.UnorderedAccess);
				}
				ambientOcclusionEvaluateBuilder.SetExecute(_ambientOcclusionExecute);

				graph.AddPass("Ambient Occlusion Blur X", PassKind.Compute)
					.ReadTexture(_view.FrameResources.GBufferDepth, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.GBufferNormal, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.AmbientOcclusionRaw, ResourceState.ShaderResource)
					.WriteTexture(_view.FrameResources.AmbientOcclusionTemp, ResourceState.UnorderedAccess)
					.SetExecute(_ambientOcclusionBlurHorizontalExecute);

				var blurVerticalBuilder = graph.AddPass("Ambient Occlusion Blur Y", PassKind.Compute)
					.ReadTexture(_view.FrameResources.GBufferDepth, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.GBufferNormal, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.AmbientOcclusionTemp, ResourceState.ShaderResource);
				if (_view.FrameResources.Config.AmbientOcclusion.Resolution == AmbientOcclusionResolution.Half)
				{
					blurVerticalBuilder
						.WriteTexture(_view.FrameResources.AmbientOcclusionRaw, ResourceState.UnorderedAccess)
						.SetExecute(_ambientOcclusionBlurVerticalExecute);

					graph.AddPass("Ambient Occlusion Upsample", PassKind.Compute)
						.ReadTexture(_view.FrameResources.GBufferDepth, ResourceState.ShaderResource)
						.ReadTexture(_view.FrameResources.GBufferNormal, ResourceState.ShaderResource)
						.ReadTexture(_view.FrameResources.AmbientOcclusionRaw, ResourceState.ShaderResource)
						.WriteTexture(_view.FrameResources.AmbientOcclusionFinal, ResourceState.UnorderedAccess)
						.SetExecute(_ambientOcclusionUpsampleExecute);
				}
				else
				{
					blurVerticalBuilder
						.WriteTexture(_view.FrameResources.AmbientOcclusionFinal, ResourceState.UnorderedAccess)
						.SetExecute(_ambientOcclusionBlurVerticalExecute);
				}
			}

			if (_view.FrameResources.FogIntegrated.IsValid)
			{
				graph.AddPass("Volumetric Fog Inject", PassKind.Compute)
					.ReadTexture(_sharedResources.SkyboxIrradiance, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.ShadowMapDepth0, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.ShadowMapDepth1, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.ShadowMapDepth2, ResourceState.ShaderResource)
					.WriteTexture(_view.FrameResources.FogCurrent, ResourceState.UnorderedAccess)
					.SetExecute(_volumetricFogInjectExecute);
				graph.AddPass("Volumetric Fog Temporal", PassKind.Compute)
					.ReadTexture(_view.FrameResources.FogCurrent, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.FogHistoryRead, ResourceState.ShaderResource)
					.WriteTexture(_view.FrameResources.FogHistoryWrite, ResourceState.UnorderedAccess)
					.SetExecute(_volumetricFogTemporalExecute);
				graph.AddPass("Volumetric Fog Integrate", PassKind.Compute)
					.ReadTexture(_view.FrameResources.FogHistoryWrite, ResourceState.ShaderResource)
					.WriteTexture(_view.FrameResources.FogIntegrated, ResourceState.UnorderedAccess)
					.SetExecute(_volumetricFogIntegrateExecute);
			}

			if (_view.FrameResources.DdgiTraceIrradiance.IsValid &&
			    _view.FrameResources.DdgiTraceVisibility.IsValid &&
			    _view.FrameResources.DdgiIrradianceEstimator.IsValid &&
			    _view.FrameResources.DdgiIrradianceL0HistoryRead.IsValid &&
				    _view.FrameResources.DdgiIrradianceL0HistoryWrite.IsValid &&
				    _view.FrameResources.DdgiIrradianceLyHistoryRead.IsValid &&
				    _view.FrameResources.DdgiIrradianceLyHistoryWrite.IsValid &&
				    _view.FrameResources.DdgiIrradianceLzHistoryRead.IsValid &&
				    _view.FrameResources.DdgiIrradianceLzHistoryWrite.IsValid &&
				    _view.FrameResources.DdgiIrradianceLxHistoryRead.IsValid &&
				    _view.FrameResources.DdgiIrradianceLxHistoryWrite.IsValid &&
				    _view.FrameResources.DdgiVisibilityHistoryRead.IsValid &&
				    _view.FrameResources.DdgiVisibilityHistoryWrite.IsValid &&
				    _view.FrameResources.DdgiProbeStateRead.IsValid &&
				    _view.FrameResources.DdgiProbeStateWrite.IsValid &&
				    _view.FrameResources.DdgiProbeActivity.IsValid &&
				    _view.FrameResources.DdgiProbeActivityMarks.IsValid)
			{
				graph.AddPass("DDGI Probe Classify", PassKind.Compute)
					.WriteTexture(_view.FrameResources.DdgiProbeActivityMarks, ResourceState.UnorderedAccess)
					.WriteTexture(_view.FrameResources.DdgiProbeActivity, ResourceState.UnorderedAccess)
					.SetExecute(_ddgiClassifyExecute);

				if (DdgiUtilities.IsRelocationTraceEnabled(_view.FrameResources.Config))
				{
					graph.AddPass("DDGI Relocation Trace", PassKind.Compute)
						.ReadTexture(_view.FrameResources.DdgiProbeActivity, ResourceState.ShaderResource)
						.ReadTexture(_view.FrameResources.DdgiProbeStateRead, ResourceState.ShaderResource)
						.WriteTexture(_view.FrameResources.DdgiTraceVisibility, ResourceState.UnorderedAccess)
						.SetExecute(context => ExecuteDdgiRelocationTrace(context, 0));
				}

				var relocationSolveBuilder = graph.AddPass("DDGI Relocation Solve", PassKind.Compute)
					.ReadTexture(_view.FrameResources.DdgiProbeActivity, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.DdgiProbeStateRead, ResourceState.ShaderResource)
					.WriteTexture(_view.FrameResources.DdgiProbeStateWrite, ResourceState.UnorderedAccess)
					.WriteTexture(_view.FrameResources.DdgiProbeRelocationDecision, ResourceState.UnorderedAccess);
				if (DdgiUtilities.IsRelocationTraceEnabled(_view.FrameResources.Config))
				{
					relocationSolveBuilder.ReadTexture(
						_view.FrameResources.DdgiTraceVisibility,
						ResourceState.ShaderResource);
				}
				relocationSolveBuilder.SetExecute(context => ExecuteDdgiRelocate(context, 0));

				var ddgiTraceBuilder = graph.AddPass("DDGI Probe Trace", PassKind.Compute)
					.ReadTexture(_view.FrameResources.DdgiProbeActivity, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.DdgiProbeStateRead, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.DdgiProbeStateWrite, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.DdgiIrradianceL0HistoryRead, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.DdgiIrradianceLyHistoryRead, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.DdgiIrradianceLzHistoryRead, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.DdgiIrradianceLxHistoryRead, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.DdgiVisibilityHistoryRead, ResourceState.ShaderResource)
					.WriteTexture(_view.FrameResources.DdgiTraceIrradiance, ResourceState.UnorderedAccess)
					.WriteTexture(_view.FrameResources.DdgiTraceVisibility, ResourceState.UnorderedAccess);
				if (_sharedResources.SkyboxEnvironment.IsValid)
				{
					ddgiTraceBuilder.ReadTexture(_sharedResources.SkyboxEnvironment, ResourceState.ShaderResource);
				}
				ddgiTraceBuilder.SetExecute(_ddgiTraceExecute);

				graph.AddPass("DDGI Irradiance Integrate", PassKind.Compute)
					.ReadTexture(_view.FrameResources.DdgiProbeActivity, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.DdgiProbeStateRead, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.DdgiProbeStateWrite, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.DdgiTraceIrradiance, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.DdgiIrradianceL0HistoryRead, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.DdgiIrradianceLyHistoryRead, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.DdgiIrradianceLzHistoryRead, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.DdgiIrradianceLxHistoryRead, ResourceState.ShaderResource)
					.WriteTexture(_view.FrameResources.DdgiIrradianceL0HistoryWrite, ResourceState.UnorderedAccess)
					.WriteTexture(_view.FrameResources.DdgiIrradianceLyHistoryWrite, ResourceState.UnorderedAccess)
					.WriteTexture(_view.FrameResources.DdgiIrradianceLzHistoryWrite, ResourceState.UnorderedAccess)
					.WriteTexture(_view.FrameResources.DdgiIrradianceLxHistoryWrite, ResourceState.UnorderedAccess)
					.WriteBuffer(_view.FrameResources.DdgiIrradianceEstimator, ResourceState.UnorderedAccess)
					.SetExecute(_ddgiIrradianceIntegrateExecute);

				graph.AddPass("DDGI Visibility Integrate", PassKind.Compute)
					.ReadTexture(_view.FrameResources.DdgiProbeActivity, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.DdgiProbeStateRead, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.DdgiProbeStateWrite, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.DdgiTraceVisibility, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.DdgiVisibilityHistoryRead, ResourceState.ShaderResource)
					.WriteTexture(_view.FrameResources.DdgiVisibilityHistoryWrite, ResourceState.UnorderedAccess)
					.SetExecute(_ddgiVisibilityIntegrateExecute);
			}

			graph.AddPass("Clustered Lighting Build", PassKind.Compute)
				.SetExecute(_clusteredLightingBuildExecute);
			graph.AddPass("Clustered Lighting Write", PassKind.Compute)
				.SetExecute(_clusteredLightingWriteExecute);

			// Reflections run before deferred lighting so their radiance can feed the specular
			// term directly. Shaded hit color therefore comes from the previous frame's pyramid.
			if (_view.FrameResources.ReflectionsRadiance.IsValid)
			{
				var reflectionsBuilder = graph.AddPass(
						_view.FrameResources.Config.Reflections.Mode == ReflectionMode.RayTraced
							? "Reflections (Ray Traced)"
							: "Reflections (Screen Space)",
						PassKind.Compute)
					.ReadTexture(_view.FrameResources.GBufferNormal, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.GBufferMaterial, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.GBufferVelocity, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.GBufferDepth, ResourceState.ShaderResource)
					.WriteTexture(_view.FrameResources.ReflectionsTrace, ResourceState.UnorderedAccess);
				foreach (var colorPyramidLevel in _view.FrameResources.ColorPyramidLevels)
				{
					reflectionsBuilder.ReadTexture(colorPyramidLevel, ResourceState.ShaderResource);
				}
				ReadSkyboxTextures(reflectionsBuilder);
				reflectionsBuilder.SetExecute(_reflectionsExecute);

				if (_view.FrameResources.Config.Reflections.Mode == ReflectionMode.RayTraced &&
				    _view.FrameResources.Config.Reflections.RayTracedSettings.Resolution !=
				    RayTracedReflectionResolution.Full)
				{
					graph.AddPass("Reflections Upsample", PassKind.Compute)
						.ReadTexture(_view.FrameResources.GBufferDepth, ResourceState.ShaderResource)
						.ReadTexture(_view.FrameResources.GBufferNormal, ResourceState.ShaderResource)
						.ReadTexture(_view.FrameResources.ReflectionsTrace, ResourceState.ShaderResource)
						.WriteTexture(_view.FrameResources.ReflectionsRadiance, ResourceState.UnorderedAccess)
						.SetExecute(_reflectionsUpsampleExecute);
				}
			}
			var deferredLightingBuilder = graph.AddPass("Deferred Lighting", PassKind.Compute)
				.ReadTexture(_view.FrameResources.GBufferAlbedo, ResourceState.ShaderResource)
				.ReadTexture(_view.FrameResources.GBufferNormal, ResourceState.ShaderResource)
				.ReadTexture(_view.FrameResources.GBufferMaterial, ResourceState.ShaderResource)
				.ReadTexture(_view.FrameResources.GBufferEmissive, ResourceState.ShaderResource)
				.ReadTexture(_view.FrameResources.GBufferDepth, ResourceState.ShaderResource)
				.ReadTexture(_view.FrameResources.ShadowMapDepth0, ResourceState.ShaderResource)
				.ReadTexture(_view.FrameResources.ShadowMapDepth1, ResourceState.ShaderResource)
				.ReadTexture(_view.FrameResources.ShadowMapDepth2, ResourceState.ShaderResource);
			if (_view.FrameResources.AmbientOcclusionFinal.IsValid)
			{
				deferredLightingBuilder.ReadTexture(_view.FrameResources.AmbientOcclusionFinal, ResourceState.ShaderResource);
			}
			if (_view.FrameResources.ReflectionsRadiance.IsValid)
			{
				deferredLightingBuilder.ReadTexture(_view.FrameResources.ReflectionsRadiance, ResourceState.ShaderResource);
			}
			if (_view.FrameResources.FogIntegrated.IsValid)
			{
				deferredLightingBuilder.ReadTexture(_view.FrameResources.FogIntegrated, ResourceState.ShaderResource);
			}
			if (_view.FrameResources.DdgiIrradianceL0HistoryWrite.IsValid &&
			    _view.FrameResources.DdgiIrradianceLyHistoryWrite.IsValid &&
			    _view.FrameResources.DdgiIrradianceLzHistoryWrite.IsValid &&
			    _view.FrameResources.DdgiIrradianceLxHistoryWrite.IsValid &&
			    _view.FrameResources.DdgiVisibilityHistoryWrite.IsValid &&
			    _view.FrameResources.DdgiProbeStateWrite.IsValid &&
			    _view.FrameResources.DdgiProbeActivity.IsValid &&
			    _view.FrameResources.DdgiProbeRelocationDecision.IsValid)
			{
				deferredLightingBuilder
					.ReadTexture(_view.FrameResources.DdgiIrradianceL0HistoryWrite, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.DdgiIrradianceLyHistoryWrite, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.DdgiIrradianceLzHistoryWrite, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.DdgiIrradianceLxHistoryWrite, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.DdgiVisibilityHistoryWrite, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.DdgiProbeStateWrite, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.DdgiProbeActivity, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.DdgiProbeRelocationDecision, ResourceState.ShaderResource);
				if (_view.FrameResources.WriteDdgiFinalContributionDebug)
				{
					deferredLightingBuilder.WriteTexture(_view.FrameResources.DdgiFinalContribution, ResourceState.UnorderedAccess);
				}
				if (_view.FrameResources.WriteDdgiProbeDebug)
				{
					deferredLightingBuilder
						.WriteTexture(_view.FrameResources.DdgiProbeBaseWeightDebug, ResourceState.UnorderedAccess)
						.WriteTexture(_view.FrameResources.DdgiWeightedVisibilityDebug, ResourceState.UnorderedAccess)
						.WriteTexture(_view.FrameResources.DdgiDominantProbeDebug, ResourceState.UnorderedAccess)
						.WriteTexture(_view.FrameResources.DdgiDominantProbeCoordDebug, ResourceState.UnorderedAccess)
						.WriteTexture(_view.FrameResources.DdgiProbeRelocationDebug, ResourceState.UnorderedAccess)
						.WriteTexture(_view.FrameResources.DdgiProbeRelocationDecisionDebug, ResourceState.UnorderedAccess);
				}
			}
			
			ReadSkyboxTextures(deferredLightingBuilder);
			
			deferredLightingBuilder
				.WriteTexture(_view.FrameResources.LightingBuffer, ResourceState.UnorderedAccess)
				.SetExecute(_deferredLightingExecute);

			var transparentForwardBuilder = graph.AddPass("Transparent Forward", PassKind.Graphics)
				.ReadTexture(_view.FrameResources.GBufferDepth, ResourceState.DepthWrite)
				.ReadTexture(_view.FrameResources.ShadowMapDepth0, ResourceState.ShaderResource)
				.ReadTexture(_view.FrameResources.ShadowMapDepth1, ResourceState.ShaderResource)
				.ReadTexture(_view.FrameResources.ShadowMapDepth2, ResourceState.ShaderResource)
				.WriteTexture(_view.FrameResources.LightingBuffer, ResourceState.RenderTarget);
			if (_view.FrameResources.Fsr3.TransparencyMask.IsValid)
			{
				transparentForwardBuilder.WriteTexture(_view.FrameResources.Fsr3.TransparencyMask, ResourceState.RenderTarget);
			}
			if (_view.FrameResources.DdgiProbeStateWrite.IsValid)
			{
				transparentForwardBuilder.ReadTexture(
					_view.FrameResources.DdgiProbeStateWrite,
					ResourceState.ShaderResource);
			}
			if (_view.FrameResources.FogIntegrated.IsValid)
			{
				transparentForwardBuilder.ReadTexture(_view.FrameResources.FogIntegrated, ResourceState.ShaderResource);
			}
			
			ReadSkyboxTextures(transparentForwardBuilder);
			transparentForwardBuilder.SetExecute(_transparentForwardExecute);

			if (_view.FrameResources.Config.AntiAliasing.UsesFsr3 && _view.FrameResources.Fsr3.InternalHistoryWrite.IsValid)
			{
				AddFsr3Passes(graph);
			}
			else if (_view.FrameResources.Config.AntiAliasing.Enabled && _view.FrameResources.HistoryColorWrite.IsValid)
			{
				graph.AddPass("TAA Resolve", PassKind.Compute)
					.ReadTexture(_view.FrameResources.LightingBuffer, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.GBufferVelocity, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.GBufferNormal, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.GBufferMaterial, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.GBufferDepth, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.HistoryColorRead, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.HistoryDepthRead, ResourceState.ShaderResource)
					.WriteTexture(_view.FrameResources.ResolvedSceneColor, ResourceState.UnorderedAccess)
					.SetExecute(_taaResolveExecute);
				graph.AddPass("TAA History Store", PassKind.Compute)
					.ReadTexture(_view.FrameResources.ResolvedSceneColor, ResourceState.ShaderResource)
					.ReadTexture(_view.FrameResources.GBufferDepth, ResourceState.ShaderResource)
					.WriteTexture(_view.FrameResources.HistoryColorWrite, ResourceState.UnorderedAccess)
					.WriteTexture(_view.FrameResources.HistoryDepthWrite, ResourceState.UnorderedAccess)
					.SetExecute(_taaHistoryStoreExecute);
			}

			// Capture the finished HDR scene color so next frame's reflections have shaded,
			// pre-filtered radiance to sample.
			var colorPyramidLevels = _view.FrameResources.ColorPyramidLevels ?? [];
			for (var level = 0; level < colorPyramidLevels.Length; level++)
			{
				var stage = level == 0 ? ColorPyramidPass.Stage.Copy : ColorPyramidPass.Stage.Downsample;
				var source = level == 0
					? _view.FrameResources.LightingBuffer
					: colorPyramidLevels[level - 1];
				var output = colorPyramidLevels[level];
				graph.AddPass($"Color Pyramid {level}", PassKind.Compute)
					.ReadTexture(source, ResourceState.ShaderResource)
					.WriteTexture(output, ResourceState.UnorderedAccess)
					.SetExecute(context => ExecuteColorPyramid(context, stage, source, output));
			}

			if (_view.FrameResources.BloomDownsampleLevels?.Length > 0)
			{
				graph.AddPass("Bloom Prefilter", PassKind.Compute)
					.ReadTexture(_view.FrameResources.ResolvedSceneColor, ResourceState.ShaderResource)
					.WriteTexture(_view.FrameResources.BloomDownsampleLevels[0], ResourceState.UnorderedAccess)
					.SetExecute(context => ExecuteBloom(context, BloomPass.Stage.Prefilter,
						_view.FrameResources.ResolvedSceneColor, _view.FrameResources.BloomDownsampleLevels[0], default));

				for (var level = 1; level < _view.FrameResources.BloomDownsampleLevels.Length; level++)
				{
					var source = _view.FrameResources.BloomDownsampleLevels[level - 1];
					var output = _view.FrameResources.BloomDownsampleLevels[level];
					graph.AddPass($"Bloom Downsample {level}", PassKind.Compute)
						.ReadTexture(source, ResourceState.ShaderResource)
						.WriteTexture(output, ResourceState.UnorderedAccess)
						.SetExecute(context => ExecuteBloom(context, BloomPass.Stage.Downsample, source, output, default));
				}

				for (var level = _view.FrameResources.BloomUpsampleLevels.Length - 1; level >= 0; level--)
				{
					var small = level == _view.FrameResources.BloomUpsampleLevels.Length - 1
						? _view.FrameResources.BloomDownsampleLevels[level + 1]
						: _view.FrameResources.BloomUpsampleLevels[level + 1];
					var large = _view.FrameResources.BloomDownsampleLevels[level];
					var output = _view.FrameResources.BloomUpsampleLevels[level];
					graph.AddPass($"Bloom Upsample {level}", PassKind.Compute)
						.ReadTexture(small, ResourceState.ShaderResource)
						.ReadTexture(large, ResourceState.ShaderResource)
						.WriteTexture(output, ResourceState.UnorderedAccess)
						.SetExecute(context => ExecuteBloom(context, BloomPass.Stage.Upsample, small, output, large));
				}

				var bloomResult = _view.FrameResources.BloomUpsampleLevels.Length > 0
					? _view.FrameResources.BloomUpsampleLevels[0]
					: _view.FrameResources.BloomDownsampleLevels[0];
				graph.AddPass("Bloom Composite", PassKind.Compute)
					.ReadTexture(_view.FrameResources.ResolvedSceneColor, ResourceState.ShaderResource)
					.ReadTexture(bloomResult, ResourceState.ShaderResource)
					.WriteTexture(_view.FrameResources.BloomCompositeSceneColor, ResourceState.UnorderedAccess)
					.SetExecute(_bloomCompositeExecute);
			}

			graph.AddPass("Tonemapping", PassKind.Compute)
				.ReadTexture(_view.FrameResources.BloomCompositeSceneColor.IsValid ? _view.FrameResources.BloomCompositeSceneColor : _view.FrameResources.ResolvedSceneColor, ResourceState.ShaderResource)
				.WriteTexture(_view.FrameResources.TonemappedLinearSceneColor, ResourceState.UnorderedAccess)
				.SetExecute(_tonemappingExecute);

			if (_view.FrameResources.Config.AntiAliasing.UsesCasSharpening)
			{
				graph.AddPass("CAS Sharpen", PassKind.Compute)
					.ReadTexture(_view.FrameResources.TonemappedLinearSceneColor, ResourceState.ShaderResource)
					.WriteTexture(_view.FrameResources.DisplayLinearSceneColor, ResourceState.UnorderedAccess)
					.SetExecute(_casSharpenExecute);
			}

			var copyToFinal = graph.AddPass("Copy To Final", PassKind.Compute)
				.ReadTexture(_view.FrameResources.DisplayLinearSceneColor, ResourceState.ShaderResource)
				.WriteTexture(_view.FrameResources.EncodedSceneColor, ResourceState.UnorderedAccess);
			// Only the view that owns the window's presentation writes the shared final target; any other view's
			// copy would overwrite it.
			if (_view.OwnsPresentation)
			{
				copyToFinal.WriteTexture(_sharedResources.FinalColor, ResourceState.UnorderedAccess);
			}

			copyToFinal.SetExecute(_copyToFinalExecute);

			// After tonemapping and upscaling so the outline colour reaches the
			// viewport exactly as authored, and only on EncodedSceneColor so the
			// presented game image and play mode stay clean. capture_frame reads
			// this target, which is what makes the outline verifiable.
			if (_view.FrameResources.GBufferDepth.IsValid)
			{
				graph.AddPass("Selection Outline", PassKind.Graphics)
					.ReadTexture(_view.FrameResources.GBufferDepth, ResourceState.ShaderResource)
					.WriteTexture(_view.FrameResources.EncodedSceneColor, ResourceState.RenderTarget)
					.SetExecute(_selectionOutlineExecute);
			}

			// Gameplay screen UI belongs to the game's view, not to previews or documents.
			if (_view.OwnsPresentation &&
			    ReferenceEquals(_gameplayUiFrame.Screen, UiFrameData.Empty) == false &&
			    _gameplayUiFrame.Screen.CommandCount > 0)
			{
				// Keep capture/debug output and the presented target identical. Both are BGRA8, which
				// matches the UI pipeline, and CSS colors are already authored in display space.
				graph.AddPass("Gameplay UI Screen Capture", PassKind.Graphics)
					.WriteTexture(_view.FrameResources.EncodedSceneColor, ResourceState.RenderTarget)
					.SetExecute(_gameplayScreenEncodedUiExecute);
				graph.AddPass("Gameplay UI Screen", PassKind.Graphics)
					.WriteTexture(_sharedResources.FinalColor, ResourceState.RenderTarget)
					.SetExecute(_gameplayScreenFinalUiExecute);
			}

			if (_view.FrameResources.MotionVectorDebugColor.IsValid)
			{
				graph.AddPass("Motion Vector Debug", PassKind.Compute)
					.ReadTexture(_view.FrameResources.GBufferVelocity, ResourceState.ShaderResource)
					.WriteTexture(_view.FrameResources.MotionVectorDebugColor, ResourceState.UnorderedAccess)
					.SetExecute(_motionVectorDebugExecute);
			}
		}
	}

	private void RecordSharedPresentationPasses(RenderGraph graph)
	{
		var imguiBuilder = graph.AddPass("ImGui", PassKind.Graphics)
			.WriteTexture(_sharedResources.FinalColor, ResourceState.RenderTarget);
		var selectedSceneDebugViewHandle = GetSelectedSceneDebugViewHandle();
		if (selectedSceneDebugViewHandle.IsValid)
		{
			imguiBuilder.ReadTexture(selectedSceneDebugViewHandle, ResourceState.ShaderResource);
		}
		var sceneColorDebugViewHandle = GetSceneColorDebugViewHandle();
		if (sceneColorDebugViewHandle.IsValid && sceneColorDebugViewHandle.Id != selectedSceneDebugViewHandle.Id)
		{
			imguiBuilder.ReadTexture(sceneColorDebugViewHandle, ResourceState.ShaderResource);
		}

		imguiBuilder.SetExecute(_imguiExecute);
	}

	private void ReadSkyboxTextures(RenderGraphBuilder builder)
	{
		if (_sharedResources.SkyboxEnvironment.IsValid)
		{
			builder.ReadTexture(_sharedResources.SkyboxEnvironment, ResourceState.ShaderResource);
		}
		if (_sharedResources.SkyboxIrradiance.IsValid)
		{
			builder.ReadTexture(_sharedResources.SkyboxIrradiance, ResourceState.ShaderResource);
		}
		if (_sharedResources.SkyboxPrefilter.IsValid)
		{
			builder.ReadTexture(_sharedResources.SkyboxPrefilter, ResourceState.ShaderResource);
		}
		if (_sharedResources.SkyboxBrdfLut.IsValid)
		{
			builder.ReadTexture(_sharedResources.SkyboxBrdfLut, ResourceState.ShaderResource);
		}
	}

	/// <summary>Starts resolving this frame's view outputs: every view begins the frame with none.</summary>
	public void BeginViewportResolve() => Array.Clear(_viewportTextureIds);

	/// <summary>
	/// Resolves the bound view's output texture and the render state it publishes. Runs for every view in the
	/// frame, including one that recorded no passes, so a hidden view publishes an empty state rather than
	/// keeping last frame's.
	/// </summary>
	public void PrepareSceneViewport()
	{
		if (_view.FrameResources.SceneEnabled == false)
		{
			_viewportTextureIds[_view.View.Index] = 0;
			_view.ResolvedSceneViewportState = SceneViewportRenderState.Empty;
			return;
		}

		var textureId = ResolveSceneViewportTextureId(out var activeDebugViewId);
		_viewportTextureIds[_view.View.Index] = textureId;
		_view.ResolvedSceneViewportState = new SceneViewportRenderState(
			textureId,
			_view.FrameResources.SceneFramebufferSize,
			_view.ResolvedProjection,
			_view.SceneDebugViewOptions,
			activeDebugViewId);
	}

	/// <summary>
	/// The cross-frame state for one view, created on first use. State is never shared between views: a
	/// view's temporal history, fog grid, colour pyramid and probe volume are only meaningful for the camera
	/// and target size that produced them.
	/// </summary>
	private RenderViewState GetOrCreateViewState(RenderViewId view) => _viewRegistry.GetOrCreate(view);

	/// <summary>
	/// Points the builder at <paramref name="view"/>'s state before one of its passes executes. Pass callbacks
	/// read per-view state when they run rather than when they were recorded, so with several views in one
	/// graph each pass has to be run against its own view. A shared pass carries no view and leaves the
	/// binding as it is.
	/// </summary>
	internal void BindView(RenderViewId view)
	{
		if (view.IsValid && view != _view.View)
		{
			_view = _viewRegistry.GetOrCreate(view);
		}
	}

	/// <summary>
	/// Releases a destroyed view's draw resources: its indirect command sets in every pass, retired rather than
	/// disposed because frames in flight may still execute them.
	/// </summary>
	public void ReleaseViewDrawResources(RenderViewId view)
	{
		if (view.IsValid == false || view == RenderViewId.Primary)
		{
			return;
		}

		var device = _renderer.GetGfxDevice();
		var commandSets = new List<SharedDrawIndirectCommandSet>();
		commandSets.AddRange(_gpuDrawPass.TakeViewCommandSets(view.Index));
		commandSets.AddRange(_transparentForwardPass.TakeViewCommandSets(view.Index));
		commandSets.AddRange(_shadowMapPass.TakeViewCommandSets(view.Index));
		_gpuDrawPass.ForgetIndirectCommandSets(commandSets);
		foreach (var commandSet in commandSets)
		{
			device.Retire(commandSet, $"Indirect command set for {view}");
		}
	}

	/// <summary>Exposes a view's state so tests can assert that views do not share history.</summary>
	internal RenderViewState GetViewStateForTest(RenderViewId view) => GetOrCreateViewState(view);

	/// <summary>
	/// Retires a closed view's GPU resources and forgets it. The primary view is kept, because the builder
	/// always has a view selected; closing it would leave nothing to record into.
	/// </summary>
	public void ReleaseView(RenderViewId view)
	{
		if (view == RenderViewId.Primary || _viewRegistry.TryGet(view, out var state) == false)
		{
			return;
		}

		_viewRegistry.Release(view, _renderer.GetGfxDevice());
		if (ReferenceEquals(_view, state))
		{
			_view = GetOrCreateViewState(RenderViewId.Primary);
		}
	}

	public RenderGraphResourceHandle GetFinalColorHandle() => _sharedResources.FinalColor;
	public RenderGraphResourceHandle GetCaptureColorHandle() => _view.FrameResources.EncodedSceneColor;

	private void RegisterSceneDebugView(
		string id,
		string label,
		RenderGraphResourceHandle handle,
		SceneDebugViewKind kind)
	{
		if (string.IsNullOrWhiteSpace(id))
		{
			throw new ArgumentException("Debug view id cannot be empty.", nameof(id));
		}

		if (string.IsNullOrWhiteSpace(label))
		{
			throw new ArgumentException("Debug view label cannot be empty.", nameof(label));
		}

		_view.SceneDebugViews.Add(new SceneDebugViewRegistration(id, label, handle, kind));
	}

	private RenderGraphResourceHandle CreateDdgiDebugTexture(Int2 size)
	{
		return _resources.CreateTransientTexture(new TextureDescriptor(
			size.X,
			size.Y,
			TextureFormat.Rgba16Float,
			TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
			new ColorRGBA(0.0f, 0.0f, 0.0f, 1.0f)));
	}

	private RenderGraphResourceHandle CreateBloomTexture(Int2 size) => _resources.CreateTransientTexture(new TextureDescriptor(
		size.X, size.Y, TextureFormat.Rgba16Float,
		TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
		new ColorRGBA(0.0f, 0.0f, 0.0f, 1.0f)));

	private static int GetBloomLevelCount(Int2 size, BloomQuality quality)
	{
		var maximum = quality switch { BloomQuality.Low => 4, BloomQuality.Medium => 5, _ => 6 };
		var count = 0;
		var levelSize = new Int2(Math.Max(1, (size.X + 1) / 2), Math.Max(1, (size.Y + 1) / 2));
		while (count < maximum)
		{
			count++;
			if (levelSize.X == 1 && levelSize.Y == 1) break;
			levelSize = new Int2(Math.Max(1, (levelSize.X + 1) / 2), Math.Max(1, (levelSize.Y + 1) / 2));
		}
		return count;
	}

	private SceneDebugViewOption[] BuildSceneDebugViewOptions()
	{
		if (_view.SceneDebugViews.Count == 0)
		{
			return Array.Empty<SceneDebugViewOption>();
		}

		var options = new SceneDebugViewOption[_view.SceneDebugViews.Count];
		for (var i = 0; i < _view.SceneDebugViews.Count; i++)
		{
			var debugView = _view.SceneDebugViews[i];
			options[i] = new SceneDebugViewOption(debugView.Id, debugView.Label, debugView.Kind);
		}

		return options;
	}

	private RenderGraphResourceHandle GetSelectedSceneDebugViewHandle()
	{
		var resolvedView = GetResolvedSceneDebugView();
		return resolvedView?.Handle ?? default;
	}

	private RenderGraphResourceHandle GetSceneColorDebugViewHandle()
	{
		return TryGetSceneDebugView(SceneDebugViewIds.FinalColor, out var sceneColorView)
			? sceneColorView.Handle
			: default;
	}

	private SceneDebugViewRegistration? GetResolvedSceneDebugView()
	{
		if (TryGetSceneDebugView(_view.RequestedSceneDebugViewId, out var requestedView))
		{
			return requestedView;
		}

		if (TryGetSceneDebugView(SceneDebugViewIds.FinalColor, out var sceneColorView))
		{
			return sceneColorView;
		}

		return null;
	}

	private bool TryGetSceneDebugView(string id, out SceneDebugViewRegistration view)
	{
		for (var i = 0; i < _view.SceneDebugViews.Count; i++)
		{
			if (string.Equals(_view.SceneDebugViews[i].Id, id, StringComparison.Ordinal))
			{
				view = _view.SceneDebugViews[i];
				return true;
			}
		}

		view = default;
		return false;
	}

	private DescriptorHandle ResolveSceneDebugTextureHandle(SceneDebugViewRegistration debugView)
	{
		if (debugView.Handle.IsValid == false)
		{
			return DescriptorHandle.Invalid;
		}

		var texture = _resources.GetTexture(debugView.Handle);
		return debugView.Kind == SceneDebugViewKind.Depth && texture.DepthShaderResourceView.IsValid
			? texture.DepthShaderResourceView
			: texture.ShaderResourceView;
	}

	private nint ResolveSceneViewportTextureId(out string activeDebugViewId)
	{
		var resolvedView = GetResolvedSceneDebugView();
		if (resolvedView.HasValue)
		{
			var descriptorHandle = ResolveSceneDebugTextureHandle(resolvedView.Value);
			if (descriptorHandle.IsValid)
			{
				activeDebugViewId = resolvedView.Value.Id;
				return (nint)descriptorHandle.Value;
			}
		}

		if (TryGetSceneDebugView(SceneDebugViewIds.FinalColor, out var sceneColorView))
		{
			var fallbackTextureId = ResolveSceneDebugTextureHandle(sceneColorView);
			activeDebugViewId = SceneDebugViewIds.FinalColor;
			return fallbackTextureId.IsValid ? (nint)fallbackTextureId.Value : 0;
		}

		activeDebugViewId = SceneDebugViewIds.FinalColor;
		return 0;
	}

	private static string NormalizeSceneDebugViewId(string? requestedDebugViewId)
	{
		return string.IsNullOrWhiteSpace(requestedDebugViewId)
			? SceneDebugViewIds.FinalColor
			: requestedDebugViewId;
	}

	private static bool IsDdgiFinalContributionDebugView(string? requestedDebugViewId)
	{
		return string.Equals(
			NormalizeSceneDebugViewId(requestedDebugViewId),
			SceneDebugViewIds.DdgiFinalContribution,
			StringComparison.Ordinal);
	}

	private static bool IsMotionVectorDebugView(string? requestedDebugViewId)
	{
		return string.Equals(
			NormalizeSceneDebugViewId(requestedDebugViewId),
			SceneDebugViewIds.MotionVectors,
			StringComparison.Ordinal);
	}

	private static bool IsDdgiProbeDebugView(string? requestedDebugViewId)
	{
		var debugViewId = NormalizeSceneDebugViewId(requestedDebugViewId);
		return string.Equals(debugViewId, SceneDebugViewIds.DdgiProbeBaseWeight, StringComparison.Ordinal) ||
		       string.Equals(debugViewId, SceneDebugViewIds.DdgiWeightedVisibility, StringComparison.Ordinal) ||
		       string.Equals(debugViewId, SceneDebugViewIds.DdgiDominantProbe, StringComparison.Ordinal) ||
		       string.Equals(debugViewId, SceneDebugViewIds.DdgiDominantProbeCoord, StringComparison.Ordinal) ||
		       string.Equals(debugViewId, SceneDebugViewIds.DdgiProbeRelocation, StringComparison.Ordinal) ||
		       string.Equals(debugViewId, SceneDebugViewIds.DdgiProbeRelocationDecision, StringComparison.Ordinal);
	}

	/// <summary>
	/// Rewrites the UI frame's viewport sentinels to the outputs resolved this frame. Once per frame, after every
	/// view has been prepared, because the UI frame holds the sentinels of all views together.
	/// </summary>
	public void ResolveUiViewportTextures() => ResolveViewportTextureIds(_uiFrame, _viewportTextureIds);

	/// <summary>
	/// Rewrites every viewport sentinel in the UI frame to the output its view resolved to this frame. The UI
	/// frame is built on the game thread, before the render thread knows those texture ids, so each view's
	/// image carries a sentinel until here. A view that resolved to nothing is left at zero, which the
	/// backends draw with their fallback texture.
	/// </summary>
	private static void ResolveViewportTextureIds(UiFrameData uiFrame, nint[] textureIdsByViewIndex)
	{
		if (ReferenceEquals(uiFrame, UiFrameData.Empty) || uiFrame.CommandCount == 0)
		{
			return;
		}

		for (var i = 0; i < uiFrame.CommandCount; i++)
		{
			var command = uiFrame.Commands[i];
			if (UiTextureIds.TryGetViewport(command.TextureId, out var view) == false)
			{
				continue;
			}

			uiFrame.Commands[i] = new UiDrawCommand(
				command.ElemCount,
				command.IdxOffset,
				command.VtxOffset,
				command.ClipRect,
				textureIdsByViewIndex[view.Index]);
		}
	}

	/// <summary>
	/// Shared pass: applies every set-up view's draw-database changes to the shared draw tables, tagging each draw
	/// with the view that owns it. Each view's changes are also copied for its own ray-tracing update, which runs
	/// among that view's passes.
	/// </summary>
	private void ExecuteGpuDrawUpdate(RenderGraphContext context)
	{
		_frameDrawSources.Clear();
		for (var i = 0; i < _frameViews.Count; i++)
		{
			var view = _frameViews[i];
			if (context.FrameSnapshot.TryGetView(view, out var viewSnapshot) == false)
			{
				continue;
			}

			var viewState = GetOrCreateViewState(view);
			viewSnapshot.GpuDrawDatabase.CopyUpdates(viewState.RayTracingUpdates);

			// Before RecordUpdate, not after. RecordUpdate would otherwise upload the instance through
			// the ordinary mesh path, which allocates a vertex range but leaves the shared bind-pose
			// source mesh unuploaded — and the skinning shader reads its bind pose from there.
			EnsureSkinnedInstanceResources(viewSnapshot.SkinningPackets);
			_frameDrawSources.Add(new GpuDrawSource(view, viewSnapshot.GpuDrawDatabase));
		}

		if (_frameDrawSources.Count == 0)
		{
			return;
		}

		// Graphics bindings require these buffers even when no meshes are skinned.
		var device = _renderer.GetGfxDevice();
		_skinningPass.EnsureResources(device);
		_gpuDrawResources.SkinVertexBuffer = _skinningPass.SkinVertexBuffer;
		_gpuDrawResources.BoneMatrixBuffer = _skinningPass.BoneMatrixBuffer;
		_gpuDrawResources.SkinnedInstanceBuffer = _skinningPass.SkinnedInstanceBuffer;

		_gpuDrawPass.RecordUpdate(context, CollectionsMarshal.AsSpan(_frameDrawSources));
	}

	/// <summary>
	/// The view's own part of the draw update: skinning its instances and updating its acceleration structures.
	/// Both still use renderer-wide resources — see the multi-viewport architecture notes.
	/// </summary>
	private void ExecuteGpuDrawViewUpdate(RenderGraphContext context)
	{
		var device = _renderer.GetGfxDevice();
		var skinningPackets = context.ViewSnapshot.SkinningPackets;
		_skinningPass.Record(
			context.CommandList,
			device,
			_renderer,
			skinningPackets,
			context.ViewSnapshot.BoneMatrices,
			context.GpuDrawDatabase);

		_gpuDrawResources.SkinVertexBuffer = _skinningPass.SkinVertexBuffer;
		_gpuDrawResources.BoneMatrixBuffer = _skinningPass.BoneMatrixBuffer;
		_gpuDrawResources.SkinnedInstanceBuffer = _skinningPass.SkinnedInstanceBuffer;

		if (RequiresRayTracingScene(_view.FrameResources.Config))
		{
			// Deliberately after skinning: acceleration structures are built over the vertices this
			// frame produced, so a ray-traced reflection shows the pose being drawn rather than the
			// previous one.
			for (var i = 0; i < skinningPackets.Count; i++)
			{
				_rayTracingSceneResources.QueueSkinnedInstanceRebuild(skinningPackets[i].InstanceMesh);
			}

			_rayTracingSceneResources.RecordUpdate(context, _renderer, _view.RayTracingUpdates);
		}
	}

	/// <summary>
	/// Claims each skinned instance's private vertex range, and makes its shared bind-pose source
	/// resident, before anything else can upload the instance through the ordinary mesh path.
	/// </summary>
	private void EnsureSkinnedInstanceResources(IReadOnlyList<SkinningPacket> packets)
	{
		for (var i = 0; i < packets.Count; i++)
		{
			_renderer.EnsureSkinnedInstanceResources(packets[i].InstanceMesh);
		}
	}

	private void ExecuteGpuDrawCullShadow(RenderGraphContext context)
	{
		var sceneData = context.SceneData;
		_shadowMapPass.PrepareFrame(sceneData, _view.FrameResources.Config.ShadowMaps);
		var shadowData = _shadowMapPass.GetCurrentFrameData();
		if (shadowData.Enabled == false)
		{
			return;
		}

		Span<Matrix4x4> cascadeViewProjections = stackalloc Matrix4x4[ShadowMapPass.MaxCascadeCount];
		Span<Vector4> casterPlanes = stackalloc Vector4[ShadowMapPass.MaxCascadeCount * FrustumCulling.MaxPlaneCount];
		Span<int> casterPlaneCounts = stackalloc int[ShadowMapPass.MaxCascadeCount];
		for (var cascadeIndex = 0; cascadeIndex < shadowData.CascadeCount; cascadeIndex++)
		{
			cascadeViewProjections[cascadeIndex] = shadowData.GetCascadeViewProjection(cascadeIndex);
			casterPlaneCounts[cascadeIndex] = _shadowMapPass.BuildCasterCullingPlanes(
				sceneData, cascadeIndex, _view.FrameResources.Config.ShadowMaps.TightCasterCulling,
				casterPlanes.Slice(cascadeIndex * FrustumCulling.MaxPlaneCount, FrustumCulling.MaxPlaneCount));
		}

		_gpuDrawPass.RecordCullForViews(
			context,
			cascadeViewProjections[..shadowData.CascadeCount],
			sceneData.CameraOrigin,
			useShadowBuffers: true,
			DrawPassParticipation.ShadowCaster,
			casterPlanes[..(shadowData.CascadeCount * FrustumCulling.MaxPlaneCount)],
			casterPlaneCounts[..shadowData.CascadeCount]);

		// Encoding and compaction both belong here rather than in the shadow pass: compaction is compute
		// work, and it has to complete before the render pass that executes its output begins.
		var device = _renderer.GetGfxDevice();
		for (var cascadeIndex = 0; cascadeIndex < shadowData.CascadeCount; cascadeIndex++)
		{
			EnsureShadowIndirectCommands(context, device, cascadeIndex);
			var compacted = _gpuDrawPass.RecordIndirectCompaction(
				context,
				_shadowMapPass.GetIndirectCommandSet(cascadeIndex, _gpuDrawResources.ActiveViewIndex),
				DrawPassParticipation.ShadowCaster,
				_gpuDrawResources.ShadowDrawArgsBuffer,
				GpuDrawResources.GetShadowDrawArgsOffsetBytes(cascadeIndex),
				lane => _shadowMapPass.HasIndirectLane(cascadeIndex, lane));
			_shadowMapPass.SetCompactedExecution(cascadeIndex, compacted);
		}
	}

	private void EnsureShadowIndirectCommands(RenderGraphContext context, IGfxDevice device, int cascadeIndex)
	{
		_shadowMapPass.EnsureIndirectResources(device, cascadeIndex, _gpuDrawResources.ActiveViewIndex);
		_gpuDrawPass.EnsureIndirectCommandsForPass(
			context.GpuDrawDatabase,
			_shadowMapPass.GetIndirectCommandSet(cascadeIndex, _gpuDrawResources.ActiveViewIndex),
			DrawPassParticipation.ShadowCaster,
			SharedDrawIndirectEncodeResources.FromGpuDrawResources(
				_gpuDrawResources,
				_gpuDrawResources.ShadowDrawArgsBuffer,
				GpuDrawResources.GetShadowDrawArgsOffsetBytes(cascadeIndex),
				_view.FrameResources.Config.ShadowMaps.WeldShadowVertices ? MeshIndexStream.ShadowOpaque : MeshIndexStream.Main),
			lane => _shadowMapPass.HasIndirectLane(cascadeIndex, lane),
			lane => _shadowMapPass.GetBufferBindings(cascadeIndex, lane),
			lane => _shadowMapPass.GetPassBindingSet(cascadeIndex, lane, _gpuDrawResources));
	}

	private void ExecuteShadowMap(RenderGraphContext context)
	{
		var device = _renderer.GetGfxDevice();
		var cascadeCount = _shadowMapPass.GetCurrentFrameData().CascadeCount;
		for (var cascadeIndex = 0; cascadeIndex < cascadeCount; cascadeIndex++)
		{
			var shadowMapHandle = GetShadowMapHandle(_view.FrameResources, cascadeIndex);
			var depthTexture = context.GetTexture(shadowMapHandle);
			// Commands were encoded and compacted by the shadow cull pass; this pass only executes them.
			var config = _shadowMapPass.BuildConfig(
				context,
				depthTexture,
				device,
				_gpuDrawResources,
				cascadeIndex);
			_shadowMapPass.Record(context, in config);
		}
	}

	private void ExecuteGpuDrawCullCamera(RenderGraphContext context)
	{
		_gpuDrawPass.RecordCull(context, context.SceneData);
		_gpuDrawPass.RecordGBufferIndirectCompaction(context);
	}

	private void ExecuteSkyboxEnvironment(RenderGraphContext context)
	{
		_skyboxPass.RecordEnvironment(context, _view.FrameResources.Config.SkyboxConfig);
	}

	private void ExecuteSkyboxIrradiance(RenderGraphContext context)
	{
		_skyboxPass.RecordIrradiance(context);
	}

	private void ExecuteSkyboxPrefilter(RenderGraphContext context)
	{
		_skyboxPass.RecordPrefilter(context);
	}

	private void ExecuteSkyboxBrdf(RenderGraphContext context)
	{
		_skyboxPass.RecordBrdfLut(context);
	}

	private void ExecuteGBuffer(RenderGraphContext context)
	{
		var albedoHandle = _view.FrameResources.DecalSourceGBufferAlbedo.IsValid
			? _view.FrameResources.DecalSourceGBufferAlbedo
			: _view.FrameResources.GBufferAlbedo;
		var normalHandle = _view.FrameResources.DecalSourceGBufferNormal.IsValid
			? _view.FrameResources.DecalSourceGBufferNormal
			: _view.FrameResources.GBufferNormal;
		var materialHandle = _view.FrameResources.DecalSourceGBufferMaterial.IsValid
			? _view.FrameResources.DecalSourceGBufferMaterial
			: _view.FrameResources.GBufferMaterial;
		var emissiveHandle = _view.FrameResources.DecalSourceGBufferEmissive.IsValid
			? _view.FrameResources.DecalSourceGBufferEmissive
			: _view.FrameResources.GBufferEmissive;
		var albedoTexture = context.GetTexture(albedoHandle);
		var normalTexture = context.GetTexture(normalHandle);
		var materialTexture = context.GetTexture(materialHandle);
		var emissiveTexture = context.GetTexture(emissiveHandle);
		var depthTexture = context.GetTexture(_view.FrameResources.GBufferDepth);
		_gpuDrawPass.EnsureGBufferIndirectCommands(context);
		var bucketList = _gpuDrawPass.BuildGBufferBuckets();

		var gbufferConfig = new GBufferPassConfig
		{
			FramebufferWidth = _view.FrameResources.SceneFramebufferSize.X,
			FramebufferHeight = _view.FrameResources.SceneFramebufferSize.Y,
			AlbedoTarget = albedoTexture,
			NormalTarget = normalTexture,
			MaterialTarget = materialTexture,
			EmissiveTarget = emissiveTexture,
			VelocityTarget = context.GetTexture(_view.FrameResources.GBufferVelocity),
			DepthTarget = depthTexture,
			AlbedoClearColor = new(0.392f, 0.584f, 0.929f, 1.0f),
			EmissiveClearColor = new(0.0f, 0.0f, 0.0f, 1.0f),
			NormalClearColor = new(0.5f, 0.5f, 1.0f, 1.0f),
			MaterialClearColor = new(0.0f, 0.0f, 0.0f, 1.0f),
			VelocityClearColor = new(0.0f, 0.0f, 0.0f, 0.0f),
			DepthClearValue = 1.0f,
			InstanceBuffer = _gpuDrawResources.InstanceBuffer,
			MaterialBuffer = _gpuDrawResources.MaterialBuffer,
			TerrainMaterialBuffer = _gpuDrawResources.TerrainMaterialBuffer,
			TerrainLayerBuffer = _gpuDrawResources.TerrainLayerBuffer,
			DrawArgsBuffer = _gpuDrawResources.DrawArgsBuffer,
			DrawCountPerBucketBuffer = _gpuDrawResources.DrawCountPerBucketBuffer,
			DrawExecutionRangePerBucketBuffer = _gpuDrawResources.DrawExecutionRangePerBucketBuffer,
			MaterialGenerationBuffer = _gpuDrawResources.MaterialGenerationBuffer,
			Buckets = bucketList.ToArray(),
			FallbackMaxCommandCount = _gpuDrawResources.ActiveDrawCommandUpperBound,
			IndirectCommandSlot = _gpuDrawResources.ActiveIndirectCommandSlot,
			CompactedExecutionRangeBuffer = _gpuDrawPass.GBufferCompactedExecutionRangeBuffer,
			PackedVertexBuffer = _gpuDrawResources.PackedMeshVertexBuffer,
			PackedIndexBuffer = _gpuDrawResources.PackedMeshIndexBuffer,
			PackedVertexStride = _gpuDrawResources.PackedMeshVertexStride,
			CameraLayout = _gpuDrawResources.GBufferCameraLayout,
			CameraBuffer = _gpuDrawResources.CameraBuffer,
			SkyboxEnvironment = DescriptorHandle.Invalid,
			SkyboxSampler = DescriptorHandle.Invalid
		};

		GBufferPass.Record(context, gbufferConfig, context.SceneData);
	}

	private void ExecuteScreenSpaceDecal(RenderGraphContext context)
	{
		var config = _screenSpaceDecalPass.BuildConfig(
			context,
			_view.FrameResources,
			_renderer.GetGfxDevice(),
			_gpuDrawResources,
			context.SceneData);
		_screenSpaceDecalPass.Record(context, in config, context.SceneData);
	}

	private void ExecuteSelectionOutline(RenderGraphContext context)
	{
		var config = _selectionOutlinePass.BuildConfig(
			context,
			_view.FrameResources,
			_renderer.GetGfxDevice());
		_selectionOutlinePass.Record(context, in config, context.SceneData);
	}

	private void ExecuteGBufferDecalSeed(RenderGraphContext context)
	{
		var config = _gBufferDecalSeedPass.BuildConfig(
			context,
			_view.FrameResources,
			_renderer.GetGfxDevice());
		_gBufferDecalSeedPass.Record(context, in config);
	}

	private void ExecuteDeferredLighting(RenderGraphContext context)
	{
		var config = _deferredLightingPass.BuildConfig(
			context,
			_view.FrameResources,
			_sharedResources,
			_renderer.GetGfxDevice(),
			_gpuDrawResources,
			GetShadowFrameData(),
			context.SceneData);
		_deferredLightingPass.Record(context, ref config, context.SceneData);
	}

	private ShadowFrameData GetShadowFrameData() =>
		_view.FrameResources.Config.ShadowMaps.Enabled
			? _shadowMapPass.GetCurrentFrameData()
			: ShadowMapPass.GetDisabledFrameData(_view.FrameResources.Config.ShadowMaps);

	private void ExecuteVolumetricFog(RenderGraphContext context, VolumetricFogStage stage)
	{
		var device = _renderer.GetGfxDevice();
		if (stage == VolumetricFogStage.Inject)
		{
			_volumetricFogPass.PrepareFrame(
				context,
				_view.FrameResources,
				device,
				_gpuDrawResources,
				GetShadowFrameData(),
				_view.FrameResources.FogHistoryValid);
		}
		var config = _volumetricFogPass.BuildConfig(
			context,
			_view.FrameResources,
			_sharedResources,
			device,
			_gpuDrawResources,
			stage);
		_volumetricFogPass.Record(context, stage, in config);
	}

	private void ExecuteReflections(RenderGraphContext context)
	{
		var isRayTraced = _view.FrameResources.Config.Reflections.Mode == ReflectionMode.RayTraced;
		var config = _reflectionsPass.BuildConfig(
			context,
			_view.FrameResources,
			_sharedResources,
			_renderer.GetGfxDevice(),
			_renderer,
			_gpuDrawResources,
			isRayTraced ? _rayTracingSceneResources : null);
		_reflectionsPass.Record(context, in config, context.SceneData);
	}

	private void ExecuteReflectionsUpsample(RenderGraphContext context)
	{
		var config = _reflectionsUpsamplePass.BuildConfig(
			context,
			_view.FrameResources,
			_renderer.GetGfxDevice());
		_reflectionsUpsamplePass.Record(context, in config, context.SceneData);
	}

	private void ExecuteColorPyramid(
		RenderGraphContext context,
		ColorPyramidPass.Stage stage,
		RenderGraphResourceHandle source,
		RenderGraphResourceHandle output)
	{
		var config = _colorPyramidPass.BuildConfig(
			context,
			_renderer.GetGfxDevice(),
			stage,
			source,
			output);
		_colorPyramidPass.Record(context, stage, in config);
	}

	private void AddFsr3Passes(RenderGraph graph)
	{
		var fsr = _view.FrameResources.Fsr3;
		var size = _view.FrameResources.SceneFramebufferSize;
		AddFsr3Clear(graph, "FSR3 Clear Reconstructed Depth", fsr.ReconstructedPrevNearestDepth,
			size, BitConverter.SingleToUInt32Bits(1.0f), true);
		AddFsr3Clear(graph, "FSR3 Clear Luma SPD Counter", fsr.LumaSpdAtomic, new Int2(1, 1), 0u, true);
		AddFsr3Clear(graph, "FSR3 Clear Shading SPD Counter", fsr.ShadingSpdAtomic, new Int2(1, 1), 0u, true);
		for (var i = 0; i < fsr.LumaSpdMips.Length; i++)
		{
			var mipSize = GetFsr3MipSize(size, i);
			AddFsr3Clear(graph, $"FSR3 Clear Luma Mip {i}", fsr.LumaSpdMips[i], mipSize, 0u, false);
		}
		var shadingSize = GetFsr3ShadingChangeSize(size);
		for (var i = 0; i < fsr.ShadingSpdMips.Length; i++)
		{
			AddFsr3Clear(graph, $"FSR3 Clear Shading Mip {i}", fsr.ShadingSpdMips[i],
				GetFsr3MipSize(shadingSize, i), 0u, false);
		}
		if (!fsr.HistoryValid || _view.ResetTaaHistoryThisFrame)
		{
			AddFsr3Clear(graph, "FSR3 Clear Frame Info", fsr.FrameInfo, new Int2(1, 1), 0u, false);
			AddFsr3Clear(graph, "FSR3 Clear Internal History", fsr.InternalHistoryRead, size, 0u, false);
			AddFsr3Clear(graph, "FSR3 Clear Luma History", fsr.LumaHistoryRead, size, 0u, false);
			AddFsr3Clear(graph, "FSR3 Clear Previous Luma", fsr.CurrentLumaRead, size, 0u, false);
			AddFsr3Clear(graph, "FSR3 Clear Accumulation", fsr.AccumulationRead, size, 0u, false);
		}

		graph.AddPass("FSR3 Prepare Inputs", PassKind.Compute)
			.ReadTexture(_view.FrameResources.LightingBuffer, ResourceState.ShaderResource)
			.ReadTexture(_view.FrameResources.GBufferDepth, ResourceState.ShaderResource)
			.ReadTexture(_view.FrameResources.GBufferVelocity, ResourceState.ShaderResource)
			.WriteTexture(fsr.DilatedMotionVectors, ResourceState.UnorderedAccess)
			.WriteTexture(fsr.DilatedDepth, ResourceState.UnorderedAccess)
			.WriteTexture(fsr.FarthestDepth, ResourceState.UnorderedAccess)
			.WriteTexture(fsr.CurrentLumaWrite, ResourceState.UnorderedAccess)
			.WriteTexture(fsr.ReconstructedPrevNearestDepth, ResourceState.UnorderedAccess)
			.SetExecute(ExecuteFsr3PrepareInputs);

		var lumaPyramid = graph.AddPass("FSR3 Luma Pyramid", PassKind.Compute)
			.ReadTexture(fsr.CurrentLumaWrite, ResourceState.ShaderResource)
			.ReadTexture(fsr.FarthestDepth, ResourceState.ShaderResource)
			.WriteTexture(fsr.FarthestDepthMip1, ResourceState.UnorderedAccess)
			.WriteTexture(fsr.FrameInfo, ResourceState.UnorderedAccess)
			.WriteTexture(fsr.LumaSpdAtomic, ResourceState.UnorderedAccess);
		foreach (var mip in fsr.LumaSpdMips) lumaPyramid.WriteTexture(mip, ResourceState.UnorderedAccess);
		lumaPyramid.SetExecute(ExecuteFsr3LumaPyramid);

		var shadingPyramid = graph.AddPass("FSR3 Shading Change Pyramid", PassKind.Compute)
			.ReadTexture(fsr.CurrentLumaWrite, ResourceState.ShaderResource)
			.ReadTexture(fsr.CurrentLumaRead, ResourceState.ShaderResource)
			.ReadTexture(fsr.DilatedMotionVectors, ResourceState.ShaderResource)
			.ReadTexture(fsr.FrameInfo, ResourceState.ShaderResource)
			.WriteTexture(fsr.ShadingSpdAtomic, ResourceState.UnorderedAccess);
		foreach (var mip in fsr.ShadingSpdMips) shadingPyramid.WriteTexture(mip, ResourceState.UnorderedAccess);
		shadingPyramid.SetExecute(ExecuteFsr3ShadingChangePyramid);

		var shadingChange = graph.AddPass("FSR3 Shading Change", PassKind.Compute)
			.WriteTexture(fsr.ShadingChange, ResourceState.UnorderedAccess);
		foreach (var mip in fsr.ShadingSpdMips) shadingChange.ReadTexture(mip, ResourceState.ShaderResource);
		shadingChange.SetExecute(ExecuteFsr3ShadingChange);

		// Prepare Reactivity only writes pixels that acquire a new thin-feature lock. NewLocks is
		// transient and may alias an earlier full-resolution FSR3 intermediate, so its untouched
		// pixels are not implicitly zero even though its descriptor has a zero clear colour. Clear
		// at the lifetime boundary, after those earlier intermediates have finished using the alias.
		// Otherwise their depth/luma payload is read as a positive lock and stale colour history is
		// protected from rectification, producing bright trails behind moving opaque geometry.
		AddFsr3Clear(graph, "FSR3 Clear New Locks", fsr.NewLocks, size, 0u, false);

		graph.AddPass("FSR3 Prepare Reactivity", PassKind.Compute)
			.ReadTexture(fsr.ReconstructedPrevNearestDepth, ResourceState.UnorderedAccess)
			.ReadTexture(fsr.DilatedMotionVectors, ResourceState.ShaderResource)
			.ReadTexture(fsr.DilatedDepth, ResourceState.ShaderResource)
			.ReadTexture(_view.FrameResources.GBufferDepth, ResourceState.ShaderResource)
			.ReadTexture(_view.FrameResources.GBufferMaterial, ResourceState.ShaderResource)
			.ReadTexture(fsr.TransparencyMask, ResourceState.ShaderResource)
			.ReadTexture(fsr.AccumulationRead, ResourceState.ShaderResource)
			.ReadTexture(fsr.ShadingChange, ResourceState.ShaderResource)
			.ReadTexture(fsr.CurrentLumaWrite, ResourceState.ShaderResource)
			.ReadTexture(fsr.FrameInfo, ResourceState.ShaderResource)
			.WriteTexture(fsr.AccumulationWrite, ResourceState.UnorderedAccess)
			.WriteTexture(fsr.DilatedReactiveMasks, ResourceState.UnorderedAccess)
			.WriteTexture(fsr.NewLocks, ResourceState.UnorderedAccess)
			.SetExecute(ExecuteFsr3PrepareReactivity);

		graph.AddPass("FSR3 Luma Instability", PassKind.Compute)
			.ReadTexture(fsr.FrameInfo, ResourceState.ShaderResource)
			.ReadTexture(fsr.DilatedReactiveMasks, ResourceState.ShaderResource)
			.ReadTexture(fsr.DilatedMotionVectors, ResourceState.ShaderResource)
			.ReadTexture(fsr.LumaHistoryRead, ResourceState.ShaderResource)
			.ReadTexture(fsr.FarthestDepthMip1, ResourceState.ShaderResource)
			.ReadTexture(fsr.CurrentLumaWrite, ResourceState.ShaderResource)
			.WriteTexture(fsr.LumaHistoryWrite, ResourceState.UnorderedAccess)
			.WriteTexture(fsr.LumaInstability, ResourceState.UnorderedAccess)
			.SetExecute(ExecuteFsr3LumaInstability);

		graph.AddPass("FSR3 Accumulate", PassKind.Compute)
			.ReadTexture(fsr.FrameInfo, ResourceState.ShaderResource)
			.ReadTexture(_view.FrameResources.LightingBuffer, ResourceState.ShaderResource)
			.ReadTexture(fsr.DilatedMotionVectors, ResourceState.ShaderResource)
			.ReadTexture(fsr.DilatedReactiveMasks, ResourceState.ShaderResource)
			.ReadTexture(fsr.FarthestDepthMip1, ResourceState.ShaderResource)
			.ReadTexture(fsr.LumaInstability, ResourceState.ShaderResource)
			.ReadTexture(fsr.NewLocks, ResourceState.UnorderedAccess)
			.ReadTexture(fsr.InternalHistoryRead, ResourceState.ShaderResource)
			.WriteTexture(fsr.InternalHistoryWrite, ResourceState.UnorderedAccess)
			.SetExecute(ExecuteFsr3Accumulate);

		graph.AddPass("FSR3 RCAS", PassKind.Compute)
			.ReadTexture(fsr.InternalHistoryWrite, ResourceState.ShaderResource)
			.ReadTexture(fsr.FrameInfo, ResourceState.ShaderResource)
			.WriteTexture(_view.FrameResources.ResolvedSceneColor, ResourceState.UnorderedAccess)
			.SetExecute(ExecuteFsr3Rcas);

		if (GraphicsConfig.Fsr3DebugViewEnabled)
		{
			graph.AddPass("FSR3 Debug View", PassKind.Compute)
				.ReadTexture(fsr.DilatedReactiveMasks, ResourceState.ShaderResource)
				.ReadTexture(fsr.DilatedMotionVectors, ResourceState.ShaderResource)
				.ReadTexture(fsr.DilatedDepth, ResourceState.ShaderResource)
				.ReadTexture(fsr.InternalHistoryWrite, ResourceState.ShaderResource)
				.ReadTexture(fsr.CurrentLumaWrite, ResourceState.ShaderResource)
				.ReadTexture(fsr.CurrentLumaRead, ResourceState.ShaderResource)
				.ReadTexture(fsr.FrameInfo, ResourceState.ShaderResource)
				.WriteTexture(_view.FrameResources.ResolvedSceneColor, ResourceState.UnorderedAccess)
				.SetExecute(ExecuteFsr3DebugView);
		}
	}

	private void AddFsr3Clear(RenderGraph graph, string name, RenderGraphResourceHandle texture,
		Int2 size, uint valueBits, bool uintTexture)
	{
		graph.AddPass(name, PassKind.Compute)
			.WriteTexture(texture, ResourceState.UnorderedAccess)
			.SetExecute(context => _passSet.Fsr3ClearPass.Record(context, _renderer.GetGfxDevice(),
				texture, size, valueBits, uintTexture));
	}

	private static Int2 GetFsr3MipSize(Int2 sourceSize, int mipIndex)
	{
		var divisor = 1 << Math.Min(mipIndex + 1, 30);
		return new Int2(Math.Max(sourceSize.X / divisor, 1), Math.Max(sourceSize.Y / divisor, 1));
	}

	internal static Int2 GetFsr3ShadingChangeSize(Int2 renderSize) => new(
		Math.Max(renderSize.X / 2, 1),
		Math.Max(renderSize.Y / 2, 1));

	private Fsr3ConstantValues BuildFsr3Constants(RenderGraphContext context)
	{
		var size = _view.FrameResources.SceneFramebufferSize;
		var camera = context.ViewSnapshot.Camera;
		var verticalFov = float.DegreesToRadians(camera.Fov > 0.0f ? camera.Fov : 70.0f);
		var depth = Fsr3Constants.BuildDeviceToViewDepth(context.SceneData.NearPlane,
			context.SceneData.FarPlane, verticalFov, (float)Math.Max(size.X, 1) / Math.Max(size.Y, 1));
		var reset = _view.ResetTaaHistoryThisFrame || context.SceneData.ResetHistory || !_view.FrameResources.Fsr3.HistoryValid;
		return Fsr3Constants.Build(size, size, size, size, depth,
			context.SceneData.JitterPixels, context.SceneData.PreviousJitterPixels, verticalFov,
			Math.Max(_uiFrame.DeltaTime, 1.0f / 1000.0f), reset ? 0.0f : _view.Fsr3FrameIndex);
	}

	private void ExecuteFsr3PrepareInputs(RenderGraphContext context)
	{
		var fsr = _view.FrameResources.Fsr3;
		var constants = BuildFsr3Constants(context);
		var config = _passSet.Fsr3PrepareInputsPass.BuildConfig(context, _renderer.GetGfxDevice(),
			_view.FrameResources.LightingBuffer, _view.FrameResources.GBufferDepth, _view.FrameResources.GBufferVelocity,
			fsr.DilatedMotionVectors, fsr.DilatedDepth, fsr.FarthestDepth, fsr.CurrentLumaWrite,
			fsr.ReconstructedPrevNearestDepth, in constants);
		_passSet.Fsr3PrepareInputsPass.Record(context, in config);
	}

	private void ExecuteFsr3LumaPyramid(RenderGraphContext context)
	{
		var fsr = _view.FrameResources.Fsr3;
		var constants = BuildFsr3Constants(context);
		var config = _passSet.Fsr3LumaPyramidPass.BuildConfig(context, _renderer.GetGfxDevice(),
			fsr.CurrentLumaWrite, fsr.FarthestDepth, fsr.FarthestDepthMip1, fsr.FrameInfo,
			fsr.LumaSpdAtomic, fsr.LumaSpdMips, in constants);
		_passSet.Fsr3LumaPyramidPass.Record(context, in config);
	}

	private void ExecuteFsr3ShadingChangePyramid(RenderGraphContext context)
	{
		var fsr = _view.FrameResources.Fsr3;
		var constants = BuildFsr3Constants(context);
		var config = _passSet.Fsr3ShadingChangePyramidPass.BuildConfig(context, _renderer.GetGfxDevice(),
			fsr.CurrentLumaWrite, fsr.CurrentLumaRead, fsr.DilatedMotionVectors, fsr.FrameInfo,
			fsr.ShadingSpdAtomic, fsr.ShadingSpdMips, in constants);
		_passSet.Fsr3ShadingChangePyramidPass.Record(context, in config);
	}

	private void ExecuteFsr3ShadingChange(RenderGraphContext context)
	{
		var fsr = _view.FrameResources.Fsr3;
		var constants = BuildFsr3Constants(context);
		var config = _passSet.Fsr3ShadingChangePass.BuildConfig(context, _renderer.GetGfxDevice(),
			fsr.ShadingSpdMips, fsr.ShadingChange, in constants);
		_passSet.Fsr3ShadingChangePass.Record(context, in config);
	}

	private void ExecuteFsr3PrepareReactivity(RenderGraphContext context)
	{
		var fsr = _view.FrameResources.Fsr3;
		var constants = BuildFsr3Constants(context);
		var settings = _view.FrameResources.Config.AntiAliasing;
		var config = _passSet.Fsr3PrepareReactivityPass.BuildConfig(context, _renderer.GetGfxDevice(),
			fsr.ReconstructedPrevNearestDepth, fsr.DilatedMotionVectors, fsr.DilatedDepth,
			_view.FrameResources.GBufferDepth,
			_view.FrameResources.GBufferMaterial, fsr.TransparencyMask, fsr.AccumulationRead, fsr.AccumulationWrite,
			fsr.ShadingChange, fsr.CurrentLumaWrite, fsr.FrameInfo, fsr.DilatedReactiveMasks,
			fsr.NewLocks, in constants,
			settings.AlphaTestReactiveScale,
			settings.TransparencyAndCompositionMaskScale);
		_passSet.Fsr3PrepareReactivityPass.Record(context, in config);
	}

	private void ExecuteFsr3LumaInstability(RenderGraphContext context)
	{
		var fsr = _view.FrameResources.Fsr3;
		var constants = BuildFsr3Constants(context);
		var config = _passSet.Fsr3LumaInstabilityPass.BuildConfig(context, _renderer.GetGfxDevice(),
			fsr.FrameInfo, fsr.DilatedReactiveMasks, fsr.DilatedMotionVectors, fsr.LumaHistoryRead,
			fsr.LumaHistoryWrite, fsr.FarthestDepthMip1, fsr.CurrentLumaWrite, fsr.LumaInstability,
			in constants);
		_passSet.Fsr3LumaInstabilityPass.Record(context, in config);
	}

	private void ExecuteFsr3Accumulate(RenderGraphContext context)
	{
		var fsr = _view.FrameResources.Fsr3;
		var constants = BuildFsr3Constants(context);
		var config = _passSet.Fsr3AccumulatePass.BuildConfig(context, _renderer.GetGfxDevice(),
			fsr.FrameInfo, _view.FrameResources.LightingBuffer, fsr.DilatedMotionVectors,
			fsr.DilatedReactiveMasks, fsr.FarthestDepthMip1, fsr.LumaInstability, fsr.NewLocks,
			fsr.InternalHistoryRead, fsr.InternalHistoryWrite, in constants);
		_passSet.Fsr3AccumulatePass.Record(context, in config);
	}

	private void ExecuteFsr3Rcas(RenderGraphContext context)
	{
		var fsr = _view.FrameResources.Fsr3;
		var constants = BuildFsr3Constants(context);
		var settings = _view.FrameResources.Config.AntiAliasing;
		var config = _passSet.Fsr3RcasPass.BuildConfig(context, _renderer.GetGfxDevice(),
			fsr.InternalHistoryWrite, _view.FrameResources.ResolvedSceneColor, fsr.FrameInfo,
			in constants, settings.Sharpness, settings.EnableSharpening);
		_passSet.Fsr3RcasPass.Record(context, in config);
	}

	private void ExecuteFsr3DebugView(RenderGraphContext context)
	{
		var fsr = _view.FrameResources.Fsr3;
		var constants = BuildFsr3Constants(context);
		var config = _passSet.Fsr3DebugViewPass.BuildConfig(context, _renderer.GetGfxDevice(),
			fsr.DilatedReactiveMasks, fsr.DilatedMotionVectors, fsr.DilatedDepth,
			fsr.InternalHistoryWrite, fsr.CurrentLumaWrite, fsr.CurrentLumaRead,
			_view.FrameResources.ResolvedSceneColor, fsr.FrameInfo, in constants);
		_passSet.Fsr3DebugViewPass.Record(context, in config);
	}

	private void ExecuteClusteredLightingBuild(RenderGraphContext context)
	{
		var config = _clusteredLightingPass.BuildConfig(
			_renderer.GetGfxDevice(),
			_gpuDrawResources,
			_view.FrameResources.SceneFramebufferSize);
		_clusteredLightingPass.Record(context, in config, context.SceneData, ClusteredLightingPass.Stage.BuildClusters);
	}

	private void ExecuteClusteredLightingWrite(RenderGraphContext context)
	{
		var config = _clusteredLightingPass.BuildConfig(
			_renderer.GetGfxDevice(),
			_gpuDrawResources,
			_view.FrameResources.SceneFramebufferSize);
		_clusteredLightingPass.Record(context, in config, context.SceneData, ClusteredLightingPass.Stage.WriteLightIndices);
	}

	private void ExecuteTemporalResolve(RenderGraphContext context)
	{
		var config = _temporalAntiAliasingPass.BuildConfig(
			context,
			_view.FrameResources,
			_renderer.GetGfxDevice(),
			_view.HistoryValid,
			_view.ResetTaaHistoryThisFrame || context.SceneData.ResetHistory);
		_temporalAntiAliasingPass.Record(context, in config);
	}

	private void ExecuteTemporalHistoryStore(RenderGraphContext context)
	{
		var config = _temporalHistoryStorePass.BuildConfig(
			context,
			_view.FrameResources,
			_renderer.GetGfxDevice());
		_temporalHistoryStorePass.Record(context, in config);
	}

	private void ExecuteAmbientOcclusion(RenderGraphContext context)
	{
		var config = _ambientOcclusionPass.BuildConfig(
			context,
			_view.FrameResources,
			_renderer.GetGfxDevice(),
			_renderer,
			_gpuDrawResources,
			_view.FrameResources.Config.AmbientOcclusion.Mode == AmbientOcclusionMode.RayTraced
				? _rayTracingSceneResources
				: null);
		_ambientOcclusionPass.Record(context, in config, context.SceneData);
	}

	private void ExecuteAmbientOcclusionBlurHorizontal(RenderGraphContext context)
	{
		var config = _ambientOcclusionBlurPass.BuildConfig(
			context,
			_view.FrameResources,
			_renderer.GetGfxDevice(),
			blurHorizontally: true);
		_ambientOcclusionBlurPass.Record(context, in config, context.SceneData);
	}

	private void ExecuteAmbientOcclusionBlurVertical(RenderGraphContext context)
	{
		var config = _ambientOcclusionBlurPass.BuildConfig(
			context,
			_view.FrameResources,
			_renderer.GetGfxDevice(),
			blurHorizontally: false);
		_ambientOcclusionBlurPass.Record(context, in config, context.SceneData);
	}

	private void ExecuteAmbientOcclusionUpsample(RenderGraphContext context)
	{
		var config = _ambientOcclusionUpsamplePass.BuildConfig(
			context,
			_view.FrameResources,
			_renderer.GetGfxDevice());
		_ambientOcclusionUpsamplePass.Record(context, in config, context.SceneData);
	}

	private void ExecuteDdgiClassify(RenderGraphContext context)
	{
		_view.CurrentDdgiConfig = _ddgiPass.BuildConfig(
			context,
			_view.FrameResources,
			_sharedResources,
			_renderer.GetGfxDevice(),
			_renderer,
			_gpuDrawResources,
			_rayTracingSceneResources,
			context.SceneData,
			_view.DdgiHistoryValid);
		_view.CurrentDdgiConfigValid = true;
		_ddgiPass.RecordClassify(context, in _view.CurrentDdgiConfig);
	}

	private void ExecuteDdgiTrace(RenderGraphContext context)
	{
		if (_view.CurrentDdgiConfigValid == false)
		{
			throw new InvalidOperationException("DDGI trace executed before DDGI probe classification config was built.");
		}

		_ddgiPass.RecordTrace(context, in _view.CurrentDdgiConfig);
	}

	private void ExecuteDdgiIrradianceIntegrate(RenderGraphContext context)
	{
		if (_view.CurrentDdgiConfigValid == false)
		{
			throw new InvalidOperationException("DDGI irradiance integrate executed before DDGI trace config was built.");
		}

		_ddgiPass.RecordIrradianceIntegrate(context, in _view.CurrentDdgiConfig);
	}

	private void ExecuteDdgiVisibilityIntegrate(RenderGraphContext context)
	{
		if (_view.CurrentDdgiConfigValid == false)
		{
			throw new InvalidOperationException("DDGI visibility integrate executed before DDGI trace config was built.");
		}

		_ddgiPass.RecordVisibilityIntegrate(context, in _view.CurrentDdgiConfig);
	}

	private void ExecuteDdgiRelocationTrace(RenderGraphContext context, int iteration)
	{
		if (_view.CurrentDdgiConfigValid == false)
		{
			throw new InvalidOperationException("DDGI relocation trace executed before DDGI probe classification config was built.");
		}

		_ddgiPass.RecordRelocationTrace(context, in _view.CurrentDdgiConfig, iteration);
	}

	private void ExecuteDdgiRelocate(RenderGraphContext context, int iteration)
	{
		if (_view.CurrentDdgiConfigValid == false)
		{
			throw new InvalidOperationException("DDGI relocation solve executed before DDGI probe classification config was built.");
		}

		_ddgiPass.RecordRelocate(context, in _view.CurrentDdgiConfig, iteration);
	}

	private void ExecuteTransparentForward(RenderGraphContext context)
	{
		var device = _renderer.GetGfxDevice();
		_transparentForwardPass.EnsureIndirectResources(device, _gpuDrawResources.ActiveViewIndex);
		_gpuDrawPass.EnsureIndirectCommandsForPass(
			context.GpuDrawDatabase,
			_transparentForwardPass.GetIndirectCommandSet(_gpuDrawResources.ActiveViewIndex),
			DrawPassParticipation.ForwardTransparent,
			SharedDrawIndirectEncodeResources.FromGpuDrawResources(_gpuDrawResources),
			lane => _transparentForwardPass.HasIndirectLane(lane),
			lane => _transparentForwardPass.GetBufferBindings(lane),
			lane => _transparentForwardPass.GetPassBindingSet(lane, _gpuDrawResources));
		var config = _transparentForwardPass.BuildConfig(
			context,
			_view.FrameResources,
			_sharedResources,
			device,
			_gpuDrawResources,
			GetShadowFrameData(),
			context.SceneData);
		_transparentForwardPass.Record(context, in config, context.SceneData);
	}

	private void ExecuteTonemapping(RenderGraphContext context)
	{
		var config = _tonemappingPass.BuildConfig(
			context,
			_view.FrameResources,
			_renderer.GetGfxDevice());
		_tonemappingPass.Record(context, in config);
	}

	private void ExecuteBloom(RenderGraphContext context, BloomPass.Stage stage,
		RenderGraphResourceHandle source, RenderGraphResourceHandle output, RenderGraphResourceHandle secondary)
	{
		var config = _bloomPass.BuildConfig(context, _renderer.GetGfxDevice(), stage, source, output, secondary, _view.FrameResources.Config.Bloom);
		_bloomPass.Record(context, stage, in config);
	}

	private void ExecuteBloomComposite(RenderGraphContext context)
	{
		var bloomResult = _view.FrameResources.BloomUpsampleLevels.Length > 0
			? _view.FrameResources.BloomUpsampleLevels[0]
			: _view.FrameResources.BloomDownsampleLevels[0];
		ExecuteBloom(context, BloomPass.Stage.Composite, _view.FrameResources.ResolvedSceneColor,
			_view.FrameResources.BloomCompositeSceneColor, bloomResult);
	}

	private void ExecuteCasSharpen(RenderGraphContext context)
	{
		var config = _casSharpenPass.BuildConfig(
			context,
			_view.FrameResources,
			_renderer.GetGfxDevice());
		_casSharpenPass.Record(context, in config);
	}

	private void ExecuteCopyToFinal(RenderGraphContext context)
	{
		var config = _copyToFinalPass.BuildConfig(
			context,
			_view.FrameResources,
			_sharedResources,
			_renderer.GetGfxDevice(),
			_view.OwnsPresentation);
		_copyToFinalPass.Record(context, in config);
	}

	private void ExecuteMotionVectorDebug(RenderGraphContext context)
	{
		var config = _motionVectorDebugPass.BuildConfig(
			context,
			_renderer.GetGfxDevice(),
			_view.FrameResources.GBufferVelocity,
			_view.FrameResources.MotionVectorDebugColor,
			_view.FrameResources.SceneFramebufferSize,
			_view.FrameResources.Config.MotionVectorDebug);
		_motionVectorDebugPass.Record(context, in config);
	}

	private void ExecuteImGui(RenderGraphContext context)
	{
		var finalColor = context.GetTexture(_sharedResources.FinalColor);
		_imGuiRenderer.EnsureResources(_renderer.GetGfxDevice(), _uiFrame);
		_imGuiRenderer.Record(context, _uiFrame, finalColor, clearTarget: _view.FrameResources.SceneEnabled == false);
	}

	private void ExecuteGameplayScreenEncodedUi(RenderGraphContext context) =>
		ExecuteGameplayScreenUi(context, _view.FrameResources.EncodedSceneColor);

	private void ExecuteGameplayScreenFinalUi(RenderGraphContext context) =>
		ExecuteGameplayScreenUi(context, _sharedResources.FinalColor);

	private void ExecuteGameplayScreenUi(RenderGraphContext context, RenderGraphResourceHandle targetHandle)
	{
		var target = context.GetTexture(targetHandle);
		_gameplayUiRenderer.EnsureResources(_renderer.GetGfxDevice(), _gameplayUiFrame.Screen);
		_gameplayUiRenderer.Record(context, _gameplayUiFrame.Screen, target, clearTarget: false);
	}

	private void ExecuteGameplayTextureUi(RenderGraphContext context, GameplayTextureTarget target)
	{
		_gameplayUiRenderer.EnsureResources(_renderer.GetGfxDevice(), target.Surface.Frame);
		_gameplayUiRenderer.Record(
			context,
			target.Surface.Frame,
			target.Texture,
			clearTarget: true,
			target.Surface.ClearColor);
		target.Surface.IsDirty = false;
	}

	private static RenderGraphResourceHandle GetShadowMapHandle(in RenderViewResources resources, int cascadeIndex)
	{
		return cascadeIndex switch
		{
			0 => resources.ShadowMapDepth0,
			1 => resources.ShadowMapDepth1,
			2 => resources.ShadowMapDepth2,
			_ => throw new ArgumentOutOfRangeException(nameof(cascadeIndex), cascadeIndex, "Cascade index is out of range.")
		};
	}

	private void InvalidateTransientPoolIfFrameShapeChanged(
		Int2 framebufferSize,
		Int2 sceneFramebufferSize,
		int shadowMapResolution,
		bool sceneEnabled)
	{
		var changed = _view.HasPreviousFrameShape == false ||
		              _view.PreviousFramebufferSize.X != framebufferSize.X ||
		              _view.PreviousFramebufferSize.Y != framebufferSize.Y ||
		              _view.PreviousSceneFramebufferSize.X != sceneFramebufferSize.X ||
		              _view.PreviousSceneFramebufferSize.Y != sceneFramebufferSize.Y ||
		              _view.PreviousShadowMapResolution != shadowMapResolution ||
		              _view.PreviousSceneEnabled != sceneEnabled;
		if (changed == false)
		{
			return;
		}

		_resources.InvalidateTransientTexturePool();
		_view.PreviousFramebufferSize = framebufferSize;
		_view.PreviousSceneFramebufferSize = sceneFramebufferSize;
		_view.PreviousShadowMapResolution = shadowMapResolution;
		_view.PreviousSceneEnabled = sceneEnabled;
		_view.HasPreviousFrameShape = true;
	}

	public void CompleteFrame()
	{
		if (_view.FrameResources.ColorPyramidLevels is not { Length: > 0 } || _view.FrameResources.SceneEnabled == false)
		{
			_view.ColorPyramidValid = false;
		}
		else
		{
			for (var level = 0; level < _view.FrameResources.ColorPyramidLevels.Length; level++)
			{
				_view.ColorPyramidStates[level] = _resources.GetResourceState(_view.FrameResources.ColorPyramidLevels[level]);
			}

			_view.ColorPyramidValid = true;
		}

		if (_view.FrameResources.Config.AntiAliasing.Enabled == false || _view.FrameResources.SceneEnabled == false)
		{
			_view.HistoryValid = false;
		}
		else if (_view.FrameResources.HistoryColorWrite.IsValid == false ||
		         _view.FrameResources.HistoryDepthWrite.IsValid == false)
		{
			_view.HistoryValid = false;
		}
		else
		{
			if (_view.FrameResources.HistoryColorRead.IsValid)
			{
				_view.HistoryColorStates[_view.HistoryReadIndex] = _resources.GetResourceState(_view.FrameResources.HistoryColorRead);
			}

			if (_view.FrameResources.HistoryDepthRead.IsValid)
			{
				_view.HistoryDepthStates[_view.HistoryReadIndex] = _resources.GetResourceState(_view.FrameResources.HistoryDepthRead);
			}

			var writeIndex = 1 - _view.HistoryReadIndex;
			_view.HistoryColorStates[writeIndex] = _resources.GetResourceState(_view.FrameResources.HistoryColorWrite);
			_view.HistoryDepthStates[writeIndex] = _resources.GetResourceState(_view.FrameResources.HistoryDepthWrite);
			if (_view.FrameResources.Config.AntiAliasing.UsesFsr3)
			{
				_view.Fsr3CurrentLumaStates[_view.HistoryReadIndex] = _resources.GetResourceState(_view.FrameResources.Fsr3.CurrentLumaRead);
				_view.Fsr3CurrentLumaStates[writeIndex] = _resources.GetResourceState(_view.FrameResources.Fsr3.CurrentLumaWrite);
				_view.Fsr3AccumulationStates[_view.HistoryReadIndex] = _resources.GetResourceState(_view.FrameResources.Fsr3.AccumulationRead);
				_view.Fsr3AccumulationStates[writeIndex] = _resources.GetResourceState(_view.FrameResources.Fsr3.AccumulationWrite);
				_view.Fsr3FrameInfoState = _resources.GetResourceState(_view.FrameResources.Fsr3.FrameInfo);
				_view.Fsr3FrameIndex++;
			}
			_view.HistoryReadIndex = writeIndex;
			_view.HistoryValid = true;
		}

		if (_view.FrameResources.Config.VolumetricFog.Enabled &&
		    _view.FrameResources.SceneEnabled &&
		    _view.FrameResources.FogHistoryWrite.IsValid)
		{
			if (_view.FrameResources.FogHistoryRead.IsValid)
			{
				_view.FogHistoryStates[_view.FogHistoryReadIndex] = _resources.GetResourceState(_view.FrameResources.FogHistoryRead);
			}
			var writeIndex = 1 - _view.FogHistoryReadIndex;
			_view.FogHistoryStates[writeIndex] = _resources.GetResourceState(_view.FrameResources.FogHistoryWrite);
			_view.FogHistoryReadIndex = writeIndex;
			_view.FogHistoryValid = true;
		}
		else
		{
			_view.FogHistoryValid = false;
		}

		if (HasRayTracedDdgi(_view.FrameResources.Config) == false || _view.FrameResources.SceneEnabled == false)
		{
			_view.DdgiHistoryValid = false;
			return;
		}

		if (_view.FrameResources.DdgiIrradianceL0HistoryWrite.IsValid == false ||
		    _view.FrameResources.DdgiIrradianceLyHistoryWrite.IsValid == false ||
		    _view.FrameResources.DdgiIrradianceLzHistoryWrite.IsValid == false ||
		    _view.FrameResources.DdgiIrradianceLxHistoryWrite.IsValid == false ||
		    _view.FrameResources.DdgiVisibilityHistoryWrite.IsValid == false ||
		    _view.FrameResources.DdgiProbeStateWrite.IsValid == false ||
		    _view.FrameResources.DdgiIrradianceEstimator.IsValid == false)
		{
			_view.DdgiHistoryValid = false;
			return;
		}

		UpdateDdgiIrradianceState(0, _view.FrameResources.DdgiIrradianceL0HistoryRead, _view.DdgiHistoryReadIndex);
		UpdateDdgiIrradianceState(1, _view.FrameResources.DdgiIrradianceLyHistoryRead, _view.DdgiHistoryReadIndex);
		UpdateDdgiIrradianceState(2, _view.FrameResources.DdgiIrradianceLzHistoryRead, _view.DdgiHistoryReadIndex);
		UpdateDdgiIrradianceState(3, _view.FrameResources.DdgiIrradianceLxHistoryRead, _view.DdgiHistoryReadIndex);

		if (_view.FrameResources.DdgiVisibilityHistoryRead.IsValid)
		{
			_view.DdgiVisibilityStates[_view.DdgiHistoryReadIndex] = _resources.GetResourceState(_view.FrameResources.DdgiVisibilityHistoryRead);
		}

		if (_view.FrameResources.DdgiProbeStateRead.IsValid)
		{
			_view.DdgiProbeStateStates[_view.DdgiHistoryReadIndex] = _resources.GetResourceState(_view.FrameResources.DdgiProbeStateRead);
		}
		if (_view.FrameResources.DdgiProbeActivity.IsValid)
		{
			_view.DdgiProbeActivityState = _resources.GetResourceState(_view.FrameResources.DdgiProbeActivity);
		}

		var ddgiWriteIndex = 1 - _view.DdgiHistoryReadIndex;
		UpdateDdgiIrradianceState(0, _view.FrameResources.DdgiIrradianceL0HistoryWrite, ddgiWriteIndex);
		UpdateDdgiIrradianceState(1, _view.FrameResources.DdgiIrradianceLyHistoryWrite, ddgiWriteIndex);
		UpdateDdgiIrradianceState(2, _view.FrameResources.DdgiIrradianceLzHistoryWrite, ddgiWriteIndex);
		UpdateDdgiIrradianceState(3, _view.FrameResources.DdgiIrradianceLxHistoryWrite, ddgiWriteIndex);
		_view.DdgiVisibilityStates[ddgiWriteIndex] = _resources.GetResourceState(_view.FrameResources.DdgiVisibilityHistoryWrite);
		_view.DdgiProbeStateStates[ddgiWriteIndex] = _resources.GetResourceState(_view.FrameResources.DdgiProbeStateWrite);
		_view.DdgiIrradianceEstimatorState = _resources.GetResourceState(_view.FrameResources.DdgiIrradianceEstimator);
		_view.DdgiHistoryReadIndex = ddgiWriteIndex;
		_view.DdgiHistoryValid = true;
		_view.DdgiCommittedRuntimeOrigin = _view.FrameResources.DdgiRuntimeOrigin;
		_view.DdgiCommittedStorageOffset = _view.FrameResources.DdgiStorageOffset;
		_view.DdgiCommittedPlacementValid = true;
	}

	private void UpdateDdgiIrradianceState(int coefficientIndex, RenderGraphResourceHandle handle, int historyIndex)
	{
		if (handle.IsValid)
		{
			_view.DdgiIrradianceStates[coefficientIndex, historyIndex] = _resources.GetResourceState(handle);
		}
	}

	private static TextureDescriptor CreateFogTextureDescriptor(Int3 grid) => new(
		grid.X,
		grid.Y,
		TextureFormat.Rgba16Float,
		TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
		new ColorRGBA(0.0f, 0.0f, 0.0f, 1.0f),
		dimension: TextureDimension.Texture3D,
		depth: grid.Z);

	private void EnsureFogHistoryResources(IGfxDevice device, Int3 grid, float maxDistance)
	{
		var changed = _view.FogHistoryDevice is not null &&
		              (!ReferenceEquals(_view.FogHistoryDevice, device) ||
		               _view.FogHistoryBackendKind != device.BackendKind ||
		               !_view.FogHistoryGrid.Equals(grid) ||
		               MathF.Abs(_view.FogHistoryMaxDistance - maxDistance) > 1e-5f);
		if (changed) _view.ReleaseFogHistoryResources();
		if (_view.FogHistoryTextures[0] is not null && _view.FogHistoryTextures[1] is not null) return;
		for (var i = 0; i < 2; i++)
		{
			_view.FogHistoryTextures[i] = device.CreateTexture(CreateFogTextureDescriptor(grid));
			_view.FogHistoryStates[i] = ResourceState.UnorderedAccess;
		}
		_view.FogHistoryDevice = device;
		_view.FogHistoryBackendKind = device.BackendKind;
		_view.FogHistoryGrid = grid;
		_view.FogHistoryMaxDistance = maxDistance;
		_view.FogHistoryReadIndex = 0;
		_view.FogHistoryValid = false;
	}

	private void EnsureTemporalHistoryResources(IGfxDevice device, Int2 sceneFramebufferSize, AntiAliasingMode mode)
	{
		var deviceChanged = _view.HistoryDevice is not null && ReferenceEquals(_view.HistoryDevice, device) == false;
		var backendChanged = _view.HistoryBackendKind.HasValue && _view.HistoryBackendKind.Value != device.BackendKind;
		var sizeChanged = _view.HistorySize.X != sceneFramebufferSize.X || _view.HistorySize.Y != sceneFramebufferSize.Y;
		if (deviceChanged || backendChanged || sizeChanged || _view.HistoryMode != mode)
		{
			_view.ReleaseTemporalHistoryResources();
		}

		if (_view.HistoryColorTextures[0] is not null &&
		    _view.HistoryColorTextures[1] is not null &&
		    _view.HistoryDepthTextures[0] is not null &&
		    _view.HistoryDepthTextures[1] is not null)
		{
			return;
		}

		for (var i = 0; i < 2; i++)
		{
			_view.HistoryColorTextures[i] = device.CreateTexture(new TextureDescriptor(
				sceneFramebufferSize.X,
				sceneFramebufferSize.Y,
				TextureFormat.Rgba16Float,
				TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
				new ColorRGBA(0.0f, 0.0f, 0.0f, 1.0f)));
			_view.HistoryDepthTextures[i] = device.CreateTexture(new TextureDescriptor(
				sceneFramebufferSize.X,
				sceneFramebufferSize.Y,
				TextureFormat.Rgba16Float,
				TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
				new ColorRGBA(1.0f, 1.0f, 1.0f, 1.0f)));
			if (mode == AntiAliasingMode.Fsr3)
			{
				_view.Fsr3CurrentLumaTextures[i] = device.CreateTexture(new TextureDescriptor(
					sceneFramebufferSize.X, sceneFramebufferSize.Y, TextureFormat.Rgba16Float,
					TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
					new ColorRGBA(0.0f, 0.0f, 0.0f, 0.0f)));
				_view.Fsr3AccumulationTextures[i] = device.CreateTexture(new TextureDescriptor(
					sceneFramebufferSize.X, sceneFramebufferSize.Y, TextureFormat.Rgba16Float,
					TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
					new ColorRGBA(0.0f, 0.0f, 0.0f, 0.0f)));
			}
			_view.HistoryColorStates[i] = ResourceState.UnorderedAccess;
			_view.HistoryDepthStates[i] = ResourceState.UnorderedAccess;
			_view.Fsr3CurrentLumaStates[i] = ResourceState.UnorderedAccess;
			_view.Fsr3AccumulationStates[i] = ResourceState.UnorderedAccess;
		}

		if (mode == AntiAliasingMode.Fsr3)
		{
			_view.Fsr3FrameInfoTexture = device.CreateTexture(new TextureDescriptor(
				1, 1, TextureFormat.Rgba16Float,
				TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
				new ColorRGBA(0.0f, 0.0f, 0.0f, 0.0f)));
			_view.Fsr3FrameInfoState = ResourceState.UnorderedAccess;
		}
		_view.HistoryMode = mode;

		_view.HistoryDevice = device;
		_view.HistoryBackendKind = device.BackendKind;
		_view.HistorySize = sceneFramebufferSize;
		_view.HistoryReadIndex = 0;
		_view.HistoryValid = false;
		_view.ResetTaaHistoryThisFrame = true;
	}

	private RenderGraphResourceHandle CreateFsr3Texture(Int2 size) =>
		_resources.CreateTransientTexture(new TextureDescriptor(
			Math.Max(size.X, 1), Math.Max(size.Y, 1), TextureFormat.Rgba16Float,
			TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
			new ColorRGBA(0.0f, 0.0f, 0.0f, 0.0f)));

	private RenderGraphResourceHandle CreateFsr3UintTexture(Int2 size) =>
		_resources.CreateTransientTexture(new TextureDescriptor(
			Math.Max(size.X, 1), Math.Max(size.Y, 1), TextureFormat.R32Uint,
			TextureUsage.ShaderResource | TextureUsage.UnorderedAccess));

	private RenderGraphResourceHandle[] CreateFsr3SpdMips(Int2 sourceSize)
	{
		var result = new RenderGraphResourceHandle[Fsr3LumaPyramidPass.SpdMipCount];
		var size = new Int2(Math.Max(sourceSize.X / 2, 1), Math.Max(sourceSize.Y / 2, 1));
		for (var i = 0; i < result.Length; i++)
		{
			result[i] = CreateFsr3Texture(size);
			size = new Int2(Math.Max(size.X / 2, 1), Math.Max(size.Y / 2, 1));
		}
		return result;
	}

	private static int GetColorPyramidLevelCount(Int2 sceneFramebufferSize)
	{
		var levelCount = 1;
		var width = Math.Max(sceneFramebufferSize.X, 1);
		var height = Math.Max(sceneFramebufferSize.Y, 1);
		while (levelCount < ReflectionsPass.MaxColorPyramidLevels && Math.Min(width, height) > 8)
		{
			width = Math.Max(1, (width + 1) / 2);
			height = Math.Max(1, (height + 1) / 2);
			levelCount++;
		}

		return levelCount;
	}

	private void EnsureColorPyramidResources(IGfxDevice device, Int2 sceneFramebufferSize)
	{
		var deviceChanged = _view.ColorPyramidDevice is not null && ReferenceEquals(_view.ColorPyramidDevice, device) == false;
		var backendChanged = _view.ColorPyramidBackendKind.HasValue && _view.ColorPyramidBackendKind.Value != device.BackendKind;
		var sizeChanged = _view.ColorPyramidSize.X != sceneFramebufferSize.X || _view.ColorPyramidSize.Y != sceneFramebufferSize.Y;
		if (deviceChanged || backendChanged || sizeChanged)
		{
			_view.ReleaseColorPyramidResources();
		}

		if (_view.ColorPyramidTextures.Length > 0)
		{
			return;
		}

		var levelCount = GetColorPyramidLevelCount(sceneFramebufferSize);
		var textures = new IGfxTexture[levelCount];
		var states = new ResourceState[levelCount];
		var levelSize = new Int2(Math.Max(sceneFramebufferSize.X, 1), Math.Max(sceneFramebufferSize.Y, 1));
		for (var level = 0; level < levelCount; level++)
		{
			textures[level] = device.CreateTexture(new TextureDescriptor(
				levelSize.X,
				levelSize.Y,
				TextureFormat.Rgba16Float,
				TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
				new ColorRGBA(0.0f, 0.0f, 0.0f, 1.0f)));
			states[level] = ResourceState.UnorderedAccess;
			levelSize = new Int2(Math.Max(1, (levelSize.X + 1) / 2), Math.Max(1, (levelSize.Y + 1) / 2));
		}

		_view.ColorPyramidTextures = textures;
		_view.ColorPyramidStates = states;
		_view.ColorPyramidDevice = device;
		_view.ColorPyramidBackendKind = device.BackendKind;
		_view.ColorPyramidSize = sceneFramebufferSize;
		_view.ColorPyramidValid = false;
	}

	private void EnsureDdgiHistoryResources(
		IGfxDevice device,
		DdgiGridShape gridShape,
		Vector3 latticeAnchor,
		float probeSpacing)
	{
		var deviceChanged = _view.DdgiHistoryDevice is not null && ReferenceEquals(_view.DdgiHistoryDevice, device) == false;
		var backendChanged = _view.DdgiHistoryBackendKind.HasValue && _view.DdgiHistoryBackendKind.Value != device.BackendKind;
		var shapeChanged = _view.DdgiHistoryGridShape.Equals(gridShape) == false;
		var latticeAnchorChanged = _view.DdgiHistoryDevice is not null && _view.DdgiHistoryLatticeAnchor != latticeAnchor;
		var probeSpacingChanged = _view.DdgiHistoryDevice is not null &&
		                          MathF.Abs(_view.DdgiHistoryProbeSpacing - probeSpacing) > 1e-6f;
		if (deviceChanged || backendChanged || shapeChanged || latticeAnchorChanged || probeSpacingChanged)
		{
			_view.ReleaseDdgiHistoryResources();
		}

		var irradianceTexturesReady = true;
		for (var coefficientIndex = 0; coefficientIndex < DdgiShCoefficientCount; coefficientIndex++)
		{
			irradianceTexturesReady &= _view.DdgiIrradianceTextures[coefficientIndex, 0] is not null &&
			                           _view.DdgiIrradianceTextures[coefficientIndex, 1] is not null;
		}

		if (irradianceTexturesReady &&
		    _view.DdgiVisibilityTextures[0] is not null &&
		    _view.DdgiVisibilityTextures[1] is not null &&
		    _view.DdgiProbeStateTextures[0] is not null &&
		    _view.DdgiProbeStateTextures[1] is not null &&
		    _view.DdgiProbeActivityTexture is not null &&
		    _view.DdgiIrradianceEstimatorBuffer is not null)
		{
			return;
		}

		var visibilityAtlasSize = DdgiUtilities.GetAtlasSize(gridShape, DdgiUtilities.VisibilityTileInteriorSize);
		var shCoefficientTextureSize = DdgiUtilities.GetShCoefficientTextureSize(gridShape);
		for (var i = 0; i < 2; i++)
		{
			for (var coefficientIndex = 0; coefficientIndex < DdgiShCoefficientCount; coefficientIndex++)
			{
				_view.DdgiIrradianceTextures[coefficientIndex, i] = device.CreateTexture(new TextureDescriptor(
					shCoefficientTextureSize.X,
					shCoefficientTextureSize.Y,
					TextureFormat.Rgba16Float,
					TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
					new ColorRGBA(0.0f, 0.0f, 0.0f, 0.0f)));
				_view.DdgiIrradianceStates[coefficientIndex, i] = ResourceState.UnorderedAccess;
			}
			_view.DdgiVisibilityTextures[i] = device.CreateTexture(new TextureDescriptor(
				visibilityAtlasSize.X,
				visibilityAtlasSize.Y,
				TextureFormat.Rgba16Float,
				TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
				new ColorRGBA(1.0f, 1.0f, 0.0f, 1.0f)));
			_view.DdgiProbeStateTextures[i] = device.CreateTexture(new TextureDescriptor(
				gridShape.AtlasColumns,
				gridShape.AtlasRows,
				TextureFormat.Rgba16Float,
				TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
				new ColorRGBA(0.0f, 0.0f, 0.0f, 0.0f)));
			_view.DdgiVisibilityStates[i] = ResourceState.UnorderedAccess;
			_view.DdgiProbeStateStates[i] = ResourceState.UnorderedAccess;
		}
		_view.DdgiProbeActivityTexture = device.CreateTexture(new TextureDescriptor(
			shCoefficientTextureSize.X,
			shCoefficientTextureSize.Y,
			TextureFormat.Rgba16Float,
			TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
			new ColorRGBA(0.0f, 0.0f, 0.0f, 0.0f)));
		_view.DdgiProbeActivityState = ResourceState.UnorderedAccess;
		_view.DdgiIrradianceEstimatorBuffer = device.CreateBuffer(new BufferDescriptor(
			DdgiUtilities.GetIrradianceEstimatorBufferSize(gridShape),
			BufferUsage.Structured,
			BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource));
		_view.DdgiIrradianceEstimatorState = ResourceState.UnorderedAccess;

		_view.DdgiHistoryDevice = device;
		_view.DdgiHistoryBackendKind = device.BackendKind;
		_view.DdgiHistoryGridShape = gridShape;
		_view.DdgiHistoryLatticeAnchor = latticeAnchor;
		_view.DdgiHistoryProbeSpacing = probeSpacing;
		_view.DdgiHistoryReadIndex = 0;
		_view.DdgiHistoryValid = false;
	}

	private static bool HasAmbientOcclusion(RenderConfig config)
	{
		return config.AmbientOcclusion.Enabled &&
		       (config.AmbientOcclusion.Mode == AmbientOcclusionMode.RayTraced ||
		        (config.AmbientOcclusion.VisibilityBitmaskSettings.SliceCount > 0 &&
		         config.AmbientOcclusion.VisibilityBitmaskSettings.StepCount > 0));
	}

	private static bool HasReflections(RenderConfig config)
	{
		return config.Reflections.Enabled &&
		       (config.Reflections.Mode == ReflectionMode.RayTraced ||
		        (config.Reflections.ScreenSpaceSettings.MaxSteps > 0 &&
		         config.Reflections.ScreenSpaceSettings.MaxRayDistance > 0.0f));
	}

	private static Int2 GetReflectionTraceSize(Int2 fullSize, ReflectionConfig config)
	{
		if (config.Mode != ReflectionMode.RayTraced)
		{
			return fullSize;
		}

		var divisor = config.RayTracedSettings.Resolution switch
		{
			RayTracedReflectionResolution.Full => 1,
			RayTracedReflectionResolution.Half => 2,
			RayTracedReflectionResolution.Quarter => 4,
			_ => 2
		};
		return new Int2(
			Math.Max((fullSize.X + divisor - 1) / divisor, 1),
			Math.Max((fullSize.Y + divisor - 1) / divisor, 1));
	}

	private static bool HasRayTracedDdgi(RenderConfig config) => DdgiUtilities.IsRayTracedDdgiEnabled(config);

	private static bool RequiresRayTracingScene(RenderConfig config)
	{
		return (config.AmbientOcclusion.Enabled && config.AmbientOcclusion.Mode == AmbientOcclusionMode.RayTraced) ||
		       (config.Reflections.Enabled && config.Reflections.Mode == ReflectionMode.RayTraced) ||
		       HasRayTracedDdgi(config);
	}

	private static RenderConfig CreateRayTracingDisabledConfig(RenderConfig source)
	{
		var ambientOcclusion = source.AmbientOcclusion;
		if (ambientOcclusion.Enabled && ambientOcclusion.Mode == AmbientOcclusionMode.RayTraced)
		{
			ambientOcclusion.Enabled = false;
		}
		source.AmbientOcclusion = ambientOcclusion;
		var reflections = source.Reflections;
		if (reflections.Enabled && reflections.Mode == ReflectionMode.RayTraced)
		{
			reflections.Mode = ReflectionMode.ScreenSpace;
		}
		source.Reflections = reflections;
		var diffuseGlobalIllumination = source.DiffuseGlobalIllumination;
		diffuseGlobalIllumination.Enabled = false;
		source.DiffuseGlobalIllumination = diffuseGlobalIllumination;
		return source;
	}

	private static Int2 GetAmbientOcclusionInternalSize(
		Int2 sceneFramebufferSize,
		AmbientOcclusionResolution resolution)
	{
		return resolution == AmbientOcclusionResolution.Half
			? new Int2(
				(sceneFramebufferSize.X + 1) / 2,
				(sceneFramebufferSize.Y + 1) / 2)
			: sceneFramebufferSize;
	}
}

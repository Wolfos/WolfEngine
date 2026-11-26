using WolfEngine.Profiling;
using System.Runtime.InteropServices;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Features;
using WolfEngine.Rendering.Passes;
using WolfEngine.Vfx;
using WolfEngine.Rendering.UI;
using WolfEngine.Rendering.Shaders;
using WolfEngine.Rendering.Atmosphere;
using WolfEngine.Rendering.Clouds;

namespace WolfEngine.Rendering;

/// <summary>
/// FSR3's view resources: its history, its frame info and the transparency mask the transparent pass writes. Its
/// other intermediates are FSR3's own.
/// </summary>
public readonly struct Fsr3FrameResources
{
	public RenderGraphResourceHandle TransparencyMask { get; init; }
	public RenderGraphResourceHandle CurrentLumaRead { get; init; }
	public RenderGraphResourceHandle CurrentLumaWrite { get; init; }
	public RenderGraphResourceHandle FrameInfo { get; init; }
	public RenderGraphResourceHandle AccumulationRead { get; init; }
	public RenderGraphResourceHandle AccumulationWrite { get; init; }
	public RenderGraphResourceHandle LumaHistoryRead { get; init; }
	public RenderGraphResourceHandle LumaHistoryWrite { get; init; }
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
	/// <summary>One traced diffuse sample per trace pixel, at the final gather's own resolution.</summary>
	public RenderGraphResourceHandle DdgiFinalGatherTrace { get; init; }

	/// <summary>
	/// The SVGF history slots, indexed by <see cref="DdgiFinalGatherHistorySlot"/>; null when the denoiser is off.
	/// </summary>
	public RenderGraphResourceHandle[]? DdgiFinalGatherHistoryRead { get; init; }
	public RenderGraphResourceHandle[]? DdgiFinalGatherHistoryWrite { get; init; }
	public bool DdgiFinalGatherHistoryValid { get; init; }

	/// <summary>Temporally accumulated irradiance / PI in rgb, history length in alpha.</summary>
	public RenderGraphResourceHandle DdgiFinalGatherIntegrated { get; init; }

	public int DdgiFinalGatherFilterIterations { get; init; }

	/// <summary>
	/// The finished trace-resolution result: the last a-trous level's output, or the raw trace when the denoiser
	/// is off. The same handle as <see cref="DdgiFinalGatherIrradiance"/> when that needs no upsampling.
	/// </summary>
	public RenderGraphResourceHandle DdgiFinalGatherDenoised { get; init; }
	public RenderGraphResourceHandle DdgiFinalGatherIrradiance { get; init; }
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

	/// <summary>
	/// The sky this view draws and is lit by, from its own sky settings and sun: where its radiance is read
	/// from, and the image-based lighting derived from it.
	/// </summary>
	internal SkyEnvironmentSource SkyEnvironment { get; init; }

	/// <summary>The Procedural sky's atmosphere LUTs; invalid handles in other sky modes.</summary>
	internal AtmosphereViewHandles Atmosphere { get; init; }

	/// <summary>The volumetric cloud layer; disabled when clouds are off or the sky is not Procedural.</summary>
	internal CloudViewResources Clouds { get; init; }
	public RenderGraphResourceHandle SkyboxIrradiance { get; init; }
	public RenderGraphResourceHandle SkyboxPrefilter { get; init; }
	public RenderConfig Config { get; init; }
}

/// <summary>
/// Resources recorded once for the whole rendered frame, regardless of how many views it contains.
/// </summary>
public readonly struct RenderFrameSharedResources
{
	public Int2 FramebufferSize { get; init; }
	public RenderGraphResourceHandle FinalColor { get; init; }

	/// <summary>Independent of the sky, so one serves every view.</summary>
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
	private readonly CloudPass _cloudPass;
	private readonly ReflectionsPass _reflectionsPass;
	private readonly ReflectionsUpsamplePass _reflectionsUpsamplePass;
	private readonly DdgiFinalGatherPass _ddgiFinalGatherPass;
	private readonly DdgiFinalGatherSvgfPass _ddgiFinalGatherSvgfPass;
	private readonly TemporalAntiAliasingPass _temporalAntiAliasingPass;
	private readonly TemporalHistoryStorePass _temporalHistoryStorePass;
	private readonly TransparentForwardPass _transparentForwardPass;
	private readonly VfxPass _vfxPass;
	private readonly BloomPass _bloomPass;
	private readonly ColorPyramidPass _colorPyramidPass;
	private readonly TonemappingPass _tonemappingPass;
	private readonly CasSharpenPass _casSharpenPass;
	private readonly CopyToFinalPass _copyToFinalPass;
	private readonly MotionVectorDebugPass _motionVectorDebugPass;
	private readonly ShadowMapPass _shadowMapPass;
	private readonly GpuDrawPass _gpuDrawPass;
	private readonly GpuDrawResources _gpuDrawResources;
	private readonly RayTracingScene _rayTracingScene;
	private readonly SkinningPass _skinningPass;
    public int LastSkinningDispatchCount => _skinningPass.LastDispatchedInstanceCount;
	private readonly SkyboxPass _skyboxPass;
	private readonly GameplayUiGpuRenderer _gameplayUiRenderer;
	private RenderFrameSharedResources _sharedResources;

	/// <summary>
	/// The window's image before presentation: a backbuffer view's scene, with the editor UI drawn over it. It
	/// persists across frames because the UI is drawn into it every frame, including frames that run no graph.
	/// It is in the render-target state whenever no graph or UI submission is using it, which is what
	/// <see cref="IRenderer.Render"/> expects of it.
	/// </summary>
	internal EditorSceneRenderTargetManager WindowColorTarget { get; } = new(TextureFormat.Bgra8Unorm);
	private GameplayUiRenderFrame _gameplayUiFrame = GameplayUiRenderFrame.Empty;
	private readonly List<GameplayTextureTarget> _gameplayTextureTargets = [];

	private readonly record struct GameplayTextureTarget(
		GameplayUiTextureSurfaceFrame Surface,
		RenderGraphResourceHandle Handle,
		IGfxTexture Texture);
	// Output texture id per view index, rebuilt each frame and consumed when the UI frame's viewport
	// sentinels are rewritten. Zero means the view produced nothing this frame.
	private readonly nint[] _viewportTextureIds = new nint[UiTextureIds.MaxViewports];
	// Whether each view's output is drawn ignoring its alpha, because that alpha holds data rather than coverage.
	private readonly bool[] _viewportOpaque = new bool[UiTextureIds.MaxViewports];
	// Whether each view's output outlives its frame, so frames that run no graph can keep showing it.
	private readonly bool[] _viewportPersistent = new bool[UiTextureIds.MaxViewports];
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
	private readonly Action<RenderGraphContext> _transparentForwardExecute;
	private readonly Action<RenderGraphContext> _gameplayScreenEncodedUiExecute;
	private readonly Action<RenderGraphContext> _gameplayScreenFinalUiExecute;
	private readonly Action<RenderGraphContext> _gpuDrawUpdateExecute;
	private readonly Action<RenderGraphContext> _gpuDrawViewUpdateExecute;
	private readonly Action<RenderGraphContext> _gpuDrawShadowCullExecute;
	private readonly Action<RenderGraphContext> _shadowMapExecute;
	private readonly Action<RenderGraphContext> _gpuDrawCameraCullExecute;

	// The cloud noise is shared by every view, so it is imported, and generated, at most once a frame.
	private bool _cloudNoiseImported;
	private bool _cloudNoiseRecorded;
	private RenderGraphResourceHandle _cloudWeather;
	private RenderGraphResourceHandle _cloudShape;
	private RenderGraphResourceHandle _cloudDetail;
	private bool _recordProceduralSkyBrdf;
	private readonly RenderFeatureHost _renderFeatureHost;


	public RenderGraphFrameBuilder(
		RenderGraphResourceRegistry resources,
		IRenderer renderer,
		RenderGraphPassSet passSet,
		GpuDrawResources gpuDrawResources,
		GameplayUiGpuRenderer gameplayUiRenderer,
		IShaderProvider shaderProvider,
		RenderViewRegistry viewRegistry)
	{
		_viewRegistry = viewRegistry ?? throw new ArgumentNullException(nameof(viewRegistry));
		_passSet = passSet;
		_rayTracingScene = passSet.RayTracingScene;
		_skinningPass = new SkinningPass(shaderProvider);
		_resources = resources;
		_renderer = renderer;
		_renderFeatureHost = new RenderFeatureHost(resources, renderer, passSet.BindlessResourceRegistry);
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
		_cloudPass = passSet.CloudPass;
		_reflectionsPass = passSet.ReflectionsPass;
		_reflectionsUpsamplePass = passSet.ReflectionsUpsamplePass;
		_ddgiFinalGatherPass = passSet.DdgiFinalGatherPass;
		_ddgiFinalGatherSvgfPass = passSet.DdgiFinalGatherSvgfPass;
		_temporalAntiAliasingPass = passSet.TemporalAntiAliasingPass;
		_temporalHistoryStorePass = passSet.TemporalHistoryStorePass;
		_transparentForwardPass = passSet.TransparentForwardPass;
		_vfxPass = passSet.VfxPass;
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
		_gameplayUiRenderer = gameplayUiRenderer;
		_view = GetOrCreateViewState(RenderViewId.Primary);

		_gbufferExecute = ExecuteGBuffer;
		_transparentForwardExecute = ExecuteTransparentForward;
		_gameplayScreenEncodedUiExecute = ExecuteGameplayScreenEncodedUi;
		_gameplayScreenFinalUiExecute = ExecuteGameplayScreenFinalUi;
		_gpuDrawUpdateExecute = ExecuteGpuDrawUpdate;
		_gpuDrawViewUpdateExecute = ExecuteGpuDrawViewUpdate;
		_gpuDrawShadowCullExecute = ExecuteGpuDrawCullShadow;
		_shadowMapExecute = ExecuteShadowMap;
		_gpuDrawCameraCullExecute = ExecuteGpuDrawCullCamera;
	}

	public void InvalidateShaderPipelines()
	{
		_passSet.InvalidateShaderPipelines();
		ShaderPipelineInvalidation.Invalidate(_rayTracingScene.Resources);
		_skinningPass.InvalidateShaders();
	}

	public void ResetRayTracingScene()
	{
		// The replacement scene may have RT disabled, so waiting for its next RT pass leaks the old scene.
		_rayTracingScene.Reset(_renderer.GetGfxDevice());
	}

	public RayTracingSceneState GetRayTracingSceneState() => _rayTracingScene.Resources.GetState();

	public FoliageDrawStats GetFoliageStats() => _gpuDrawPass.GetFoliageStats();

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
		Vector3 sunIlluminance,
		RenderConfig config,
		Vector3 cameraPosition)
	{
		BeginSharedFrame(framebufferSize);
		BeginViewFrame(
			RenderViewId.Primary,
			framebufferSize,
			sceneFramebufferSize,
			sceneColorHandle,
			sceneEnabled,
			hasActiveDecals,
			config,
			cameraPosition,
			sunDirection,
			sunIlluminance);
	}

	/// <summary>
	/// Sets up what every view in the frame shares: gameplay UI texture targets, the shared BRDF LUT, and the
	/// final presentation target. Once per frame, before any view. Each view's sky is set up by
	/// <see cref="BeginViewFrame"/>.
	/// </summary>
	public void BeginSharedFrame(Int2 framebufferSize)
	{
		_frameViews.Clear();
		var device = _renderer.GetGfxDevice();
		_cloudNoiseImported = false;
		_cloudNoiseRecorded = false;
		_gameplayTextureTargets.Clear();
		for (var i = 0; i < _gameplayUiFrame.TextureSurfaces.Length; i++)
		{
			var surface = _gameplayUiFrame.TextureSurfaces[i];
			var texture = _gameplayUiRenderer.EnsureTarget(device, surface.Target);
			var handle = _resources.ImportTexture(texture, takeOwnership: false, initialState: ResourceState.ShaderResource);
			_gameplayTextureTargets.Add(new GameplayTextureTarget(surface, handle, texture));
		}
		_gameplayUiRenderer.PruneTargets(device, _gameplayUiFrame);

		_skyboxPass.PrepareBrdfLut(device);
		_recordProceduralSkyBrdf = _skyboxPass.ShouldRecordBrdfLutUpdate;
		var skyboxBrdfHandle = _resources.ImportTexture(
			_skyboxPass.BrdfLut,
			takeOwnership: false,
			initialState: _skyboxPass.BrdfLutInitialState);

		_sharedResources = new()
		{
			FramebufferSize = framebufferSize,
			FinalColor = _resources.ImportTexture(
				WindowColorTarget.EnsureTarget(device, framebufferSize),
				takeOwnership: false,
				initialState: WindowColorTarget.CurrentState),
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
		Vector3 cameraPosition,
		Vector3 sunDirection,
		Vector3 sunIlluminance,
		Texture? skyHdri = null)
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
		_view.ResetTaaHistoryThisFrame = frameShapeChanged || (taaEnabled &&
			(!_view.PreviousTaaEnabled || _view.PreviousAntiAliasingMode != config.AntiAliasing.Mode));
		_view.PreviousAntiAliasingMode = config.AntiAliasing.Mode;
		if (frameShapeChanged)
		{
			_view.InvalidateScreenSpaceHistories();
		}

		if (!taaEnabled || !sceneEnabled)
		{
			_view.ReleaseTemporalHistoryResources();
		}
		if (!config.VolumetricFog.Enabled || !sceneEnabled)
		{
			_view.ReleaseFogHistoryResources();
		}
		if (UsesClouds(config) == false || sceneEnabled == false)
		{
			_view.ReleaseCloudHistoryResources();
		}
		if (DdgiUtilities.IsFinalGatherDenoiseEnabled(config) == false || sceneEnabled == false)
		{
			_view.ReleaseFinalGatherHistoryResources();
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
		var ddgiFinalGatherTraceHandle = default(RenderGraphResourceHandle);
		var ddgiFinalGatherHistoryReadHandles = default(RenderGraphResourceHandle[]);
		var ddgiFinalGatherHistoryWriteHandles = default(RenderGraphResourceHandle[]);
		var ddgiFinalGatherHistoryValid = false;
		var ddgiFinalGatherIntegratedHandle = default(RenderGraphResourceHandle);
		var ddgiFinalGatherFilterIterations = 0;
		var ddgiFinalGatherDenoisedHandle = default(RenderGraphResourceHandle);
		var ddgiFinalGatherIrradianceHandle = default(RenderGraphResourceHandle);
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
		var resolvedSceneColorSize = GetResolvedSceneColorSize(config.AntiAliasing, framebufferSize, sceneFramebufferSize);
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
					resolvedSceneColorSize.X,
					resolvedSceneColorSize.Y,
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
				_view.FogHistory.Ensure(device, new FogHistoryShape(fogGrid, maxDistance));
				_view.FogHistory.Import(_resources);
				fogCurrentHandle = _resources.CreateTransientTexture(CreateFogTextureDescriptor(fogGrid));
				fogIntegratedHandle = _resources.CreateTransientTexture(CreateFogTextureDescriptor(fogGrid));
				fogHistoryReadHandle = _view.FogHistory.Previous();
				fogHistoryWriteHandle = _view.FogHistory.Current();
				fogHistoryValid = _view.FogHistory.IsValid;
			}
			else
			{
				_view.ReleaseFogHistoryResources();
			}

			fsr3Resources = new Fsr3FrameResources { TransparencyMask = transparencyMaskHandle };
			if (taaEnabled)
			{
				if (config.AntiAliasing.UsesFsr3)
				{
					_view.TemporalHistory.Release();
					if (EnsureFsr3HistoryResources(device, sceneFramebufferSize, framebufferSize))
					{
						_view.ResetTaaHistoryThisFrame = true;
					}

					_view.Fsr3History.Import(_resources);
					var frameInfoHandle = _resources.ImportTexture(
						_view.Fsr3FrameInfoTexture ?? throw new InvalidOperationException("FSR3 frame info was not allocated."),
						takeOwnership: false,
						initialState: _view.Fsr3FrameInfoState);
					// FSR3 creates its own intermediates when it adds its nodes; these are the ones it shares.
					fsr3Resources = new Fsr3FrameResources
					{
						TransparencyMask = transparencyMaskHandle,
						CurrentLumaRead = _view.Fsr3History.Previous(Fsr3HistorySlot.CurrentLuma),
						CurrentLumaWrite = _view.Fsr3History.Current(Fsr3HistorySlot.CurrentLuma),
						FrameInfo = frameInfoHandle,
						AccumulationRead = _view.Fsr3History.Previous(Fsr3HistorySlot.Accumulation),
						AccumulationWrite = _view.Fsr3History.Current(Fsr3HistorySlot.Accumulation),
						LumaHistoryRead = _view.Fsr3History.Previous(Fsr3HistorySlot.LumaHistory),
						LumaHistoryWrite = _view.Fsr3History.Current(Fsr3HistorySlot.LumaHistory),
						InternalHistoryRead = _view.Fsr3History.Previous(Fsr3HistorySlot.InternalUpscaled),
						InternalHistoryWrite = _view.Fsr3History.Current(Fsr3HistorySlot.InternalUpscaled),
						HistoryValid = _view.Fsr3History.IsValid
					};
				}
				else
				{
					_view.ReleaseFsr3HistoryResources();

					var temporalHistoryShape = new TemporalHistoryShape(sceneFramebufferSize, config.AntiAliasing.Mode);
					if (_view.TemporalHistory.Ensure(device, temporalHistoryShape))
					{
						_view.ResetTaaHistoryThisFrame = true;
					}

					_view.TemporalHistory.Import(_resources);
					historyColorReadHandle = _view.TemporalHistory.Previous(TemporalHistorySlot.Color);
					historyColorWriteHandle = _view.TemporalHistory.Current(TemporalHistorySlot.Color);
					historyDepthReadHandle = _view.TemporalHistory.Previous(TemporalHistorySlot.Depth);
					historyDepthWriteHandle = _view.TemporalHistory.Current(TemporalHistorySlot.Depth);
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
				var visibilityTileInteriorSize = DdgiUtilities.GetVisibilityTileInteriorSize(ddgiConfig);
				var visibilityAtlasSize = DdgiUtilities.GetAtlasSize(ddgiGridShape, visibilityTileInteriorSize);
				EnsureDdgiHistoryResources(
					device,
					new DdgiHistoryShape(
						ddgiGridShape,
						ddgiConfig.Origin,
						ddgiProbeSpacing,
						visibilityTileInteriorSize));
				ddgiRuntimeOrigin = DdgiUtilities.GetRuntimeOrigin(
					ddgiConfig.Origin,
					ddgiGridShape,
					ddgiProbeSpacing,
					cameraPosition);
				if (_view.DdgiHistory.IsValid && _view.DdgiCommittedPlacementValid)
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
				_view.DdgiHistory.Import(_resources);
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
					_view.DdgiIrradianceEstimatorBuffer!,
					takeOwnership: false,
					initialState: _view.DdgiIrradianceEstimatorState);
				ddgiIrradianceL0ReadHandle = _view.DdgiHistory.Previous(DdgiHistorySlot.Irradiance);
				ddgiIrradianceL0WriteHandle = _view.DdgiHistory.Current(DdgiHistorySlot.Irradiance);
				ddgiIrradianceLyReadHandle = _view.DdgiHistory.Previous(DdgiHistorySlot.Irradiance + 1);
				ddgiIrradianceLyWriteHandle = _view.DdgiHistory.Current(DdgiHistorySlot.Irradiance + 1);
				ddgiIrradianceLzReadHandle = _view.DdgiHistory.Previous(DdgiHistorySlot.Irradiance + 2);
				ddgiIrradianceLzWriteHandle = _view.DdgiHistory.Current(DdgiHistorySlot.Irradiance + 2);
				ddgiIrradianceLxReadHandle = _view.DdgiHistory.Previous(DdgiHistorySlot.Irradiance + 3);
				ddgiIrradianceLxWriteHandle = _view.DdgiHistory.Current(DdgiHistorySlot.Irradiance + 3);
				ddgiVisibilityReadHandle = _view.DdgiHistory.Previous(DdgiHistorySlot.Visibility);
				ddgiVisibilityWriteHandle = _view.DdgiHistory.Current(DdgiHistorySlot.Visibility);
				ddgiProbeStateReadHandle = _view.DdgiHistory.Previous(DdgiHistorySlot.ProbeState);
				ddgiProbeStateWriteHandle = _view.DdgiHistory.Current(DdgiHistorySlot.ProbeState);
				ddgiProbeActivityHandle = _resources.ImportTexture(
					_view.DdgiProbeActivityTexture!,
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
				if (DdgiUtilities.IsFinalGatherEnabled(config))
				{
					var finalGatherTraceSize = DdgiUtilities.GetFinalGatherTraceSize(
						sceneFramebufferSize,
						ddgiConfig.FinalGather);
					var tracesAtFullResolution =
						finalGatherTraceSize.X == sceneFramebufferSize.X &&
						finalGatherTraceSize.Y == sceneFramebufferSize.Y;
					ddgiFinalGatherTraceHandle = CreateFinalGatherTexture(finalGatherTraceSize);
					ddgiFinalGatherDenoisedHandle = ddgiFinalGatherTraceHandle;

					if (DdgiUtilities.IsFinalGatherDenoiseEnabled(config))
					{
						_view.FinalGatherHistory.Ensure(device, finalGatherTraceSize);
						_view.FinalGatherHistory.Import(_resources);
						ddgiFinalGatherHistoryReadHandles = new RenderGraphResourceHandle[DdgiFinalGatherHistorySlot.Count];
						ddgiFinalGatherHistoryWriteHandles = new RenderGraphResourceHandle[DdgiFinalGatherHistorySlot.Count];
						for (var slot = 0; slot < DdgiFinalGatherHistorySlot.Count; slot++)
						{
							ddgiFinalGatherHistoryReadHandles[slot] = _view.FinalGatherHistory.Previous(slot);
							ddgiFinalGatherHistoryWriteHandles[slot] = _view.FinalGatherHistory.Current(slot);
						}
						ddgiFinalGatherHistoryValid = _view.FinalGatherHistory.IsValid;
						ddgiFinalGatherIntegratedHandle = CreateFinalGatherTexture(finalGatherTraceSize);

						// The first a-trous level writes the color history; later levels ping-pong between the
						// denoiser's own intermediates and the last writes the finished target.
						ddgiFinalGatherFilterIterations = DdgiUtilities.GetFinalGatherFilterIterations(ddgiConfig.FinalGather);
						ddgiFinalGatherDenoisedHandle = ddgiFinalGatherFilterIterations > 1
							? CreateFinalGatherTexture(finalGatherTraceSize)
							: ddgiFinalGatherHistoryWriteHandles[DdgiFinalGatherHistorySlot.Color];
					}

					ddgiFinalGatherIrradianceHandle = tracesAtFullResolution
						? ddgiFinalGatherDenoisedHandle
						: CreateFinalGatherTexture(sceneFramebufferSize);
				}
			}
		}

		if (sceneEnabled && config.Bloom.Enabled)
		{
			// Bloom runs on the resolved image, which an upscaler has already brought to display size. Its levels
			// are view resources rather than bloom's own, because the debug views show them.
			var bloomLevels = BloomPass.CreateLevels(_resources, resolvedSceneColorSize, config.Bloom.Quality);
			bloomDownsampleLevels = bloomLevels.Downsample;
			bloomUpsampleLevels = bloomLevels.Upsample;
			bloomCompositeSceneColorHandle = bloomLevels.Composite;
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
			? CreateEncodedSceneColor(framebufferSize)
			: default;

		var (skyEnvironment, skyboxIrradianceHandle, skyboxPrefilterHandle, atmosphereHandles) = sceneEnabled
			? ImportViewSky(sunDirection, sunIlluminance, cameraPosition, config.Sky, skyHdri)
			: default;
		var cloudResources = sceneEnabled
			? PrepareViewClouds(device, config, cameraPosition, sceneFramebufferSize, atmosphereHandles, skyEnvironment)
			: default;

		_view.FrameResources = new()
		{
			SkyEnvironment = skyEnvironment,
			Atmosphere = atmosphereHandles,
			Clouds = cloudResources,
			SkyboxIrradiance = skyboxIrradianceHandle,
			SkyboxPrefilter = skyboxPrefilterHandle,
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
			DdgiFinalGatherTrace = ddgiFinalGatherTraceHandle,
			DdgiFinalGatherHistoryRead = ddgiFinalGatherHistoryReadHandles,
			DdgiFinalGatherHistoryWrite = ddgiFinalGatherHistoryWriteHandles,
			DdgiFinalGatherHistoryValid = ddgiFinalGatherHistoryValid,
			DdgiFinalGatherIntegrated = ddgiFinalGatherIntegratedHandle,
			DdgiFinalGatherFilterIterations = ddgiFinalGatherFilterIterations,
			DdgiFinalGatherDenoised = ddgiFinalGatherDenoisedHandle,
			DdgiFinalGatherIrradiance = ddgiFinalGatherIrradianceHandle,
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
			if (ddgiFinalGatherTraceHandle.IsValid)
			{
				// Alpha holds hit distance, history length or variance, not coverage.
				RegisterSceneDebugView(SceneDebugViewIds.DdgiFinalGatherRaw, "DDGI Final Gather (Raw Trace)", ddgiFinalGatherTraceHandle, SceneDebugViewKind.ColorIgnoreAlpha);
				if (ddgiFinalGatherIntegratedHandle.IsValid)
				{
					RegisterSceneDebugView(
						SceneDebugViewIds.DdgiFinalGatherAccumulated,
						"DDGI Final Gather (Accumulated)",
						ddgiFinalGatherIntegratedHandle,
						SceneDebugViewKind.ColorIgnoreAlpha);
					RegisterSceneDebugView(
						SceneDebugViewIds.DdgiFinalGatherDenoised,
						"DDGI Final Gather (Denoised)",
						ddgiFinalGatherDenoisedHandle,
						SceneDebugViewKind.ColorIgnoreAlpha);
				}
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
			RegisterSceneDebugView(SceneDebugViewIds.GBufferMaterial, "GBuffer Material", gbufferMaterialHandle, SceneDebugViewKind.ColorIgnoreAlpha);
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

	/// <summary>Once per frame, before views are set up: releases what unregistered render features owned.</summary>
	internal void BeginRenderFeatures(RenderFeatureRegistry registry) => _renderFeatureHost.BeginFrame(registry);

	/// <summary>Hands the bound view its snapshot's render features; call after <see cref="BeginViewFrame"/>.</summary>
	internal void SetViewRenderFeatures(IReadOnlyList<RenderFeatureFrame> frames) => _view.RenderFeatureFrames = frames;

	internal void SetViewVfx(VfxWorldFrame? frame) => _view.VfxFrame = frame;

	/// <summary>Once per frame, after execution and before the resource registry ends the frame.</summary>
	internal void CompleteRenderFeatures() => _renderFeatureHost.CompleteFrame();

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
			if (!_gameplayUiRenderer.NeedsRedraw(target.Surface))
			{
				continue;
			}

			graph.AddPass($"Gameplay UI Texture {target.Surface.SurfaceId}", PassKind.Graphics)
				.WriteTexture(target.Handle, ResourceState.RenderTarget)
				.SetExecute(context => ExecuteGameplayTextureUi(context, target));
		}

		if (_recordProceduralSkyBrdf)
		{
			_skyboxPass.AddBrdfLutNodes(graph, _sharedResources);
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
	/// <summary>
	/// Regenerates the bound view's sky when what it is built from changed since it was last built: its settings,
	/// its sun in Procedural mode, or its HDRI texture in HDRI mode.
	/// </summary>
	private void RecordViewSkyUpdate(RenderGraph graph)
	{
		if (_view.FrameResources.Atmosphere.Transmittance.IsValid)
		{
			_skyboxPass.Atmosphere.AddNodes(graph, _view);
		}

		RecordCloudNoise(graph);

		if (_view.Sky.ShouldRecordLightingUpdate == false)
		{
			return;
		}

		if (_view.FrameResources.SkyEnvironment.ViewEnvironment.IsValid)
		{
			_skyboxPass.AddEnvironmentNodes(graph, _view.FrameResources);

			var clouds = _view.FrameResources.Clouds;
			if (clouds.Enabled && clouds.RecordsEnvironment)
			{
				_cloudPass.AddEnvironmentNodes(graph, _view.FrameResources);
			}
		}

		_skyboxPass.AddImageLightingNodes(graph, _view.FrameResources);
	}


	private void RecordViewPasses(RenderGraph graph)
	{
		if (_view.FrameResources.SceneEnabled)
		{
			RecordViewSkyUpdate(graph);

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
				_gBufferDecalSeedPass.AddNodes(graph, _view.FrameResources);
				_screenSpaceDecalPass.AddNodes(graph, _view.FrameResources);
			}

			if (_view.FrameResources.AmbientOcclusionRaw.IsValid)
			{
				_ambientOcclusionPass.AddNodes(graph, _view.FrameResources);
				_ambientOcclusionBlurPass.AddNodes(graph, _view);
				if (_view.FrameResources.Config.AmbientOcclusion.Resolution == AmbientOcclusionResolution.Half)
				{
					_ambientOcclusionUpsamplePass.AddNodes(graph, _view.FrameResources);
				}
			}

			if (_view.FrameResources.Clouds.Enabled)
			{
				_cloudPass.AddNodes(graph, _view.FrameResources);
			}

			if (_view.FrameResources.FogIntegrated.IsValid)
			{
				_volumetricFogPass.AddNodes(graph, _view.FrameResources);
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
				_ddgiPass.AddNodes(graph, _view);

				if (_view.FrameResources.DdgiFinalGatherTrace.IsValid)
				{
					_ddgiFinalGatherPass.AddNodes(graph, _view.FrameResources, _sharedResources);

					if (_view.FrameResources.DdgiFinalGatherHistoryWrite is not null)
					{
						_ddgiFinalGatherSvgfPass.AddNodes(graph, _view);
					}

					if (_view.FrameResources.DdgiFinalGatherDenoised.Id != _view.FrameResources.DdgiFinalGatherIrradiance.Id)
					{
						_reflectionsUpsamplePass.AddFinalGatherNodes(graph, _view.FrameResources);
					}
				}
			}

			_clusteredLightingPass.AddNodes(graph);

			// Reflections run before deferred lighting so their radiance can feed the specular
			// term directly. Shaded hit color therefore comes from the previous frame's pyramid.
			if (_view.FrameResources.ReflectionsRadiance.IsValid)
			{
				_reflectionsPass.AddNodes(graph, _view.FrameResources, _sharedResources);

				if (_view.FrameResources.Config.Reflections.Mode == ReflectionMode.RayTraced &&
				    _view.FrameResources.Config.Reflections.RayTracedSettings.Resolution !=
				    RayTracedReflectionResolution.Full)
				{
					_reflectionsUpsamplePass.AddReflectionNodes(graph, _view.FrameResources);
				}
			}
			_deferredLightingPass.AddNodes(graph, _view.FrameResources, _sharedResources);

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
			
			transparentForwardBuilder.ReadSkyLighting(_view.FrameResources, _sharedResources);
			transparentForwardBuilder.ReadCloudShadow(_view.FrameResources);
			transparentForwardBuilder.SetExecute(_transparentForwardExecute);

			if (VfxPass.HasWork(_view.VfxFrame))
			{
				_vfxPass.AddNodes(graph, _view.FrameResources);
			}

			_renderFeatureHost.Record(
				graph,
				RenderInjectionPoint.BeforeTemporalResolve,
				_view.RenderFeatureFrames,
				_view.View,
				_view.FrameResources.LightingBuffer,
				_view.FrameResources.GBufferDepth);

			if (_view.FrameResources.Config.AntiAliasing.UsesFsr3 && _view.FrameResources.Fsr3.InternalHistoryWrite.IsValid)
			{
				_passSet.Fsr3Pass.AddNodes(graph, _view);
			}
			else if (_view.FrameResources.Config.AntiAliasing.Enabled && _view.FrameResources.HistoryColorWrite.IsValid)
			{
				_temporalAntiAliasingPass.AddNodes(graph, _view.FrameResources);
				_temporalHistoryStorePass.AddNodes(graph, _view.FrameResources);
			}

			// Capture the finished HDR scene color so next frame's reflections have shaded,
			// pre-filtered radiance to sample.
			_colorPyramidPass.AddNodes(graph, _view.FrameResources);

			if (_view.FrameResources.BloomDownsampleLevels?.Length > 0)
			{
				_bloomPass.AddNodes(graph, _view.FrameResources);
			}

			_tonemappingPass.AddNodes(graph, _view.FrameResources);

			if (_view.FrameResources.Config.AntiAliasing.UsesCasSharpening)
			{
				_casSharpenPass.AddNodes(graph, _view.FrameResources);
			}

			_copyToFinalPass.AddNodes(graph, _view.FrameResources, _sharedResources, _view.Output);

			if (_view.FrameResources.GBufferDepth.IsValid)
			{
				_selectionOutlinePass.AddNodes(graph, _view.FrameResources);
			}

			// Gameplay screen UI belongs to the game's view, not to previews or documents.
			if (_view.OwnsPresentation &&
			    ReferenceEquals(_gameplayUiFrame.Screen, UiFrameData.Empty) == false &&
			    _gameplayUiFrame.Screen.CommandCount > 0)
			{
				// Captures and the editor viewport use the encoded scene texture. Standalone
				// also composites into the window target. CSS colors are in display space.
				graph.AddPass("Gameplay UI Screen Capture", PassKind.Graphics)
					.WriteTexture(_view.FrameResources.EncodedSceneColor, ResourceState.RenderTarget)
					.SetExecute(_gameplayScreenEncodedUiExecute);
				if (_view.Output == RenderViewOutput.Backbuffer)
				{
					graph.AddPass("Gameplay UI Screen", PassKind.Graphics)
						.WriteTexture(_sharedResources.FinalColor, ResourceState.RenderTarget)
						.SetExecute(_gameplayScreenFinalUiExecute);
				}
			}

			if (_view.FrameResources.MotionVectorDebugColor.IsValid)
			{
				_motionVectorDebugPass.AddNodes(graph, _view.FrameResources);
			}
		}
	}

	private void RecordSharedPresentationPasses(RenderGraph graph)
	{
		// The editor UI is drawn after the graph, in a submission of its own (see UiCompositor), so it cannot
		// declare what it samples. This pass declares it instead: it leaves the window target ready to be drawn
		// into, and every view's output readable by the UI. It records no work; its barriers are the point.
		var handoffBuilder = graph.AddPass("UI Handoff", PassKind.Graphics)
			.WriteTexture(_sharedResources.FinalColor, ResourceState.RenderTarget);
		var boundView = _view;
		for (var i = 0; i < _frameViews.Count; i++)
		{
			_view = GetOrCreateViewState(_frameViews[i]);
			var selectedSceneDebugViewHandle = GetSelectedSceneDebugViewHandle();
			if (selectedSceneDebugViewHandle.IsValid)
			{
				handoffBuilder.ReadTexture(selectedSceneDebugViewHandle, ResourceState.ShaderResource);
			}

			var sceneColorDebugViewHandle = GetSceneColorDebugViewHandle();
			if (sceneColorDebugViewHandle.IsValid && sceneColorDebugViewHandle.Id != selectedSceneDebugViewHandle.Id)
			{
				handoffBuilder.ReadTexture(sceneColorDebugViewHandle, ResourceState.ShaderResource);
			}
		}

		_view = boundView;
		handoffBuilder.SetExecute(static _ => { });
	}

	/// <summary>Starts resolving this frame's view outputs: every view begins the frame with none.</summary>
	public void BeginViewportResolve()
	{
		Array.Clear(_viewportTextureIds);
		Array.Clear(_viewportOpaque);
		Array.Clear(_viewportPersistent);
	}

	/// <summary>
	/// Starts a frame that runs no graph. Each view keeps the output it resolved on the last frame that did, but
	/// only where that output outlives its frame: a debug view samples a transient texture that has since been
	/// returned to the pool, so such a view shows nothing until the next frame draws it again.
	/// </summary>
	public void KeepPersistentViewportOutputs()
	{
		for (var i = 0; i < _viewportTextureIds.Length; i++)
		{
			if (_viewportPersistent[i] == false)
			{
				_viewportTextureIds[i] = 0;
				_viewportOpaque[i] = false;
			}
		}
	}

	/// <summary>
	/// Resolves the bound view's output texture and the render state it publishes. Runs for every view in the
	/// frame, including one that recorded no passes, so a hidden view publishes an empty state rather than
	/// keeping last frame's.
	/// </summary>
	public void PrepareSceneViewport(ViewProjection viewProjection)
	{
		if (_view.FrameResources.SceneEnabled == false)
		{
			_viewportTextureIds[_view.View.Index] = 0;
			_viewportOpaque[_view.View.Index] = false;
			_view.ResolvedSceneViewportState = SceneViewportRenderState.Empty;
			return;
		}

		var textureId = ResolveSceneViewportTextureId(out var activeDebugViewId, out var activeDebugViewKind);
		_viewportTextureIds[_view.View.Index] = textureId;
		_viewportOpaque[_view.View.Index] = activeDebugViewKind == SceneDebugViewKind.ColorIgnoreAlpha;
		// Final Color of a texture-output view is its persistent presented target; every other output is transient.
		_viewportPersistent[_view.View.Index] = textureId != 0 &&
		                                        _view.Output == RenderViewOutput.Texture &&
		                                        string.Equals(activeDebugViewId, SceneDebugViewIds.FinalColor, StringComparison.Ordinal);
		_view.ResolvedSceneViewportState = new SceneViewportRenderState(
			textureId,
			_view.FrameResources.SceneFramebufferSize,
			_view.ResolvedProjection,
			_view.SceneDebugViewOptions,
			activeDebugViewId,
			viewProjection);
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
		_gpuDrawPass.ReleaseViewCommandSets(view.Index, device);
		_transparentForwardPass.ReleaseViewCommandSets(view.Index, device);
		_shadowMapPass.ReleaseViewCommandSets(view.Index, device);
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

	/// <summary>
	/// The bound view's sky, prepared from its sun, settings and HDRI so it regenerates only when those change:
	/// where its radiance is read from, and its irradiance and prefilter handles.
	/// </summary>
	/// <summary>Whether a config draws volumetric clouds: they are on and the sky is the Procedural atmosphere.</summary>
	internal static bool UsesClouds(RenderConfig config)
	{
		return config.Clouds.Enabled && config.Sky.Mode == SkyConfigMode.Procedural;
	}

	/// <summary>
	/// Advances the bound view's clouds by one frame and sets up their resources: the shared noise, the history at
	/// trace resolution, the shadow map, and the cloud environment when the sky environment is redrawn. Also
	/// requests that redraw when the clouds moved or changed, or when clouds were turned off and the environment
	/// still shows them.
	/// </summary>
	private CloudViewResources PrepareViewClouds(
		IGfxDevice device,
		RenderConfig config,
		Vector3 cameraPosition,
		Int2 sceneFramebufferSize,
		in AtmosphereViewHandles atmosphere,
		in SkyEnvironmentSource skyEnvironment)
	{
		var viewClouds = _view.Clouds;
		if (UsesClouds(config) == false || atmosphere.Transmittance.IsValid == false)
		{
			_view.ReleaseCloudHistoryResources();
			if (viewClouds.EnvironmentHasClouds)
			{
				_view.Sky.ShouldRecordLightingUpdate = true;
				viewClouds.MarkEnvironmentCleared();
			}

			return default;
		}

		var settings = config.Clouds;
		viewClouds.Advance(settings, _view.SceneFrameDeltaSeconds);
		var frame = new CloudFrame(settings, atmosphere.Frame, cameraPosition, viewClouds);
		var traceDivisor = frame.Quality.TraceDivisor;
		var traceSize = CloudViewResources.GetTraceSize(sceneFramebufferSize, traceDivisor);

		_view.CloudHistory.Ensure(device, new CloudHistoryShape(traceSize, settings.Quality));
		_view.CloudHistory.Import(_resources);
		ImportCloudNoise(device);

		var environment = default(RenderGraphResourceHandle);
		if (skyEnvironment.ViewEnvironment.IsValid &&
		    (_view.Sky.ShouldRecordLightingUpdate || viewClouds.EnvironmentChanged(settings, cameraPosition)))
		{
			_view.Sky.ShouldRecordLightingUpdate = true;
			viewClouds.MarkEnvironmentDrawn(settings, cameraPosition);
			var environmentWidth = _view.Sky.Environment?.Descriptor.Width ?? frame.Quality.EnvironmentWidth;
			var environmentSize = CloudPass.GetEnvironmentSize(environmentWidth, frame.Quality);
			environment = _resources.CreateTransientTexture(
				CloudViewResources.DescribeTraceTexture(environmentSize, TextureFormat.Rgba16Float));
		}

		var shadowMap = settings.CastShadows
			? _resources.CreateTransientTexture(CloudViewResources.DescribeTraceTexture(
				new Int2(CloudViewResources.ShadowMapSize, CloudViewResources.ShadowMapSize),
				TextureFormat.Rgba16Float))
			: default;

		return new CloudViewResources
		{
			Enabled = true,
			Frame = frame,
			TraceSize = traceSize,
			TraceDivisor = traceDivisor,
			Weather = _cloudWeather,
			Shape = _cloudShape,
			Detail = _cloudDetail,
			CurrentColor = _resources.CreateTransientTexture(
				CloudViewResources.DescribeTraceTexture(traceSize, CloudHistorySlot.GetFormat(CloudHistorySlot.Color))),
			CurrentDepth = _resources.CreateTransientTexture(
				CloudViewResources.DescribeTraceTexture(traceSize, CloudHistorySlot.GetFormat(CloudHistorySlot.Depth))),
			HistoryColorRead = _view.CloudHistory.Previous(CloudHistorySlot.Color),
			HistoryDepthRead = _view.CloudHistory.Previous(CloudHistorySlot.Depth),
			HistoryColorWrite = _view.CloudHistory.Current(CloudHistorySlot.Color),
			HistoryDepthWrite = _view.CloudHistory.Current(CloudHistorySlot.Depth),
			HistoryValid = _view.CloudHistory.IsValid,
			ShadowMap = shadowMap,
			ShadowMapCentre = CloudViewResources.GetShadowMapCentre(cameraPosition),
			Environment = environment
		};
	}

	private void ImportCloudNoise(IGfxDevice device)
	{
		if (_cloudNoiseImported)
		{
			return;
		}

		_cloudPass.EnsureNoiseTextures(device);
		var state = _cloudPass.NoiseInitialState;
		_cloudWeather = _resources.ImportTexture(_cloudPass.Weather, takeOwnership: false, initialState: state);
		_cloudShape = _resources.ImportTexture(_cloudPass.Shape, takeOwnership: false, initialState: state);
		_cloudDetail = _resources.ImportTexture(_cloudPass.Detail, takeOwnership: false, initialState: state);
		_cloudNoiseImported = true;
	}

	/// <summary>Generates the shared cloud noise once, in the first view that draws clouds.</summary>
	private void RecordCloudNoise(RenderGraph graph)
	{
		if (_view.FrameResources.Clouds.Enabled == false ||
		    _cloudPass.ShouldRecordNoise == false ||
		    _cloudNoiseRecorded)
		{
			return;
		}

		_cloudPass.AddNoiseNodes(graph, _view.FrameResources.Clouds);
		_cloudNoiseRecorded = true;
	}

	/// <summary>The bound view's cloud shadow map, march and temporal accumulation, ahead of fog and lighting.</summary>
	private (SkyEnvironmentSource Environment, RenderGraphResourceHandle Irradiance, RenderGraphResourceHandle Prefilter, AtmosphereViewHandles Atmosphere) ImportViewSky(
		Vector3 sunDirection,
		Vector3 sunIlluminance,
		Vector3 cameraPosition,
		SkyConfig skyConfig,
		Texture? skyHdri)
	{
		var sky = _view.Sky;
		_skyboxPass.PrepareSky(_renderer.GetGfxDevice(), sky, sunDirection, sunIlluminance, cameraPosition, skyConfig, skyHdri);

		var environment = sky.Environment is not null
			? _resources.ImportTexture(sky.Environment, takeOwnership: false, initialState: sky.EnvironmentInitialState)
			: default;
		var lightingState = sky.LightingInitialState;

		var atmosphere = default(AtmosphereViewHandles);
		if (sky.UsesAtmosphere)
		{
			var lutState = sky.AtmosphereLutsInitialState;
			var viewLutState = sky.AtmosphereViewLutsInitialState;
			atmosphere = new AtmosphereViewHandles
			{
				Frame = sky.Atmosphere,
				Transmittance = _resources.ImportTexture(sky.TransmittanceLut!, takeOwnership: false, initialState: lutState),
				MultipleScattering = _resources.ImportTexture(sky.MultipleScatteringLut!, takeOwnership: false, initialState: lutState),
				SkyView = _resources.ImportTexture(sky.SkyViewLut!, takeOwnership: false, initialState: viewLutState),
				AerialPerspective = _resources.ImportTexture(sky.AerialPerspective!, takeOwnership: false, initialState: viewLutState)
			};
		}

		return (
			SkyboxPass.GetEnvironmentSource(sky, environment),
			_resources.ImportTexture(sky.Irradiance!, takeOwnership: false, initialState: lightingState),
			_resources.ImportTexture(sky.Prefilter!, takeOwnership: false, initialState: lightingState),
			atmosphere);
	}

	/// <summary>What every view in this frame shares; set by <see cref="BeginSharedFrame"/>.</summary>
	internal RenderFrameSharedResources SharedResources => _sharedResources;

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

	private nint ResolveSceneViewportTextureId(out string activeDebugViewId, out SceneDebugViewKind activeDebugViewKind)
	{
		var resolvedView = GetResolvedSceneDebugView();
		if (resolvedView.HasValue)
		{
			var descriptorHandle = ResolveSceneDebugTextureHandle(resolvedView.Value);
			if (descriptorHandle.IsValid)
			{
				activeDebugViewId = resolvedView.Value.Id;
				activeDebugViewKind = resolvedView.Value.Kind;
				return (nint)descriptorHandle.Value;
			}
		}

		if (TryGetSceneDebugView(SceneDebugViewIds.FinalColor, out var sceneColorView))
		{
			var fallbackTextureId = ResolveSceneDebugTextureHandle(sceneColorView);
			activeDebugViewId = SceneDebugViewIds.FinalColor;
			activeDebugViewKind = sceneColorView.Kind;
			return fallbackTextureId.IsValid ? (nint)fallbackTextureId.Value : 0;
		}

		activeDebugViewId = SceneDebugViewIds.FinalColor;
		activeDebugViewKind = SceneDebugViewKind.Color;
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
	/// Rewrites the UI frame's viewport sentinels to the outputs the views resolved to. Once per presented frame,
	/// after every view has been prepared on a frame that runs the graph, or after
	/// <see cref="KeepPersistentViewportOutputs"/> on one that does not.
	/// </summary>
	public void ResolveUiViewportTextures(UiFrameData uiFrame) =>
		ResolveViewportTextureIds(uiFrame, _viewportTextureIds, _viewportOpaque);

	/// <summary>
	/// Rewrites every viewport sentinel in the UI frame to the output its view resolved to this frame. The UI
	/// frame is built on the game thread, before the render thread knows those texture ids, so each view's
	/// image carries a sentinel until here. A view that resolved to nothing is left at zero, which the
	/// backends draw with their fallback texture.
	/// </summary>
	private static void ResolveViewportTextureIds(UiFrameData uiFrame, nint[] textureIdsByViewIndex, bool[] opaqueByViewIndex)
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
				textureIdsByViewIndex[view.Index],
				command.Atlas,
				command.DistanceRange,
				command.Solid,
				opaqueByViewIndex[view.Index]);
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
        context.CommandList.BeginEvent("Skeletal deformation");
		_skinningPass.Record(
			context.CommandList,
			device,
			_renderer,
			skinningPackets,
			context.ViewSnapshot.BoneMatrices,
			context.GpuDrawDatabase);
        context.CommandList.EndEvent();

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
				if (_skinningPass.WasDeformed(skinningPackets[i].InstanceMesh))
                    _rayTracingScene.Resources.QueueSkinnedInstanceRebuild(skinningPackets[i].InstanceMesh);
			}

			using var profile = FrameProfiler.Instance.Measure("Animation acceleration structures");
            _rayTracingScene.Resources.RecordUpdate(context, _renderer, _view.RayTracingUpdates);
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
			MeshLodCullSettings.ForShadows(sceneData, _view.FrameResources.Config.MeshLod),
			casterPlanes[..(shadowData.CascadeCount * FrustumCulling.MaxPlaneCount)],
			casterPlaneCounts[..shadowData.CascadeCount]);

		// Encoding and compaction both belong here rather than in the shadow pass: compaction is compute
		// work, and it has to complete before the render pass that executes its output begins.
		var device = _renderer.GetGfxDevice();
		for (var cascadeIndex = 0; cascadeIndex < shadowData.CascadeCount; cascadeIndex++)
		{
			EnsureShadowIndirectCommands(device, cascadeIndex);
			var compacted = _gpuDrawPass.RecordIndirectCompaction(
				context,
				_shadowMapPass.GetIndirectCommandSet(cascadeIndex, _gpuDrawResources.ActiveViewIndex),
				DrawPassParticipation.ShadowCaster,
				_gpuDrawResources.ShadowDrawArgsBuffer,
				_gpuDrawResources.GetShadowDrawArgsOffsetBytes(cascadeIndex),
				lane => _shadowMapPass.HasIndirectLane(cascadeIndex, lane));
			_shadowMapPass.SetCompactedExecution(cascadeIndex, compacted);
		}
	}

	private void EnsureShadowIndirectCommands(IGfxDevice device, int cascadeIndex)
	{
		_shadowMapPass.EnsureIndirectResources(device, cascadeIndex, _gpuDrawResources.ActiveViewIndex);
		_gpuDrawPass.EnsureIndirectCommands(
			_shadowMapPass.GetIndirectCommandSet(cascadeIndex, _gpuDrawResources.ActiveViewIndex),
			SharedDrawIndirectEncodeResources.FromGpuDrawResources(
				_gpuDrawResources,
				_gpuDrawResources.ShadowDrawArgsBuffer,
				_gpuDrawResources.GetShadowDrawArgsOffsetBytes(cascadeIndex),
				_view.FrameResources.Config.ShadowMaps.WeldShadowVertices ? MeshIndexStream.ShadowOpaque : MeshIndexStream.Main,
				_gpuDrawResources.ShadowVisibleInstanceBuffer));
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
		_gpuDrawPass.RecordCull(context, context.SceneData, _view.FrameResources.Config.MeshLod);
		_gpuDrawPass.RecordGBufferIndirectCompaction(context);
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
		_gpuDrawPass.EnsureGBufferIndirectCommands();
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
			VisibleInstanceBuffer = _gpuDrawResources.VisibleInstanceBuffer,
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
			CameraBuffer = _gpuDrawResources.CameraBuffer
		};

		GBufferPass.Record(context, gbufferConfig, context.SceneData);
	}

	private ShadowFrameData GetShadowFrameData() => _shadowMapPass.GetFrameData(_view.FrameResources.Config.ShadowMaps);

	/// <summary>
	/// The size of the temporally resolved scene colour. FSR3 reconstructs at display size; TAA resolves at render
	/// size and leaves tonemapping to filter the image up to the display.
	/// </summary>
	internal static Int2 GetResolvedSceneColorSize(AntiAliasingConfig antiAliasing, Int2 displaySize, Int2 renderSize)
	{
		return antiAliasing.UsesFsr3
			? displaySize
			: renderSize;
	}

	private void ExecuteTransparentForward(RenderGraphContext context)
	{
		var device = _renderer.GetGfxDevice();
		_transparentForwardPass.EnsureIndirectResources(device, _gpuDrawResources.ActiveViewIndex);
		_gpuDrawPass.EnsureIndirectCommands(
			_transparentForwardPass.GetIndirectCommandSet(_gpuDrawResources.ActiveViewIndex),
			SharedDrawIndirectEncodeResources.FromGpuDrawResources(_gpuDrawResources));
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

	private void ExecuteGameplayScreenEncodedUi(RenderGraphContext context) =>
		ExecuteGameplayScreenUi(context, _view.FrameResources.EncodedSceneColor);

	private void ExecuteGameplayScreenFinalUi(RenderGraphContext context) =>
		ExecuteGameplayScreenUi(context, _sharedResources.FinalColor);

	private void ExecuteGameplayScreenUi(RenderGraphContext context, RenderGraphResourceHandle targetHandle)
	{
		var target = context.GetTexture(targetHandle);
		_gameplayUiRenderer.EnsureResources(_renderer.GetGfxDevice(), _gameplayUiFrame.Screen);
		_gameplayUiRenderer.Record(context.CommandList, _gameplayUiFrame.Screen, target, clearTarget: false);
	}

	private void ExecuteGameplayTextureUi(RenderGraphContext context, GameplayTextureTarget target)
	{
		_gameplayUiRenderer.EnsureResources(_renderer.GetGfxDevice(), target.Surface.Frame);
		_gameplayUiRenderer.Record(
			context.CommandList,
			target.Surface.Frame,
			target.Texture,
			clearTarget: true,
			target.Surface.ClearColor);

		// A surface recorded before its images were uploaded is redrawn once they are.
		if (target.Surface.Frame.HasPendingImages())
		{
			return;
		}

		_gameplayUiRenderer.MarkRendered(target.Surface);
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

	/// <summary>
	/// The bound view's display-encoded image. A texture-output view encodes into its persistent presented target,
	/// which the UI keeps sampling on frames that draw no scene; a backbuffer view's image lives only this frame.
	/// </summary>
	private RenderGraphResourceHandle CreateEncodedSceneColor(Int2 framebufferSize)
	{
		if (_view.Output == RenderViewOutput.Texture)
		{
			var presentedTarget = _view.PresentedSceneTarget.EnsureTarget(_renderer.GetGfxDevice(), framebufferSize);
			return _resources.ImportTexture(
				presentedTarget,
				takeOwnership: false,
				initialState: _view.PresentedSceneTarget.CurrentState);
		}

		return _resources.CreateTransientTexture(new TextureDescriptor(
			framebufferSize.X,
			framebufferSize.Y,
			TextureFormat.Bgra8Unorm,
			TextureUsage.RenderTarget | TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
			new ColorRGBA(0.05f, 0.05f, 0.05f, 1.0f)));
	}

	public void CompleteFrame()
	{
		if (_view.Output == RenderViewOutput.Texture && _view.FrameResources.EncodedSceneColor.IsValid)
		{
			_view.PresentedSceneTarget.SetCurrentState(_resources.GetResourceState(_view.FrameResources.EncodedSceneColor));
		}

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

		// Companion state is committed first, while IsImported still says whether this frame ran the effect.
		if (_view.Fsr3History.IsImported)
		{
			_view.Fsr3FrameInfoState = _resources.GetResourceState(_view.FrameResources.Fsr3.FrameInfo);
			_view.Fsr3FrameIndex++;
		}

		if (_view.DdgiHistory.IsImported)
		{
			_view.DdgiProbeActivityState = _resources.GetResourceState(_view.FrameResources.DdgiProbeActivity);
			_view.DdgiIrradianceEstimatorState = _resources.GetResourceState(_view.FrameResources.DdgiIrradianceEstimator);
			_view.DdgiCommittedRuntimeOrigin = _view.FrameResources.DdgiRuntimeOrigin;
			_view.DdgiCommittedStorageOffset = _view.FrameResources.DdgiStorageOffset;
			_view.DdgiCommittedPlacementValid = true;
		}

		_view.CompleteHistories(_resources);
	}

	internal static TextureDescriptor CreateFogTextureDescriptor(Int3 grid) => new(
		grid.X,
		grid.Y,
		TextureFormat.Rgba16Float,
		TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
		new ColorRGBA(0.0f, 0.0f, 0.0f, 1.0f),
		dimension: TextureDimension.Texture3D,
		depth: grid.Z);

	private RenderGraphResourceHandle CreateFinalGatherTexture(Int2 size)
	{
		return _resources.CreateTransientTexture(new TextureDescriptor(
			size.X,
			size.Y,
			TextureFormat.Rgba16Float,
			TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
			new ColorRGBA(0.0f, 0.0f, 0.0f, 0.0f)));
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

	/// <summary>Allocates FSR3's history for the given sizes. Returns true when it was (re)allocated.</summary>
	private bool EnsureFsr3HistoryResources(IGfxDevice device, Int2 renderSize, Int2 upscaleSize)
	{
		var previousDevice = _view.Fsr3History.Device;
		var shape = new Fsr3HistoryShape(renderSize, upscaleSize);
		if (_view.Fsr3History.Ensure(device, shape) == false &&
		    _view.Fsr3FrameInfoTexture is not null)
		{
			return false;
		}

		// The frame info and frame index describe the accumulation in the history, so they restart with it.
		if (_view.Fsr3FrameInfoTexture is not null)
		{
			TemporalResourceRetirement.RetireTexture(previousDevice, _view.Fsr3FrameInfoTexture, _view.Fsr3FrameInfoState);
		}

		_view.Fsr3FrameInfoTexture = device.CreateTexture(new TextureDescriptor(
			1,
			1,
			TextureFormat.Rgba16Float,
			TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
			new ColorRGBA(0.0f, 0.0f, 0.0f, 0.0f)));
		_view.Fsr3FrameInfoState = ResourceState.UnorderedAccess;
		_view.Fsr3FrameIndex = 0;

		return true;
	}

	private void EnsureDdgiHistoryResources(IGfxDevice device, DdgiHistoryShape shape)
	{
		var previousDevice = _view.DdgiHistory.Device;
		if (_view.DdgiHistory.Ensure(device, shape) == false &&
		    _view.DdgiProbeActivityTexture is not null &&
		    _view.DdgiIrradianceEstimatorBuffer is not null)
		{
			return;
		}

		// The activity texture, estimator and committed placement describe the probes the history held, so they
		// restart with it.
		_view.ReleaseDdgiCompanionResources(previousDevice);

		var shCoefficientTextureSize = DdgiUtilities.GetShCoefficientTextureSize(shape.GridShape);
		_view.DdgiProbeActivityTexture = device.CreateTexture(new TextureDescriptor(
			shCoefficientTextureSize.X,
			shCoefficientTextureSize.Y,
			TextureFormat.Rgba16Float,
			TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
			new ColorRGBA(0.0f, 0.0f, 0.0f, 0.0f)));
		_view.DdgiProbeActivityState = ResourceState.UnorderedAccess;
		_view.DdgiIrradianceEstimatorBuffer = device.CreateBuffer(new BufferDescriptor(
			DdgiUtilities.GetIrradianceEstimatorBufferSize(shape.GridShape),
			BufferUsage.Structured,
			BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource));
		_view.DdgiIrradianceEstimatorState = ResourceState.UnorderedAccess;
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

/// <summary>A view's Procedural-sky atmosphere LUTs for one frame.</summary>
internal readonly struct AtmosphereViewHandles
{
	/// <summary>This frame's atmosphere: its medium, the camera's altitude and the sun.</summary>
	public AtmosphereFrame Frame { get; init; }
	public RenderGraphResourceHandle Transmittance { get; init; }
	public RenderGraphResourceHandle MultipleScattering { get; init; }
	public RenderGraphResourceHandle SkyView { get; init; }
	public RenderGraphResourceHandle AerialPerspective { get; init; }
}

using System.Diagnostics.CodeAnalysis;
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

public readonly struct RenderGraphFrameResources
{
	public Int2 FramebufferSize { get; init; }
	public Int2 SceneFramebufferSize { get; init; }
	public bool SceneEnabled { get; init; }
	public RenderGraphResourceHandle TonemappedLinearSceneColor { get; init; }
	public RenderGraphResourceHandle DisplayLinearSceneColor { get; init; }
	public RenderGraphResourceHandle EncodedSceneColor { get; init; }
	public RenderGraphResourceHandle FinalColor { get; init; }
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
	public RenderGraphResourceHandle SkyboxEnvironment { get; init; }
	public RenderGraphResourceHandle SkyboxIrradiance { get; init; }
	public RenderGraphResourceHandle SkyboxPrefilter { get; init; }
	public RenderGraphResourceHandle SkyboxBrdfLut { get; init; }
	public RenderConfig Config { get; init; }
}

internal sealed class RenderGraphFrameBuilder
{
	private readonly RenderGraphPassSet _passSet;
	private readonly struct SceneDebugViewRegistration
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
	private readonly DeferredLightingPass _deferredLightingPass;
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
	private RenderGraphFrameResources _frameResources;
	private UiFrameData _uiFrame = UiFrameData.Empty;
	private GameplayUiRenderFrame _gameplayUiFrame = GameplayUiRenderFrame.Empty;
	private readonly List<GameplayTextureTarget> _gameplayTextureTargets = [];

	private readonly record struct GameplayTextureTarget(
		GameplayUiTextureSurfaceFrame Surface,
		RenderGraphResourceHandle Handle,
		IGfxTexture Texture);
	private readonly List<SceneDebugViewRegistration> _sceneDebugViews = [];
	private readonly List<GpuDrawUpdate> _frameGpuDrawUpdates = [];
	private SceneDebugViewOption[] _sceneDebugViewOptions = Array.Empty<SceneDebugViewOption>();
	private string _requestedSceneDebugViewId = SceneDebugViewIds.FinalColor;
	private SceneViewportRenderState _resolvedSceneViewportState = SceneViewportRenderState.Empty;
	private bool _hasPreviousFrameShape;
	private Int2 _previousFramebufferSize;
	private Int2 _previousSceneFramebufferSize;
	private int _previousShadowMapResolution;
	private bool _previousSceneEnabled;
	private bool _previousTaaEnabled;
	private bool _historyValid;
	private bool _ddgiHistoryValid;
	private bool _resetTaaHistoryThisFrame;
	private IGfxDevice? _historyDevice;
	private GraphicsBackendKind? _historyBackendKind;
	private Int2 _historySize;
	private int _historyReadIndex;
	private readonly IGfxTexture?[] _historyColorTextures = new IGfxTexture?[2];
	private readonly IGfxTexture?[] _historyDepthTextures = new IGfxTexture?[2];
	private readonly ResourceState[] _historyColorStates = new ResourceState[2];
	private readonly ResourceState[] _historyDepthStates = new ResourceState[2];
	private readonly IGfxTexture?[] _fsr3CurrentLumaTextures = new IGfxTexture?[2];
	private readonly IGfxTexture?[] _fsr3AccumulationTextures = new IGfxTexture?[2];
	private readonly ResourceState[] _fsr3CurrentLumaStates = new ResourceState[2];
	private readonly ResourceState[] _fsr3AccumulationStates = new ResourceState[2];
	private IGfxTexture? _fsr3FrameInfoTexture;
	private ResourceState _fsr3FrameInfoState = ResourceState.UnorderedAccess;
	private uint _fsr3FrameIndex;
	private IGfxDevice? _colorPyramidDevice;
	private GraphicsBackendKind? _colorPyramidBackendKind;
	private Int2 _colorPyramidSize;
	private bool _colorPyramidValid;
	private IGfxTexture[] _colorPyramidTextures = Array.Empty<IGfxTexture>();
	private ResourceState[] _colorPyramidStates = Array.Empty<ResourceState>();
	private IGfxDevice? _ddgiHistoryDevice;
	private GraphicsBackendKind? _ddgiHistoryBackendKind;
	private DdgiGridShape _ddgiHistoryGridShape;
	private int _ddgiHistoryReadIndex;
	private const int DdgiShCoefficientCount = DdgiUtilities.ShCoefficientCount;
	private readonly IGfxTexture?[,] _ddgiIrradianceTextures = new IGfxTexture?[DdgiShCoefficientCount, 2];
	private readonly IGfxTexture?[] _ddgiVisibilityTextures = new IGfxTexture?[2];
	private readonly IGfxTexture?[] _ddgiProbeStateTextures = new IGfxTexture?[2];
	private IGfxTexture? _ddgiProbeActivityTexture;
	private IGfxBuffer? _ddgiIrradianceEstimatorBuffer;
	private readonly ResourceState[,] _ddgiIrradianceStates = new ResourceState[DdgiShCoefficientCount, 2];
	private readonly ResourceState[] _ddgiVisibilityStates = new ResourceState[2];
	private readonly ResourceState[] _ddgiProbeStateStates = new ResourceState[2];
	private ResourceState _ddgiProbeActivityState = ResourceState.Common;
	private ResourceState _ddgiIrradianceEstimatorState = ResourceState.Common;
	private Vector3 _ddgiHistoryLatticeAnchor;
	private float _ddgiHistoryProbeSpacing;
	private Vector3 _ddgiCommittedRuntimeOrigin;
	private Int3 _ddgiCommittedStorageOffset;
	private bool _ddgiCommittedPlacementValid;
	private DdgiPassConfig _currentDdgiConfig;
	private bool _currentDdgiConfigValid;
	
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
	private readonly Action<RenderGraphContext> _deferredLightingExecute;
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
		IShaderProvider shaderProvider)
	{
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
		_deferredLightingPass = passSet.DeferredLightingPass;
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
		_deferredLightingExecute = ExecuteDeferredLighting;
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
		if (RequiresRayTracingScene(config) && (device.SupportsRayTracing == false || _renderer.GetPackedMeshIndexBuffer() is null))
		{
			config = CreateRayTracingDisabledConfig(config);
		}

		var taaEnabled = config.Fsr3.Enabled;
		var frameShapeChanged = _hasPreviousFrameShape == false ||
		                        _previousFramebufferSize.X != framebufferSize.X ||
		                        _previousFramebufferSize.Y != framebufferSize.Y ||
		                        _previousSceneFramebufferSize.X != sceneFramebufferSize.X ||
		                        _previousSceneFramebufferSize.Y != sceneFramebufferSize.Y ||
		                        _previousSceneEnabled != sceneEnabled;
		var shadowMapResolution = Math.Max(1, config.ShadowMaps.CascadeResolution);
		InvalidateTransientPoolIfFrameShapeChanged(framebufferSize, sceneFramebufferSize, shadowMapResolution, sceneEnabled);
		_sceneDebugViews.Clear();
		_sceneDebugViewOptions = Array.Empty<SceneDebugViewOption>();
		_resolvedSceneViewportState = SceneViewportRenderState.Empty;
		_currentDdgiConfigValid = false;
		_resetTaaHistoryThisFrame = frameShapeChanged || (taaEnabled && _previousTaaEnabled == false);
		_previousTaaEnabled = taaEnabled;
		_skyboxPass.PrepareFrame(_renderer.GetGfxDevice(), sunDirection, sunIntensityScale, config.SkyboxConfig);
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
			if (IsMotionVectorDebugView(_requestedSceneDebugViewId))
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
				if (_colorPyramidTextures.Length > 0)
				{
					colorPyramidLevelHandles = new RenderGraphResourceHandle[_colorPyramidTextures.Length];
					for (var level = 0; level < _colorPyramidTextures.Length; level++)
					{
						colorPyramidLevelHandles[level] = _resources.ImportTexture(
							_colorPyramidTextures[level],
							takeOwnership: false,
							initialState: _colorPyramidStates[level]);
					}
					colorPyramidHistoryValid = _colorPyramidValid;
				}
				else
				{
					_colorPyramidValid = false;
				}
			}

			if (taaEnabled)
			{
				EnsureTemporalHistoryResources(_renderer.GetGfxDevice(), sceneFramebufferSize);
				var historyWriteIndex = 1 - _historyReadIndex;
				if (_historyColorTextures[_historyReadIndex] is IGfxTexture historyColorRead &&
				    _historyColorTextures[historyWriteIndex] is IGfxTexture historyColorWrite &&
				    _historyDepthTextures[_historyReadIndex] is IGfxTexture historyDepthRead &&
				    _historyDepthTextures[historyWriteIndex] is IGfxTexture historyDepthWrite)
				{
					historyColorReadHandle = _resources.ImportTexture(
						historyColorRead,
						takeOwnership: false,
						initialState: _historyColorStates[_historyReadIndex]);
					historyColorWriteHandle = _resources.ImportTexture(
						historyColorWrite,
						takeOwnership: false,
						initialState: _historyColorStates[historyWriteIndex]);
					historyDepthReadHandle = _resources.ImportTexture(
						historyDepthRead,
						takeOwnership: false,
						initialState: _historyDepthStates[_historyReadIndex]);
					historyDepthWriteHandle = _resources.ImportTexture(
						historyDepthWrite,
						takeOwnership: false,
						initialState: _historyDepthStates[historyWriteIndex]);
				}
				else
				{
					_resetTaaHistoryThisFrame = true;
					_historyValid = false;
				}

				var writeIndex = 1 - _historyReadIndex;
				if (_fsr3CurrentLumaTextures[_historyReadIndex] is IGfxTexture currentLumaRead &&
				    _fsr3CurrentLumaTextures[writeIndex] is IGfxTexture currentLumaWrite &&
				    _fsr3AccumulationTextures[_historyReadIndex] is IGfxTexture accumulationRead &&
				    _fsr3AccumulationTextures[writeIndex] is IGfxTexture accumulationWrite)
				{
					var currentLumaReadHandle = _resources.ImportTexture(currentLumaRead, false,
						_fsr3CurrentLumaStates[_historyReadIndex]);
					var currentLumaWriteHandle = _resources.ImportTexture(currentLumaWrite, false,
						_fsr3CurrentLumaStates[writeIndex]);
					var accumulationReadHandle = _resources.ImportTexture(accumulationRead, false,
						_fsr3AccumulationStates[_historyReadIndex]);
					var accumulationWriteHandle = _resources.ImportTexture(accumulationWrite, false,
						_fsr3AccumulationStates[writeIndex]);
					var frameInfoHandle = _resources.ImportTexture(
						_fsr3FrameInfoTexture ?? throw new InvalidOperationException("FSR3 frame info was not allocated."),
						false, _fsr3FrameInfoState);
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
						HistoryValid = _historyValid
					};
				}
			}
			else
			{
				fsr3Resources = new Fsr3FrameResources { TransparencyMask = transparencyMaskHandle };
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
				if (_ddgiHistoryValid && _ddgiCommittedPlacementValid)
				{
					ddgiScrollDelta = DdgiUtilities.GetScrollDelta(
						_ddgiCommittedRuntimeOrigin,
						ddgiRuntimeOrigin,
						ddgiProbeSpacing);
					ddgiStorageOffset = DdgiUtilities.AdvanceStorageOffset(
						_ddgiCommittedStorageOffset,
						ddgiScrollDelta,
						ddgiGridShape);
				}
				var ddgiWriteIndex = 1 - _ddgiHistoryReadIndex;
				if (_ddgiIrradianceTextures[0, _ddgiHistoryReadIndex] is IGfxTexture ddgiIrradianceL0Read &&
				    _ddgiIrradianceTextures[0, ddgiWriteIndex] is IGfxTexture ddgiIrradianceL0Write &&
				    _ddgiIrradianceTextures[1, _ddgiHistoryReadIndex] is IGfxTexture ddgiIrradianceLyRead &&
				    _ddgiIrradianceTextures[1, ddgiWriteIndex] is IGfxTexture ddgiIrradianceLyWrite &&
				    _ddgiIrradianceTextures[2, _ddgiHistoryReadIndex] is IGfxTexture ddgiIrradianceLzRead &&
				    _ddgiIrradianceTextures[2, ddgiWriteIndex] is IGfxTexture ddgiIrradianceLzWrite &&
				    _ddgiIrradianceTextures[3, _ddgiHistoryReadIndex] is IGfxTexture ddgiIrradianceLxRead &&
				    _ddgiIrradianceTextures[3, ddgiWriteIndex] is IGfxTexture ddgiIrradianceLxWrite &&
				    _ddgiVisibilityTextures[_ddgiHistoryReadIndex] is IGfxTexture ddgiVisibilityRead &&
				    _ddgiVisibilityTextures[ddgiWriteIndex] is IGfxTexture ddgiVisibilityWrite &&
				    _ddgiProbeStateTextures[_ddgiHistoryReadIndex] is IGfxTexture ddgiProbeStateRead &&
				    _ddgiProbeStateTextures[ddgiWriteIndex] is IGfxTexture ddgiProbeStateWrite &&
				    _ddgiProbeActivityTexture is IGfxTexture ddgiProbeActivity &&
				    _ddgiIrradianceEstimatorBuffer is IGfxBuffer ddgiIrradianceEstimator)
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
						initialState: _ddgiIrradianceEstimatorState);
					ddgiIrradianceL0ReadHandle = _resources.ImportTexture(
						ddgiIrradianceL0Read,
						takeOwnership: false,
						initialState: _ddgiIrradianceStates[0, _ddgiHistoryReadIndex]);
					ddgiIrradianceL0WriteHandle = _resources.ImportTexture(
						ddgiIrradianceL0Write,
						takeOwnership: false,
						initialState: _ddgiIrradianceStates[0, ddgiWriteIndex]);
					ddgiIrradianceLyReadHandle = _resources.ImportTexture(
						ddgiIrradianceLyRead,
						takeOwnership: false,
						initialState: _ddgiIrradianceStates[1, _ddgiHistoryReadIndex]);
					ddgiIrradianceLyWriteHandle = _resources.ImportTexture(
						ddgiIrradianceLyWrite,
						takeOwnership: false,
						initialState: _ddgiIrradianceStates[1, ddgiWriteIndex]);
					ddgiIrradianceLzReadHandle = _resources.ImportTexture(
						ddgiIrradianceLzRead,
						takeOwnership: false,
						initialState: _ddgiIrradianceStates[2, _ddgiHistoryReadIndex]);
					ddgiIrradianceLzWriteHandle = _resources.ImportTexture(
						ddgiIrradianceLzWrite,
						takeOwnership: false,
						initialState: _ddgiIrradianceStates[2, ddgiWriteIndex]);
					ddgiIrradianceLxReadHandle = _resources.ImportTexture(
						ddgiIrradianceLxRead,
						takeOwnership: false,
						initialState: _ddgiIrradianceStates[3, _ddgiHistoryReadIndex]);
					ddgiIrradianceLxWriteHandle = _resources.ImportTexture(
						ddgiIrradianceLxWrite,
						takeOwnership: false,
						initialState: _ddgiIrradianceStates[3, ddgiWriteIndex]);
					ddgiVisibilityReadHandle = _resources.ImportTexture(
						ddgiVisibilityRead,
						takeOwnership: false,
						initialState: _ddgiVisibilityStates[_ddgiHistoryReadIndex]);
					ddgiVisibilityWriteHandle = _resources.ImportTexture(
						ddgiVisibilityWrite,
						takeOwnership: false,
						initialState: _ddgiVisibilityStates[ddgiWriteIndex]);
					ddgiProbeStateReadHandle = _resources.ImportTexture(
						ddgiProbeStateRead,
						takeOwnership: false,
						initialState: _ddgiProbeStateStates[_ddgiHistoryReadIndex]);
					ddgiProbeStateWriteHandle = _resources.ImportTexture(
						ddgiProbeStateWrite,
						takeOwnership: false,
						initialState: _ddgiProbeStateStates[ddgiWriteIndex]);
					ddgiProbeActivityHandle = _resources.ImportTexture(
						ddgiProbeActivity,
						takeOwnership: false,
						initialState: _ddgiProbeActivityState);
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
					_ddgiHistoryValid = false;
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

		var displayLinearSceneColorHandle = sceneEnabled
			? _resources.CreateTransientTexture(new TextureDescriptor(
				framebufferSize.X,
				framebufferSize.Y,
				TextureFormat.Rgba16Float,
				TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
				new ColorRGBA(0.05f, 0.05f, 0.05f, 1.0f)))
			: default;

		var encodedSceneColorHandle = sceneEnabled
			? _resources.CreateTransientTexture(new TextureDescriptor(
				framebufferSize.X,
				framebufferSize.Y,
				TextureFormat.Bgra8Unorm,
				TextureUsage.RenderTarget | TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
				new ColorRGBA(0.05f, 0.05f, 0.05f, 1.0f)))
			: default;

		_frameResources = new()
		{
			FramebufferSize = framebufferSize,
			SceneFramebufferSize = sceneFramebufferSize,
			SceneEnabled = sceneEnabled,
			TonemappedLinearSceneColor = tonemappedLinearSceneColorHandle,
			DisplayLinearSceneColor = displayLinearSceneColorHandle,
			EncodedSceneColor = encodedSceneColorHandle,
			FinalColor = _resources.CreateTransientTexture(new TextureDescriptor(
				framebufferSize.X,
				framebufferSize.Y,
				TextureFormat.Bgra8Unorm,
				TextureUsage.RenderTarget | TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
				new ColorRGBA(0.05f, 0.05f, 0.05f, 1.0f))),
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
				IsDdgiFinalContributionDebugView(_requestedSceneDebugViewId),
			WriteDdgiProbeDebug =
				ddgiProbeBaseWeightDebugHandle.IsValid &&
				ddgiWeightedVisibilityDebugHandle.IsValid &&
				ddgiDominantProbeDebugHandle.IsValid &&
				ddgiDominantProbeCoordDebugHandle.IsValid &&
				ddgiProbeRelocationDebugHandle.IsValid &&
				ddgiProbeRelocationDecisionDebugHandle.IsValid &&
				IsDdgiProbeDebugView(_requestedSceneDebugViewId),
			ShadowMapDepth0 = shadowMapHandle0,
			ShadowMapDepth1 = shadowMapHandle1,
			ShadowMapDepth2 = shadowMapHandle2,
			LightingBuffer = lightingHandle,
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
			SkyboxEnvironment = skyboxEnvHandle,
			SkyboxIrradiance = skyboxIrrHandle,
			SkyboxPrefilter = skyboxPrefilterHandle,
			SkyboxBrdfLut = skyboxBrdfHandle,
			Config = config
		};

		if (sceneEnabled)
		{
			RegisterSceneDebugView(SceneDebugViewIds.FinalColor, "Final Color", _frameResources.EncodedSceneColor, SceneDebugViewKind.Color);
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
			_sceneDebugViewOptions = BuildSceneDebugViewOptions();
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
		_requestedSceneDebugViewId = NormalizeSceneDebugViewId(requestedDebugViewId);
	}

	public SceneViewportRenderState GetSceneViewportRenderState() => _resolvedSceneViewportState;
	

	[SuppressMessage("ReSharper", "RedundantArgumentDefaultValue")]
	public void Build(RenderGraph graph)
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

		if (_frameResources.SceneEnabled)
		{
			graph.AddPass("GpuDraw Update", PassKind.Compute)
				.SetExecute(_gpuDrawUpdateExecute);

			graph.AddPass("GpuDraw Cull (Shadow View)", PassKind.Compute)
				.SetExecute(_gpuDrawShadowCullExecute);

			graph.AddPass("Shadow Map", PassKind.Graphics)
				.WriteTexture(_frameResources.ShadowMapDepth0, ResourceState.DepthWrite)
				.WriteTexture(_frameResources.ShadowMapDepth1, ResourceState.DepthWrite)
				.WriteTexture(_frameResources.ShadowMapDepth2, ResourceState.DepthWrite)
				.SetExecute(_shadowMapExecute);

			graph.AddPass("GpuDraw Cull (Camera View)", PassKind.Compute)
				.SetExecute(_gpuDrawCameraCullExecute);

			var gbufferBuilder = graph.AddPass("GBuffer", PassKind.Graphics)
				.WriteTexture(_frameResources.DecalSourceGBufferAlbedo.IsValid ? _frameResources.DecalSourceGBufferAlbedo : _frameResources.GBufferAlbedo, ResourceState.RenderTarget)
				.WriteTexture(_frameResources.DecalSourceGBufferNormal.IsValid ? _frameResources.DecalSourceGBufferNormal : _frameResources.GBufferNormal, ResourceState.RenderTarget)
				.WriteTexture(_frameResources.DecalSourceGBufferMaterial.IsValid ? _frameResources.DecalSourceGBufferMaterial : _frameResources.GBufferMaterial, ResourceState.RenderTarget)
				.WriteTexture(_frameResources.DecalSourceGBufferEmissive.IsValid ? _frameResources.DecalSourceGBufferEmissive : _frameResources.GBufferEmissive, ResourceState.RenderTarget)
				.WriteTexture(_frameResources.GBufferVelocity, ResourceState.RenderTarget)
				.WriteTexture(_frameResources.GBufferDepth, ResourceState.DepthWrite);
			for (var i = 0; i < _gameplayTextureTargets.Count; i++)
			{
				gbufferBuilder.ReadTexture(_gameplayTextureTargets[i].Handle, ResourceState.ShaderResource);
			}
			gbufferBuilder.SetExecute(_gbufferExecute);

			if (_frameResources.DecalSourceGBufferAlbedo.IsValid)
			{
				graph.AddPass("GBuffer Decal Seed", PassKind.Compute)
					.ReadTexture(_frameResources.DecalSourceGBufferAlbedo, ResourceState.ShaderResource)
					.ReadTexture(_frameResources.DecalSourceGBufferNormal, ResourceState.ShaderResource)
					.ReadTexture(_frameResources.DecalSourceGBufferMaterial, ResourceState.ShaderResource)
					.ReadTexture(_frameResources.DecalSourceGBufferEmissive, ResourceState.ShaderResource)
					.WriteTexture(_frameResources.GBufferAlbedo, ResourceState.UnorderedAccess)
					.WriteTexture(_frameResources.GBufferNormal, ResourceState.UnorderedAccess)
					.WriteTexture(_frameResources.GBufferMaterial, ResourceState.UnorderedAccess)
					.WriteTexture(_frameResources.GBufferEmissive, ResourceState.UnorderedAccess)
					.SetExecute(_gBufferDecalSeedExecute);

				graph.AddPass("ScreenSpaceDecal", PassKind.Graphics)
					.ReadTexture(_frameResources.DecalSourceGBufferAlbedo, ResourceState.ShaderResource)
					.ReadTexture(_frameResources.DecalSourceGBufferNormal, ResourceState.ShaderResource)
					.ReadTexture(_frameResources.DecalSourceGBufferMaterial, ResourceState.ShaderResource)
					.ReadTexture(_frameResources.DecalSourceGBufferEmissive, ResourceState.ShaderResource)
					.ReadTexture(_frameResources.GBufferDepth, ResourceState.ShaderResource)
					.WriteTexture(_frameResources.GBufferAlbedo, ResourceState.RenderTarget)
					.WriteTexture(_frameResources.GBufferNormal, ResourceState.RenderTarget)
					.WriteTexture(_frameResources.GBufferMaterial, ResourceState.RenderTarget)
					.WriteTexture(_frameResources.GBufferEmissive, ResourceState.RenderTarget)
					.SetExecute(_screenSpaceDecalExecute);
			}

			if (_frameResources.AmbientOcclusionRaw.IsValid)
			{
				var ambientOcclusionEvaluateBuilder = graph.AddPass("Ambient Occlusion Evaluate", PassKind.Compute)
					.ReadTexture(_frameResources.GBufferDepth, ResourceState.ShaderResource)
					.ReadTexture(_frameResources.GBufferNormal, ResourceState.ShaderResource)
					.WriteTexture(_frameResources.AmbientOcclusionRaw, ResourceState.UnorderedAccess);
				if (_frameResources.RayTracingHitMask.IsValid)
				{
					ambientOcclusionEvaluateBuilder.WriteTexture(_frameResources.RayTracingHitMask, ResourceState.UnorderedAccess);
				}
				if (_frameResources.RayTracingHitDistance.IsValid)
				{
					ambientOcclusionEvaluateBuilder.WriteTexture(_frameResources.RayTracingHitDistance, ResourceState.UnorderedAccess);
				}
				if (_frameResources.RayTracingAlbedo.IsValid)
				{
					ambientOcclusionEvaluateBuilder.WriteTexture(_frameResources.RayTracingAlbedo, ResourceState.UnorderedAccess);
				}
				ambientOcclusionEvaluateBuilder.SetExecute(_ambientOcclusionExecute);

				graph.AddPass("Ambient Occlusion Blur X", PassKind.Compute)
					.ReadTexture(_frameResources.GBufferDepth, ResourceState.ShaderResource)
					.ReadTexture(_frameResources.GBufferNormal, ResourceState.ShaderResource)
					.ReadTexture(_frameResources.AmbientOcclusionRaw, ResourceState.ShaderResource)
					.WriteTexture(_frameResources.AmbientOcclusionTemp, ResourceState.UnorderedAccess)
					.SetExecute(_ambientOcclusionBlurHorizontalExecute);

				var blurVerticalBuilder = graph.AddPass("Ambient Occlusion Blur Y", PassKind.Compute)
					.ReadTexture(_frameResources.GBufferDepth, ResourceState.ShaderResource)
					.ReadTexture(_frameResources.GBufferNormal, ResourceState.ShaderResource)
					.ReadTexture(_frameResources.AmbientOcclusionTemp, ResourceState.ShaderResource);
				if (_frameResources.Config.AmbientOcclusion.Resolution == AmbientOcclusionResolution.Half)
				{
					blurVerticalBuilder
						.WriteTexture(_frameResources.AmbientOcclusionRaw, ResourceState.UnorderedAccess)
						.SetExecute(_ambientOcclusionBlurVerticalExecute);

					graph.AddPass("Ambient Occlusion Upsample", PassKind.Compute)
						.ReadTexture(_frameResources.GBufferDepth, ResourceState.ShaderResource)
						.ReadTexture(_frameResources.GBufferNormal, ResourceState.ShaderResource)
						.ReadTexture(_frameResources.AmbientOcclusionRaw, ResourceState.ShaderResource)
						.WriteTexture(_frameResources.AmbientOcclusionFinal, ResourceState.UnorderedAccess)
						.SetExecute(_ambientOcclusionUpsampleExecute);
				}
				else
				{
					blurVerticalBuilder
						.WriteTexture(_frameResources.AmbientOcclusionFinal, ResourceState.UnorderedAccess)
						.SetExecute(_ambientOcclusionBlurVerticalExecute);
				}
			}

			if (_useProceduralSkybox && _recordProceduralSkyLighting)
			{
				graph.AddPass("Skybox Environment", PassKind.Compute)
					.WriteTexture(_frameResources.SkyboxEnvironment, ResourceState.UnorderedAccess)
					.SetExecute(_skyboxEnvironmentExecute);

				graph.AddPass("Skybox Irradiance", PassKind.Compute)
					.ReadTexture(_frameResources.SkyboxEnvironment, ResourceState.ShaderResource)
					.WriteTexture(_frameResources.SkyboxIrradiance, ResourceState.UnorderedAccess)
					.SetExecute(_skyboxIrradianceExecute);

				graph.AddPass("Skybox Prefilter", PassKind.Compute)
					.ReadTexture(_frameResources.SkyboxEnvironment, ResourceState.ShaderResource)
					.WriteTexture(_frameResources.SkyboxPrefilter, ResourceState.UnorderedAccess)
					.SetExecute(_skyboxPrefilterExecute);
			}

			if (_frameResources.DdgiTraceIrradiance.IsValid &&
			    _frameResources.DdgiTraceVisibility.IsValid &&
			    _frameResources.DdgiIrradianceEstimator.IsValid &&
			    _frameResources.DdgiIrradianceL0HistoryRead.IsValid &&
				    _frameResources.DdgiIrradianceL0HistoryWrite.IsValid &&
				    _frameResources.DdgiIrradianceLyHistoryRead.IsValid &&
				    _frameResources.DdgiIrradianceLyHistoryWrite.IsValid &&
				    _frameResources.DdgiIrradianceLzHistoryRead.IsValid &&
				    _frameResources.DdgiIrradianceLzHistoryWrite.IsValid &&
				    _frameResources.DdgiIrradianceLxHistoryRead.IsValid &&
				    _frameResources.DdgiIrradianceLxHistoryWrite.IsValid &&
				    _frameResources.DdgiVisibilityHistoryRead.IsValid &&
				    _frameResources.DdgiVisibilityHistoryWrite.IsValid &&
				    _frameResources.DdgiProbeStateRead.IsValid &&
				    _frameResources.DdgiProbeStateWrite.IsValid &&
				    _frameResources.DdgiProbeActivity.IsValid)
			{
				graph.AddPass("DDGI Probe Classify", PassKind.Compute)
					.WriteTexture(_frameResources.DdgiProbeActivity, ResourceState.UnorderedAccess)
					.SetExecute(_ddgiClassifyExecute);

				if (DdgiUtilities.IsRelocationTraceEnabled(_frameResources.Config))
				{
					graph.AddPass("DDGI Relocation Trace", PassKind.Compute)
						.ReadTexture(_frameResources.DdgiProbeActivity, ResourceState.ShaderResource)
						.ReadTexture(_frameResources.DdgiProbeStateRead, ResourceState.ShaderResource)
						.WriteTexture(_frameResources.DdgiTraceVisibility, ResourceState.UnorderedAccess)
						.SetExecute(context => ExecuteDdgiRelocationTrace(context, 0));
				}

				var relocationSolveBuilder = graph.AddPass("DDGI Relocation Solve", PassKind.Compute)
					.ReadTexture(_frameResources.DdgiProbeActivity, ResourceState.ShaderResource)
					.ReadTexture(_frameResources.DdgiProbeStateRead, ResourceState.ShaderResource)
					.WriteTexture(_frameResources.DdgiProbeStateWrite, ResourceState.UnorderedAccess)
					.WriteTexture(_frameResources.DdgiProbeRelocationDecision, ResourceState.UnorderedAccess);
				if (DdgiUtilities.IsRelocationTraceEnabled(_frameResources.Config))
				{
					relocationSolveBuilder.ReadTexture(
						_frameResources.DdgiTraceVisibility,
						ResourceState.ShaderResource);
				}
				relocationSolveBuilder.SetExecute(context => ExecuteDdgiRelocate(context, 0));

				var ddgiTraceBuilder = graph.AddPass("DDGI Probe Trace", PassKind.Compute)
					.ReadTexture(_frameResources.DdgiProbeActivity, ResourceState.ShaderResource)
					.ReadTexture(_frameResources.DdgiProbeStateRead, ResourceState.ShaderResource)
					.ReadTexture(_frameResources.DdgiProbeStateWrite, ResourceState.ShaderResource)
					.ReadTexture(_frameResources.DdgiIrradianceL0HistoryRead, ResourceState.ShaderResource)
					.ReadTexture(_frameResources.DdgiIrradianceLyHistoryRead, ResourceState.ShaderResource)
					.ReadTexture(_frameResources.DdgiIrradianceLzHistoryRead, ResourceState.ShaderResource)
					.ReadTexture(_frameResources.DdgiIrradianceLxHistoryRead, ResourceState.ShaderResource)
					.ReadTexture(_frameResources.DdgiVisibilityHistoryRead, ResourceState.ShaderResource)
					.WriteTexture(_frameResources.DdgiTraceIrradiance, ResourceState.UnorderedAccess)
					.WriteTexture(_frameResources.DdgiTraceVisibility, ResourceState.UnorderedAccess);
				if (_frameResources.SkyboxEnvironment.IsValid)
				{
					ddgiTraceBuilder.ReadTexture(_frameResources.SkyboxEnvironment, ResourceState.ShaderResource);
				}
				ddgiTraceBuilder.SetExecute(_ddgiTraceExecute);

				graph.AddPass("DDGI Irradiance Integrate", PassKind.Compute)
					.ReadTexture(_frameResources.DdgiProbeActivity, ResourceState.ShaderResource)
					.ReadTexture(_frameResources.DdgiProbeStateRead, ResourceState.ShaderResource)
					.ReadTexture(_frameResources.DdgiProbeStateWrite, ResourceState.ShaderResource)
					.ReadTexture(_frameResources.DdgiTraceIrradiance, ResourceState.ShaderResource)
					.ReadTexture(_frameResources.DdgiIrradianceL0HistoryRead, ResourceState.ShaderResource)
					.ReadTexture(_frameResources.DdgiIrradianceLyHistoryRead, ResourceState.ShaderResource)
					.ReadTexture(_frameResources.DdgiIrradianceLzHistoryRead, ResourceState.ShaderResource)
					.ReadTexture(_frameResources.DdgiIrradianceLxHistoryRead, ResourceState.ShaderResource)
					.WriteTexture(_frameResources.DdgiIrradianceL0HistoryWrite, ResourceState.UnorderedAccess)
					.WriteTexture(_frameResources.DdgiIrradianceLyHistoryWrite, ResourceState.UnorderedAccess)
					.WriteTexture(_frameResources.DdgiIrradianceLzHistoryWrite, ResourceState.UnorderedAccess)
					.WriteTexture(_frameResources.DdgiIrradianceLxHistoryWrite, ResourceState.UnorderedAccess)
					.WriteBuffer(_frameResources.DdgiIrradianceEstimator, ResourceState.UnorderedAccess)
					.SetExecute(_ddgiIrradianceIntegrateExecute);

				graph.AddPass("DDGI Visibility Integrate", PassKind.Compute)
					.ReadTexture(_frameResources.DdgiProbeActivity, ResourceState.ShaderResource)
					.ReadTexture(_frameResources.DdgiProbeStateRead, ResourceState.ShaderResource)
					.ReadTexture(_frameResources.DdgiProbeStateWrite, ResourceState.ShaderResource)
					.ReadTexture(_frameResources.DdgiTraceVisibility, ResourceState.ShaderResource)
					.ReadTexture(_frameResources.DdgiVisibilityHistoryRead, ResourceState.ShaderResource)
					.WriteTexture(_frameResources.DdgiVisibilityHistoryWrite, ResourceState.UnorderedAccess)
					.SetExecute(_ddgiVisibilityIntegrateExecute);
			}

			if (_useProceduralSkybox && _recordProceduralSkyBrdf)
			{
				graph.AddPass("Skybox BRDF LUT", PassKind.Compute)
					.WriteTexture(_frameResources.SkyboxBrdfLut, ResourceState.UnorderedAccess)
					.SetExecute(_skyboxBrdfExecute);
			}

			graph.AddPass("Clustered Lighting Build", PassKind.Compute)
				.SetExecute(_clusteredLightingBuildExecute);
			graph.AddPass("Clustered Lighting Write", PassKind.Compute)
				.SetExecute(_clusteredLightingWriteExecute);

			// Reflections run before deferred lighting so their radiance can feed the specular
			// term directly. Shaded hit color therefore comes from the previous frame's pyramid.
			if (_frameResources.ReflectionsRadiance.IsValid)
			{
				var reflectionsBuilder = graph.AddPass(
						_frameResources.Config.Reflections.Mode == ReflectionMode.RayTraced
							? "Reflections (Ray Traced)"
							: "Reflections (Screen Space)",
						PassKind.Compute)
					.ReadTexture(_frameResources.GBufferNormal, ResourceState.ShaderResource)
					.ReadTexture(_frameResources.GBufferMaterial, ResourceState.ShaderResource)
					.ReadTexture(_frameResources.GBufferVelocity, ResourceState.ShaderResource)
					.ReadTexture(_frameResources.GBufferDepth, ResourceState.ShaderResource)
					.WriteTexture(_frameResources.ReflectionsTrace, ResourceState.UnorderedAccess);
				foreach (var colorPyramidLevel in _frameResources.ColorPyramidLevels)
				{
					reflectionsBuilder.ReadTexture(colorPyramidLevel, ResourceState.ShaderResource);
				}
				ReadSkyboxTextures(reflectionsBuilder);
				reflectionsBuilder.SetExecute(_reflectionsExecute);

				if (_frameResources.Config.Reflections.Mode == ReflectionMode.RayTraced &&
				    _frameResources.Config.Reflections.RayTracedSettings.Resolution !=
				    RayTracedReflectionResolution.Full)
				{
					graph.AddPass("Reflections Upsample", PassKind.Compute)
						.ReadTexture(_frameResources.GBufferDepth, ResourceState.ShaderResource)
						.ReadTexture(_frameResources.GBufferNormal, ResourceState.ShaderResource)
						.ReadTexture(_frameResources.ReflectionsTrace, ResourceState.ShaderResource)
						.WriteTexture(_frameResources.ReflectionsRadiance, ResourceState.UnorderedAccess)
						.SetExecute(_reflectionsUpsampleExecute);
				}
			}

			var deferredLightingBuilder = graph.AddPass("Deferred Lighting", PassKind.Compute)
				.ReadTexture(_frameResources.GBufferAlbedo, ResourceState.ShaderResource)
				.ReadTexture(_frameResources.GBufferNormal, ResourceState.ShaderResource)
				.ReadTexture(_frameResources.GBufferMaterial, ResourceState.ShaderResource)
				.ReadTexture(_frameResources.GBufferEmissive, ResourceState.ShaderResource)
				.ReadTexture(_frameResources.GBufferDepth, ResourceState.ShaderResource)
				.ReadTexture(_frameResources.ShadowMapDepth0, ResourceState.ShaderResource)
				.ReadTexture(_frameResources.ShadowMapDepth1, ResourceState.ShaderResource)
				.ReadTexture(_frameResources.ShadowMapDepth2, ResourceState.ShaderResource);
			if (_frameResources.AmbientOcclusionFinal.IsValid)
			{
				deferredLightingBuilder.ReadTexture(_frameResources.AmbientOcclusionFinal, ResourceState.ShaderResource);
			}
			if (_frameResources.ReflectionsRadiance.IsValid)
			{
				deferredLightingBuilder.ReadTexture(_frameResources.ReflectionsRadiance, ResourceState.ShaderResource);
			}
			if (_frameResources.DdgiIrradianceL0HistoryWrite.IsValid &&
			    _frameResources.DdgiIrradianceLyHistoryWrite.IsValid &&
			    _frameResources.DdgiIrradianceLzHistoryWrite.IsValid &&
			    _frameResources.DdgiIrradianceLxHistoryWrite.IsValid &&
			    _frameResources.DdgiVisibilityHistoryWrite.IsValid &&
			    _frameResources.DdgiProbeStateWrite.IsValid &&
			    _frameResources.DdgiProbeActivity.IsValid &&
			    _frameResources.DdgiProbeRelocationDecision.IsValid)
			{
				deferredLightingBuilder
					.ReadTexture(_frameResources.DdgiIrradianceL0HistoryWrite, ResourceState.ShaderResource)
					.ReadTexture(_frameResources.DdgiIrradianceLyHistoryWrite, ResourceState.ShaderResource)
					.ReadTexture(_frameResources.DdgiIrradianceLzHistoryWrite, ResourceState.ShaderResource)
					.ReadTexture(_frameResources.DdgiIrradianceLxHistoryWrite, ResourceState.ShaderResource)
					.ReadTexture(_frameResources.DdgiVisibilityHistoryWrite, ResourceState.ShaderResource)
					.ReadTexture(_frameResources.DdgiProbeStateWrite, ResourceState.ShaderResource)
					.ReadTexture(_frameResources.DdgiProbeActivity, ResourceState.ShaderResource)
					.ReadTexture(_frameResources.DdgiProbeRelocationDecision, ResourceState.ShaderResource);
				if (_frameResources.WriteDdgiFinalContributionDebug)
				{
					deferredLightingBuilder.WriteTexture(_frameResources.DdgiFinalContribution, ResourceState.UnorderedAccess);
				}
				if (_frameResources.WriteDdgiProbeDebug)
				{
					deferredLightingBuilder
						.WriteTexture(_frameResources.DdgiProbeBaseWeightDebug, ResourceState.UnorderedAccess)
						.WriteTexture(_frameResources.DdgiWeightedVisibilityDebug, ResourceState.UnorderedAccess)
						.WriteTexture(_frameResources.DdgiDominantProbeDebug, ResourceState.UnorderedAccess)
						.WriteTexture(_frameResources.DdgiDominantProbeCoordDebug, ResourceState.UnorderedAccess)
						.WriteTexture(_frameResources.DdgiProbeRelocationDebug, ResourceState.UnorderedAccess)
						.WriteTexture(_frameResources.DdgiProbeRelocationDecisionDebug, ResourceState.UnorderedAccess);
				}
			}
			
			ReadSkyboxTextures(deferredLightingBuilder);
			
			deferredLightingBuilder
				.WriteTexture(_frameResources.LightingBuffer, ResourceState.UnorderedAccess)
				.SetExecute(_deferredLightingExecute);

			var transparentForwardBuilder = graph.AddPass("Transparent Forward", PassKind.Graphics)
				.ReadTexture(_frameResources.GBufferDepth, ResourceState.DepthWrite)
				.ReadTexture(_frameResources.ShadowMapDepth0, ResourceState.ShaderResource)
				.ReadTexture(_frameResources.ShadowMapDepth1, ResourceState.ShaderResource)
				.ReadTexture(_frameResources.ShadowMapDepth2, ResourceState.ShaderResource)
				.WriteTexture(_frameResources.LightingBuffer, ResourceState.RenderTarget);
			if (_frameResources.Fsr3.TransparencyMask.IsValid)
			{
				transparentForwardBuilder.WriteTexture(_frameResources.Fsr3.TransparencyMask, ResourceState.RenderTarget);
			}
			if (_frameResources.DdgiProbeStateWrite.IsValid)
			{
				transparentForwardBuilder.ReadTexture(
					_frameResources.DdgiProbeStateWrite,
					ResourceState.ShaderResource);
			}
			
			ReadSkyboxTextures(transparentForwardBuilder);
			transparentForwardBuilder.SetExecute(_transparentForwardExecute);

			if (_frameResources.Config.Fsr3.Enabled && _frameResources.Fsr3.InternalHistoryWrite.IsValid)
			{
				AddFsr3Passes(graph);
			}

			// Capture the finished HDR scene color so next frame's reflections have shaded,
			// pre-filtered radiance to sample.
			var colorPyramidLevels = _frameResources.ColorPyramidLevels ?? [];
			for (var level = 0; level < colorPyramidLevels.Length; level++)
			{
				var stage = level == 0 ? ColorPyramidPass.Stage.Copy : ColorPyramidPass.Stage.Downsample;
				var source = level == 0
					? _frameResources.LightingBuffer
					: colorPyramidLevels[level - 1];
				var output = colorPyramidLevels[level];
				graph.AddPass($"Color Pyramid {level}", PassKind.Compute)
					.ReadTexture(source, ResourceState.ShaderResource)
					.WriteTexture(output, ResourceState.UnorderedAccess)
					.SetExecute(context => ExecuteColorPyramid(context, stage, source, output));
			}

			if (_frameResources.BloomDownsampleLevels?.Length > 0)
			{
				graph.AddPass("Bloom Prefilter", PassKind.Compute)
					.ReadTexture(_frameResources.ResolvedSceneColor, ResourceState.ShaderResource)
					.WriteTexture(_frameResources.BloomDownsampleLevels[0], ResourceState.UnorderedAccess)
					.SetExecute(context => ExecuteBloom(context, BloomPass.Stage.Prefilter,
						_frameResources.ResolvedSceneColor, _frameResources.BloomDownsampleLevels[0], default));

				for (var level = 1; level < _frameResources.BloomDownsampleLevels.Length; level++)
				{
					var source = _frameResources.BloomDownsampleLevels[level - 1];
					var output = _frameResources.BloomDownsampleLevels[level];
					graph.AddPass($"Bloom Downsample {level}", PassKind.Compute)
						.ReadTexture(source, ResourceState.ShaderResource)
						.WriteTexture(output, ResourceState.UnorderedAccess)
						.SetExecute(context => ExecuteBloom(context, BloomPass.Stage.Downsample, source, output, default));
				}

				for (var level = _frameResources.BloomUpsampleLevels.Length - 1; level >= 0; level--)
				{
					var small = level == _frameResources.BloomUpsampleLevels.Length - 1
						? _frameResources.BloomDownsampleLevels[level + 1]
						: _frameResources.BloomUpsampleLevels[level + 1];
					var large = _frameResources.BloomDownsampleLevels[level];
					var output = _frameResources.BloomUpsampleLevels[level];
					graph.AddPass($"Bloom Upsample {level}", PassKind.Compute)
						.ReadTexture(small, ResourceState.ShaderResource)
						.ReadTexture(large, ResourceState.ShaderResource)
						.WriteTexture(output, ResourceState.UnorderedAccess)
						.SetExecute(context => ExecuteBloom(context, BloomPass.Stage.Upsample, small, output, large));
				}

				var bloomResult = _frameResources.BloomUpsampleLevels.Length > 0
					? _frameResources.BloomUpsampleLevels[0]
					: _frameResources.BloomDownsampleLevels[0];
				graph.AddPass("Bloom Composite", PassKind.Compute)
					.ReadTexture(_frameResources.ResolvedSceneColor, ResourceState.ShaderResource)
					.ReadTexture(bloomResult, ResourceState.ShaderResource)
					.WriteTexture(_frameResources.BloomCompositeSceneColor, ResourceState.UnorderedAccess)
					.SetExecute(_bloomCompositeExecute);
			}

			graph.AddPass("Tonemapping", PassKind.Compute)
				.ReadTexture(_frameResources.BloomCompositeSceneColor.IsValid ? _frameResources.BloomCompositeSceneColor : _frameResources.ResolvedSceneColor, ResourceState.ShaderResource)
				.WriteTexture(_frameResources.TonemappedLinearSceneColor, ResourceState.UnorderedAccess)
				.SetExecute(_tonemappingExecute);

			// This is married to TAA which is currently unavailable. FSR has its own RCAS sharpening
			// graph.AddPass("CAS Sharpen", PassKind.Compute)
			// 	.ReadTexture(_frameResources.TonemappedLinearSceneColor, ResourceState.ShaderResource)
			// 	.WriteTexture(_frameResources.DisplayLinearSceneColor, ResourceState.UnorderedAccess)
			// 	.SetExecute(_casSharpenExecute);

			graph.AddPass("Copy To Final", PassKind.Compute)
				.ReadTexture(_frameResources.TonemappedLinearSceneColor, ResourceState.ShaderResource)
				.WriteTexture(_frameResources.EncodedSceneColor, ResourceState.UnorderedAccess)
				.WriteTexture(_frameResources.FinalColor, ResourceState.UnorderedAccess)
				.SetExecute(_copyToFinalExecute);

			if (ReferenceEquals(_gameplayUiFrame.Screen, UiFrameData.Empty) == false &&
			    _gameplayUiFrame.Screen.CommandCount > 0)
			{
				// Keep capture/debug output and the presented target identical. Both are BGRA8, which
				// matches the UI pipeline, and CSS colors are already authored in display space.
				graph.AddPass("Gameplay UI Screen Capture", PassKind.Graphics)
					.WriteTexture(_frameResources.EncodedSceneColor, ResourceState.RenderTarget)
					.SetExecute(_gameplayScreenEncodedUiExecute);
				graph.AddPass("Gameplay UI Screen", PassKind.Graphics)
					.WriteTexture(_frameResources.FinalColor, ResourceState.RenderTarget)
					.SetExecute(_gameplayScreenFinalUiExecute);
			}

			if (_frameResources.MotionVectorDebugColor.IsValid)
			{
				graph.AddPass("Motion Vector Debug", PassKind.Compute)
					.ReadTexture(_frameResources.GBufferVelocity, ResourceState.ShaderResource)
					.WriteTexture(_frameResources.MotionVectorDebugColor, ResourceState.UnorderedAccess)
					.SetExecute(_motionVectorDebugExecute);
			}
		}

		var imguiBuilder = graph.AddPass("ImGui", PassKind.Graphics)
			.WriteTexture(_frameResources.FinalColor, ResourceState.RenderTarget);
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
		if (_frameResources.SkyboxEnvironment.IsValid)
		{
			builder.ReadTexture(_frameResources.SkyboxEnvironment, ResourceState.ShaderResource);
		}
		if (_frameResources.SkyboxIrradiance.IsValid)
		{
			builder.ReadTexture(_frameResources.SkyboxIrradiance, ResourceState.ShaderResource);
		}
		if (_frameResources.SkyboxPrefilter.IsValid)
		{
			builder.ReadTexture(_frameResources.SkyboxPrefilter, ResourceState.ShaderResource);
		}
		if (_frameResources.SkyboxBrdfLut.IsValid)
		{
			builder.ReadTexture(_frameResources.SkyboxBrdfLut, ResourceState.ShaderResource);
		}
	}

	public void PrepareSceneViewport()
	{
		if (_frameResources.SceneEnabled == false)
		{
			_resolvedSceneViewportState = SceneViewportRenderState.Empty;
			return;
		}

		var textureId = ResolveSceneViewportTextureId(out var activeDebugViewId);
		ResolveSceneViewportTextureId(_uiFrame, textureId);
		_resolvedSceneViewportState = new SceneViewportRenderState(
			textureId,
			_frameResources.SceneFramebufferSize,
			_sceneDebugViewOptions,
			activeDebugViewId);
	}

	public RenderGraphResourceHandle GetFinalColorHandle() => _frameResources.FinalColor;
	public RenderGraphResourceHandle GetCaptureColorHandle() => _frameResources.EncodedSceneColor;

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

		_sceneDebugViews.Add(new SceneDebugViewRegistration(id, label, handle, kind));
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
		if (_sceneDebugViews.Count == 0)
		{
			return Array.Empty<SceneDebugViewOption>();
		}

		var options = new SceneDebugViewOption[_sceneDebugViews.Count];
		for (var i = 0; i < _sceneDebugViews.Count; i++)
		{
			var debugView = _sceneDebugViews[i];
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
		if (TryGetSceneDebugView(_requestedSceneDebugViewId, out var requestedView))
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
		for (var i = 0; i < _sceneDebugViews.Count; i++)
		{
			if (string.Equals(_sceneDebugViews[i].Id, id, StringComparison.Ordinal))
			{
				view = _sceneDebugViews[i];
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

	private static void ResolveSceneViewportTextureId(UiFrameData uiFrame, nint textureId)
	{
		if (ReferenceEquals(uiFrame, UiFrameData.Empty) || uiFrame.CommandCount == 0)
		{
			return;
		}

		for (var i = 0; i < uiFrame.CommandCount; i++)
		{
			var command = uiFrame.Commands[i];
			if (command.TextureId != UiTextureIds.SceneViewport)
			{
				continue;
			}

			uiFrame.Commands[i] = new UiDrawCommand(
				command.ElemCount,
				command.IdxOffset,
				command.VtxOffset,
				command.ClipRect,
				textureId);
		}
	}

	private void ExecuteGpuDrawUpdate(RenderGraphContext context)
	{
		context.GpuDrawDatabase.CopyUpdates(_frameGpuDrawUpdates);

		// Before RecordUpdate, not after. RecordUpdate would otherwise upload the instance through
		// the ordinary mesh path, which allocates a vertex range but leaves the shared bind-pose
		// source mesh unuploaded — and the skinning shader reads its bind pose from there.
		var skinningPackets = context.FrameSnapshot.SkinningPackets;
		EnsureSkinnedInstanceResources(skinningPackets);

		// Graphics bindings require these buffers even when no meshes are skinned.
		var device = _renderer.GetGfxDevice();
		_skinningPass.EnsureResources(device);
		_gpuDrawResources.SkinVertexBuffer = _skinningPass.SkinVertexBuffer;
		_gpuDrawResources.BoneMatrixBuffer = _skinningPass.BoneMatrixBuffer;
		_gpuDrawResources.SkinnedInstanceBuffer = _skinningPass.SkinnedInstanceBuffer;

		_gpuDrawPass.RecordUpdate(context);

		_skinningPass.Record(
			context.CommandList,
			device,
			_renderer,
			skinningPackets,
			context.FrameSnapshot.BoneMatrices,
			context.GpuDrawDatabase);

		_gpuDrawResources.SkinVertexBuffer = _skinningPass.SkinVertexBuffer;
		_gpuDrawResources.BoneMatrixBuffer = _skinningPass.BoneMatrixBuffer;
		_gpuDrawResources.SkinnedInstanceBuffer = _skinningPass.SkinnedInstanceBuffer;

		if (RequiresRayTracingScene(_frameResources.Config))
		{
			// Deliberately after skinning: acceleration structures are built over the vertices this
			// frame produced, so a ray-traced reflection shows the pose being drawn rather than the
			// previous one.
			for (var i = 0; i < skinningPackets.Count; i++)
			{
				_rayTracingSceneResources.QueueSkinnedInstanceRebuild(skinningPackets[i].InstanceMesh);
			}

			_rayTracingSceneResources.RecordUpdate(context, _renderer, _frameGpuDrawUpdates);
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
		_shadowMapPass.PrepareFrame(sceneData, _frameResources.Config.ShadowMaps);
		var shadowData = _shadowMapPass.GetCurrentFrameData();
		if (shadowData.Enabled == false)
		{
			return;
		}

		Span<Matrix4x4> cascadeViewProjections = stackalloc Matrix4x4[ShadowMapPass.MaxCascadeCount];
		for (var cascadeIndex = 0; cascadeIndex < shadowData.CascadeCount; cascadeIndex++)
		{
			cascadeViewProjections[cascadeIndex] = shadowData.GetCascadeViewProjection(cascadeIndex);
		}

		_gpuDrawPass.RecordCullForViews(
			context,
			cascadeViewProjections[..shadowData.CascadeCount],
			sceneData.CameraOrigin,
			useShadowBuffers: true,
			DrawPassParticipation.ShadowCaster);

		// Encoding and compaction both belong here rather than in the shadow pass: compaction is compute
		// work, and it has to complete before the render pass that executes its output begins.
		var device = _renderer.GetGfxDevice();
		for (var cascadeIndex = 0; cascadeIndex < shadowData.CascadeCount; cascadeIndex++)
		{
			EnsureShadowIndirectCommands(context, device, cascadeIndex);
			var compacted = _gpuDrawPass.RecordIndirectCompaction(
				context,
				_shadowMapPass.GetIndirectCommandSet(cascadeIndex),
				DrawPassParticipation.ShadowCaster,
				_gpuDrawResources.ShadowDrawArgsBuffer,
				GpuDrawResources.GetShadowDrawArgsOffsetBytes(cascadeIndex),
				lane => _shadowMapPass.HasIndirectLane(cascadeIndex, lane));
			_shadowMapPass.SetCompactedExecution(cascadeIndex, compacted);
		}
	}

	private void EnsureShadowIndirectCommands(RenderGraphContext context, IGfxDevice device, int cascadeIndex)
	{
		_shadowMapPass.EnsureIndirectResources(device, cascadeIndex);
		_gpuDrawPass.EnsureIndirectCommandsForPass(
			context.GpuDrawDatabase,
			_shadowMapPass.GetIndirectCommandSet(cascadeIndex),
			DrawPassParticipation.ShadowCaster,
			SharedDrawIndirectEncodeResources.FromGpuDrawResources(
				_gpuDrawResources,
				_gpuDrawResources.ShadowDrawArgsBuffer,
				GpuDrawResources.GetShadowDrawArgsOffsetBytes(cascadeIndex)),
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
			var shadowMapHandle = GetShadowMapHandle(_frameResources, cascadeIndex);
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
		_skyboxPass.RecordEnvironment(context, _frameResources.Config.SkyboxConfig);
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
		var albedoHandle = _frameResources.DecalSourceGBufferAlbedo.IsValid
			? _frameResources.DecalSourceGBufferAlbedo
			: _frameResources.GBufferAlbedo;
		var normalHandle = _frameResources.DecalSourceGBufferNormal.IsValid
			? _frameResources.DecalSourceGBufferNormal
			: _frameResources.GBufferNormal;
		var materialHandle = _frameResources.DecalSourceGBufferMaterial.IsValid
			? _frameResources.DecalSourceGBufferMaterial
			: _frameResources.GBufferMaterial;
		var emissiveHandle = _frameResources.DecalSourceGBufferEmissive.IsValid
			? _frameResources.DecalSourceGBufferEmissive
			: _frameResources.GBufferEmissive;
		var albedoTexture = context.GetTexture(albedoHandle);
		var normalTexture = context.GetTexture(normalHandle);
		var materialTexture = context.GetTexture(materialHandle);
		var emissiveTexture = context.GetTexture(emissiveHandle);
		var depthTexture = context.GetTexture(_frameResources.GBufferDepth);
		_gpuDrawPass.EnsureGBufferIndirectCommands(context);
		var bucketList = _gpuDrawPass.BuildGBufferBuckets();

		var gbufferConfig = new GBufferPassConfig
		{
			FramebufferWidth = _frameResources.SceneFramebufferSize.X,
			FramebufferHeight = _frameResources.SceneFramebufferSize.Y,
			AlbedoTarget = albedoTexture,
			NormalTarget = normalTexture,
			MaterialTarget = materialTexture,
			EmissiveTarget = emissiveTexture,
			VelocityTarget = context.GetTexture(_frameResources.GBufferVelocity),
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
			_frameResources,
			_renderer.GetGfxDevice(),
			_gpuDrawResources,
			context.SceneData);
		_screenSpaceDecalPass.Record(context, in config, context.SceneData);
	}

	private void ExecuteGBufferDecalSeed(RenderGraphContext context)
	{
		var config = _gBufferDecalSeedPass.BuildConfig(
			context,
			_frameResources,
			_renderer.GetGfxDevice());
		_gBufferDecalSeedPass.Record(context, in config);
	}

	private void ExecuteDeferredLighting(RenderGraphContext context)
	{
		var config = _deferredLightingPass.BuildConfig(
			context,
			_frameResources,
			_renderer.GetGfxDevice(),
			_gpuDrawResources,
			_shadowMapPass.GetCurrentFrameData(),
			context.SceneData);
		_deferredLightingPass.Record(context, ref config, context.SceneData);
	}

	private void ExecuteReflections(RenderGraphContext context)
	{
		var isRayTraced = _frameResources.Config.Reflections.Mode == ReflectionMode.RayTraced;
		var config = _reflectionsPass.BuildConfig(
			context,
			_frameResources,
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
			_frameResources,
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
		var fsr = _frameResources.Fsr3;
		var size = _frameResources.SceneFramebufferSize;
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
		if (!fsr.HistoryValid || _resetTaaHistoryThisFrame)
		{
			AddFsr3Clear(graph, "FSR3 Clear Frame Info", fsr.FrameInfo, new Int2(1, 1), 0u, false);
			AddFsr3Clear(graph, "FSR3 Clear Internal History", fsr.InternalHistoryRead, size, 0u, false);
			AddFsr3Clear(graph, "FSR3 Clear Luma History", fsr.LumaHistoryRead, size, 0u, false);
			AddFsr3Clear(graph, "FSR3 Clear Previous Luma", fsr.CurrentLumaRead, size, 0u, false);
			AddFsr3Clear(graph, "FSR3 Clear Accumulation", fsr.AccumulationRead, size, 0u, false);
		}

		graph.AddPass("FSR3 Prepare Inputs", PassKind.Compute)
			.ReadTexture(_frameResources.LightingBuffer, ResourceState.ShaderResource)
			.ReadTexture(_frameResources.GBufferDepth, ResourceState.ShaderResource)
			.ReadTexture(_frameResources.GBufferVelocity, ResourceState.ShaderResource)
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
			.ReadTexture(_frameResources.GBufferDepth, ResourceState.ShaderResource)
			.ReadTexture(_frameResources.GBufferMaterial, ResourceState.ShaderResource)
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
			.ReadTexture(_frameResources.LightingBuffer, ResourceState.ShaderResource)
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
			.WriteTexture(_frameResources.ResolvedSceneColor, ResourceState.UnorderedAccess)
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
				.WriteTexture(_frameResources.ResolvedSceneColor, ResourceState.UnorderedAccess)
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
		var size = _frameResources.SceneFramebufferSize;
		var camera = context.FrameSnapshot.Camera;
		var verticalFov = float.DegreesToRadians(camera.Fov > 0.0f ? camera.Fov : 70.0f);
		var depth = Fsr3Constants.BuildDeviceToViewDepth(context.SceneData.NearPlane,
			context.SceneData.FarPlane, verticalFov, (float)Math.Max(size.X, 1) / Math.Max(size.Y, 1));
		var reset = _resetTaaHistoryThisFrame || context.SceneData.ResetHistory || !_frameResources.Fsr3.HistoryValid;
		return Fsr3Constants.Build(size, size, size, size, depth,
			context.SceneData.JitterPixels, context.SceneData.PreviousJitterPixels, verticalFov,
			Math.Max(_uiFrame.DeltaTime, 1.0f / 1000.0f), reset ? 0.0f : _fsr3FrameIndex);
	}

	private void ExecuteFsr3PrepareInputs(RenderGraphContext context)
	{
		var fsr = _frameResources.Fsr3;
		var constants = BuildFsr3Constants(context);
		var config = _passSet.Fsr3PrepareInputsPass.BuildConfig(context, _renderer.GetGfxDevice(),
			_frameResources.LightingBuffer, _frameResources.GBufferDepth, _frameResources.GBufferVelocity,
			fsr.DilatedMotionVectors, fsr.DilatedDepth, fsr.FarthestDepth, fsr.CurrentLumaWrite,
			fsr.ReconstructedPrevNearestDepth, in constants);
		_passSet.Fsr3PrepareInputsPass.Record(context, in config);
	}

	private void ExecuteFsr3LumaPyramid(RenderGraphContext context)
	{
		var fsr = _frameResources.Fsr3;
		var constants = BuildFsr3Constants(context);
		var config = _passSet.Fsr3LumaPyramidPass.BuildConfig(context, _renderer.GetGfxDevice(),
			fsr.CurrentLumaWrite, fsr.FarthestDepth, fsr.FarthestDepthMip1, fsr.FrameInfo,
			fsr.LumaSpdAtomic, fsr.LumaSpdMips, in constants);
		_passSet.Fsr3LumaPyramidPass.Record(context, in config);
	}

	private void ExecuteFsr3ShadingChangePyramid(RenderGraphContext context)
	{
		var fsr = _frameResources.Fsr3;
		var constants = BuildFsr3Constants(context);
		var config = _passSet.Fsr3ShadingChangePyramidPass.BuildConfig(context, _renderer.GetGfxDevice(),
			fsr.CurrentLumaWrite, fsr.CurrentLumaRead, fsr.DilatedMotionVectors, fsr.FrameInfo,
			fsr.ShadingSpdAtomic, fsr.ShadingSpdMips, in constants);
		_passSet.Fsr3ShadingChangePyramidPass.Record(context, in config);
	}

	private void ExecuteFsr3ShadingChange(RenderGraphContext context)
	{
		var fsr = _frameResources.Fsr3;
		var constants = BuildFsr3Constants(context);
		var config = _passSet.Fsr3ShadingChangePass.BuildConfig(context, _renderer.GetGfxDevice(),
			fsr.ShadingSpdMips, fsr.ShadingChange, in constants);
		_passSet.Fsr3ShadingChangePass.Record(context, in config);
	}

	private void ExecuteFsr3PrepareReactivity(RenderGraphContext context)
	{
		var fsr = _frameResources.Fsr3;
		var constants = BuildFsr3Constants(context);
		var settings = _frameResources.Config.Fsr3;
		var config = _passSet.Fsr3PrepareReactivityPass.BuildConfig(context, _renderer.GetGfxDevice(),
			fsr.ReconstructedPrevNearestDepth, fsr.DilatedMotionVectors, fsr.DilatedDepth,
			_frameResources.GBufferDepth,
			_frameResources.GBufferMaterial, fsr.TransparencyMask, fsr.AccumulationRead, fsr.AccumulationWrite,
			fsr.ShadingChange, fsr.CurrentLumaWrite, fsr.FrameInfo, fsr.DilatedReactiveMasks,
			fsr.NewLocks, in constants,
			settings.AlphaTestReactiveScale,
			settings.TransparencyAndCompositionMaskScale);
		_passSet.Fsr3PrepareReactivityPass.Record(context, in config);
	}

	private void ExecuteFsr3LumaInstability(RenderGraphContext context)
	{
		var fsr = _frameResources.Fsr3;
		var constants = BuildFsr3Constants(context);
		var config = _passSet.Fsr3LumaInstabilityPass.BuildConfig(context, _renderer.GetGfxDevice(),
			fsr.FrameInfo, fsr.DilatedReactiveMasks, fsr.DilatedMotionVectors, fsr.LumaHistoryRead,
			fsr.LumaHistoryWrite, fsr.FarthestDepthMip1, fsr.CurrentLumaWrite, fsr.LumaInstability,
			in constants);
		_passSet.Fsr3LumaInstabilityPass.Record(context, in config);
	}

	private void ExecuteFsr3Accumulate(RenderGraphContext context)
	{
		var fsr = _frameResources.Fsr3;
		var constants = BuildFsr3Constants(context);
		var config = _passSet.Fsr3AccumulatePass.BuildConfig(context, _renderer.GetGfxDevice(),
			fsr.FrameInfo, _frameResources.LightingBuffer, fsr.DilatedMotionVectors,
			fsr.DilatedReactiveMasks, fsr.FarthestDepthMip1, fsr.LumaInstability, fsr.NewLocks,
			fsr.InternalHistoryRead, fsr.InternalHistoryWrite, in constants);
		_passSet.Fsr3AccumulatePass.Record(context, in config);
	}

	private void ExecuteFsr3Rcas(RenderGraphContext context)
	{
		var fsr = _frameResources.Fsr3;
		var constants = BuildFsr3Constants(context);
		var settings = _frameResources.Config.Fsr3;
		var config = _passSet.Fsr3RcasPass.BuildConfig(context, _renderer.GetGfxDevice(),
			fsr.InternalHistoryWrite, _frameResources.ResolvedSceneColor, fsr.FrameInfo,
			in constants, settings.Sharpness, settings.EnableSharpening);
		_passSet.Fsr3RcasPass.Record(context, in config);
	}

	private void ExecuteFsr3DebugView(RenderGraphContext context)
	{
		var fsr = _frameResources.Fsr3;
		var constants = BuildFsr3Constants(context);
		var config = _passSet.Fsr3DebugViewPass.BuildConfig(context, _renderer.GetGfxDevice(),
			fsr.DilatedReactiveMasks, fsr.DilatedMotionVectors, fsr.DilatedDepth,
			fsr.InternalHistoryWrite, fsr.CurrentLumaWrite, fsr.CurrentLumaRead,
			_frameResources.ResolvedSceneColor, fsr.FrameInfo, in constants);
		_passSet.Fsr3DebugViewPass.Record(context, in config);
	}

	private void ExecuteClusteredLightingBuild(RenderGraphContext context)
	{
		var config = _clusteredLightingPass.BuildConfig(
			_renderer.GetGfxDevice(),
			_gpuDrawResources,
			_frameResources.SceneFramebufferSize);
		_clusteredLightingPass.Record(context, in config, context.SceneData, ClusteredLightingPass.Stage.BuildClusters);
	}

	private void ExecuteClusteredLightingWrite(RenderGraphContext context)
	{
		var config = _clusteredLightingPass.BuildConfig(
			_renderer.GetGfxDevice(),
			_gpuDrawResources,
			_frameResources.SceneFramebufferSize);
		_clusteredLightingPass.Record(context, in config, context.SceneData, ClusteredLightingPass.Stage.WriteLightIndices);
	}

	private void ExecuteTemporalResolve(RenderGraphContext context)
	{
		var config = _temporalAntiAliasingPass.BuildConfig(
			context,
			_frameResources,
			_renderer.GetGfxDevice(),
			_historyValid,
			_resetTaaHistoryThisFrame || context.SceneData.ResetHistory);
		_temporalAntiAliasingPass.Record(context, in config);
	}

	private void ExecuteTemporalHistoryStore(RenderGraphContext context)
	{
		var config = _temporalHistoryStorePass.BuildConfig(
			context,
			_frameResources,
			_renderer.GetGfxDevice());
		_temporalHistoryStorePass.Record(context, in config);
	}

	private void ExecuteAmbientOcclusion(RenderGraphContext context)
	{
		var config = _ambientOcclusionPass.BuildConfig(
			context,
			_frameResources,
			_renderer.GetGfxDevice(),
			_renderer,
			_gpuDrawResources,
			_frameResources.Config.AmbientOcclusion.Mode == AmbientOcclusionMode.RayTraced
				? _rayTracingSceneResources
				: null);
		_ambientOcclusionPass.Record(context, in config, context.SceneData);
	}

	private void ExecuteAmbientOcclusionBlurHorizontal(RenderGraphContext context)
	{
		var config = _ambientOcclusionBlurPass.BuildConfig(
			context,
			_frameResources,
			_renderer.GetGfxDevice(),
			blurHorizontally: true);
		_ambientOcclusionBlurPass.Record(context, in config, context.SceneData);
	}

	private void ExecuteAmbientOcclusionBlurVertical(RenderGraphContext context)
	{
		var config = _ambientOcclusionBlurPass.BuildConfig(
			context,
			_frameResources,
			_renderer.GetGfxDevice(),
			blurHorizontally: false);
		_ambientOcclusionBlurPass.Record(context, in config, context.SceneData);
	}

	private void ExecuteAmbientOcclusionUpsample(RenderGraphContext context)
	{
		var config = _ambientOcclusionUpsamplePass.BuildConfig(
			context,
			_frameResources,
			_renderer.GetGfxDevice());
		_ambientOcclusionUpsamplePass.Record(context, in config, context.SceneData);
	}

	private void ExecuteDdgiClassify(RenderGraphContext context)
	{
		_currentDdgiConfig = _ddgiPass.BuildConfig(
			context,
			_frameResources,
			_renderer.GetGfxDevice(),
			_renderer,
			_gpuDrawResources,
			_rayTracingSceneResources,
			context.SceneData,
			_ddgiHistoryValid);
		_currentDdgiConfigValid = true;
		_ddgiPass.RecordClassify(context, in _currentDdgiConfig);
	}

	private void ExecuteDdgiTrace(RenderGraphContext context)
	{
		if (_currentDdgiConfigValid == false)
		{
			throw new InvalidOperationException("DDGI trace executed before DDGI probe classification config was built.");
		}

		_ddgiPass.RecordTrace(context, in _currentDdgiConfig);
	}

	private void ExecuteDdgiIrradianceIntegrate(RenderGraphContext context)
	{
		if (_currentDdgiConfigValid == false)
		{
			throw new InvalidOperationException("DDGI irradiance integrate executed before DDGI trace config was built.");
		}

		_ddgiPass.RecordIrradianceIntegrate(context, in _currentDdgiConfig);
	}

	private void ExecuteDdgiVisibilityIntegrate(RenderGraphContext context)
	{
		if (_currentDdgiConfigValid == false)
		{
			throw new InvalidOperationException("DDGI visibility integrate executed before DDGI trace config was built.");
		}

		_ddgiPass.RecordVisibilityIntegrate(context, in _currentDdgiConfig);
	}

	private void ExecuteDdgiRelocationTrace(RenderGraphContext context, int iteration)
	{
		if (_currentDdgiConfigValid == false)
		{
			throw new InvalidOperationException("DDGI relocation trace executed before DDGI probe classification config was built.");
		}

		_ddgiPass.RecordRelocationTrace(context, in _currentDdgiConfig, iteration);
	}

	private void ExecuteDdgiRelocate(RenderGraphContext context, int iteration)
	{
		if (_currentDdgiConfigValid == false)
		{
			throw new InvalidOperationException("DDGI relocation solve executed before DDGI probe classification config was built.");
		}

		_ddgiPass.RecordRelocate(context, in _currentDdgiConfig, iteration);
	}

	private void ExecuteTransparentForward(RenderGraphContext context)
	{
		var device = _renderer.GetGfxDevice();
		_transparentForwardPass.EnsureIndirectResources(device);
		_gpuDrawPass.EnsureIndirectCommandsForPass(
			context.GpuDrawDatabase,
			_transparentForwardPass.IndirectCommandSet,
			DrawPassParticipation.ForwardTransparent,
			SharedDrawIndirectEncodeResources.FromGpuDrawResources(_gpuDrawResources),
			lane => _transparentForwardPass.HasIndirectLane(lane),
			lane => _transparentForwardPass.GetBufferBindings(lane),
			lane => _transparentForwardPass.GetPassBindingSet(lane, _gpuDrawResources));
		var config = _transparentForwardPass.BuildConfig(
			context,
			_frameResources,
			device,
			_gpuDrawResources,
			_shadowMapPass.GetCurrentFrameData(),
			context.SceneData);
		_transparentForwardPass.Record(context, in config, context.SceneData);
	}

	private void ExecuteTonemapping(RenderGraphContext context)
	{
		var config = _tonemappingPass.BuildConfig(
			context,
			_frameResources,
			_renderer.GetGfxDevice());
		_tonemappingPass.Record(context, in config);
	}

	private void ExecuteBloom(RenderGraphContext context, BloomPass.Stage stage,
		RenderGraphResourceHandle source, RenderGraphResourceHandle output, RenderGraphResourceHandle secondary)
	{
		var config = _bloomPass.BuildConfig(context, _renderer.GetGfxDevice(), stage, source, output, secondary, _frameResources.Config.Bloom);
		_bloomPass.Record(context, stage, in config);
	}

	private void ExecuteBloomComposite(RenderGraphContext context)
	{
		var bloomResult = _frameResources.BloomUpsampleLevels.Length > 0
			? _frameResources.BloomUpsampleLevels[0]
			: _frameResources.BloomDownsampleLevels[0];
		ExecuteBloom(context, BloomPass.Stage.Composite, _frameResources.ResolvedSceneColor,
			_frameResources.BloomCompositeSceneColor, bloomResult);
	}

	private void ExecuteCasSharpen(RenderGraphContext context)
	{
		var config = _casSharpenPass.BuildConfig(
			context,
			_frameResources,
			_renderer.GetGfxDevice());
		_casSharpenPass.Record(context, in config);
	}

	private void ExecuteCopyToFinal(RenderGraphContext context)
	{
		var config = _copyToFinalPass.BuildConfig(
			context,
			_frameResources,
			_renderer.GetGfxDevice());
		_copyToFinalPass.Record(context, in config);
	}

	private void ExecuteMotionVectorDebug(RenderGraphContext context)
	{
		var config = _motionVectorDebugPass.BuildConfig(
			context,
			_renderer.GetGfxDevice(),
			_frameResources.GBufferVelocity,
			_frameResources.MotionVectorDebugColor,
			_frameResources.SceneFramebufferSize,
			_frameResources.Config.MotionVectorDebug);
		_motionVectorDebugPass.Record(context, in config);
	}

	private void ExecuteImGui(RenderGraphContext context)
	{
		var finalColor = context.GetTexture(_frameResources.FinalColor);
		_imGuiRenderer.EnsureResources(_renderer.GetGfxDevice(), _uiFrame);
		_imGuiRenderer.Record(context, _uiFrame, finalColor, clearTarget: _frameResources.SceneEnabled == false);
	}

	private void ExecuteGameplayScreenEncodedUi(RenderGraphContext context) =>
		ExecuteGameplayScreenUi(context, _frameResources.EncodedSceneColor);

	private void ExecuteGameplayScreenFinalUi(RenderGraphContext context) =>
		ExecuteGameplayScreenUi(context, _frameResources.FinalColor);

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

	private static RenderGraphResourceHandle GetShadowMapHandle(in RenderGraphFrameResources resources, int cascadeIndex)
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
		var changed = _hasPreviousFrameShape == false ||
		              _previousFramebufferSize.X != framebufferSize.X ||
		              _previousFramebufferSize.Y != framebufferSize.Y ||
		              _previousSceneFramebufferSize.X != sceneFramebufferSize.X ||
		              _previousSceneFramebufferSize.Y != sceneFramebufferSize.Y ||
		              _previousShadowMapResolution != shadowMapResolution ||
		              _previousSceneEnabled != sceneEnabled;
		if (changed == false)
		{
			return;
		}

		_resources.InvalidateTransientTexturePool();
		_previousFramebufferSize = framebufferSize;
		_previousSceneFramebufferSize = sceneFramebufferSize;
		_previousShadowMapResolution = shadowMapResolution;
		_previousSceneEnabled = sceneEnabled;
		_hasPreviousFrameShape = true;
	}

	public void CompleteFrame()
	{
		if (_frameResources.ColorPyramidLevels is not { Length: > 0 } || _frameResources.SceneEnabled == false)
		{
			_colorPyramidValid = false;
		}
		else
		{
			for (var level = 0; level < _frameResources.ColorPyramidLevels.Length; level++)
			{
				_colorPyramidStates[level] = _resources.GetResourceState(_frameResources.ColorPyramidLevels[level]);
			}

			_colorPyramidValid = true;
		}

		if (_frameResources.Config.Fsr3.Enabled == false || _frameResources.SceneEnabled == false)
		{
			_historyValid = false;
		}
		else if (_frameResources.HistoryColorWrite.IsValid == false ||
		         _frameResources.HistoryDepthWrite.IsValid == false)
		{
			_historyValid = false;
		}
		else
		{
			if (_frameResources.HistoryColorRead.IsValid)
			{
				_historyColorStates[_historyReadIndex] = _resources.GetResourceState(_frameResources.HistoryColorRead);
			}

			if (_frameResources.HistoryDepthRead.IsValid)
			{
				_historyDepthStates[_historyReadIndex] = _resources.GetResourceState(_frameResources.HistoryDepthRead);
			}

			var writeIndex = 1 - _historyReadIndex;
			_historyColorStates[writeIndex] = _resources.GetResourceState(_frameResources.HistoryColorWrite);
			_historyDepthStates[writeIndex] = _resources.GetResourceState(_frameResources.HistoryDepthWrite);
			_fsr3CurrentLumaStates[_historyReadIndex] = _resources.GetResourceState(_frameResources.Fsr3.CurrentLumaRead);
			_fsr3CurrentLumaStates[writeIndex] = _resources.GetResourceState(_frameResources.Fsr3.CurrentLumaWrite);
			_fsr3AccumulationStates[_historyReadIndex] = _resources.GetResourceState(_frameResources.Fsr3.AccumulationRead);
			_fsr3AccumulationStates[writeIndex] = _resources.GetResourceState(_frameResources.Fsr3.AccumulationWrite);
			_fsr3FrameInfoState = _resources.GetResourceState(_frameResources.Fsr3.FrameInfo);
			_historyReadIndex = writeIndex;
			_historyValid = true;
			_fsr3FrameIndex++;
		}

		if (HasRayTracedDdgi(_frameResources.Config) == false || _frameResources.SceneEnabled == false)
		{
			_ddgiHistoryValid = false;
			return;
		}

		if (_frameResources.DdgiIrradianceL0HistoryWrite.IsValid == false ||
		    _frameResources.DdgiIrradianceLyHistoryWrite.IsValid == false ||
		    _frameResources.DdgiIrradianceLzHistoryWrite.IsValid == false ||
		    _frameResources.DdgiIrradianceLxHistoryWrite.IsValid == false ||
		    _frameResources.DdgiVisibilityHistoryWrite.IsValid == false ||
		    _frameResources.DdgiProbeStateWrite.IsValid == false ||
		    _frameResources.DdgiIrradianceEstimator.IsValid == false)
		{
			_ddgiHistoryValid = false;
			return;
		}

		UpdateDdgiIrradianceState(0, _frameResources.DdgiIrradianceL0HistoryRead, _ddgiHistoryReadIndex);
		UpdateDdgiIrradianceState(1, _frameResources.DdgiIrradianceLyHistoryRead, _ddgiHistoryReadIndex);
		UpdateDdgiIrradianceState(2, _frameResources.DdgiIrradianceLzHistoryRead, _ddgiHistoryReadIndex);
		UpdateDdgiIrradianceState(3, _frameResources.DdgiIrradianceLxHistoryRead, _ddgiHistoryReadIndex);

		if (_frameResources.DdgiVisibilityHistoryRead.IsValid)
		{
			_ddgiVisibilityStates[_ddgiHistoryReadIndex] = _resources.GetResourceState(_frameResources.DdgiVisibilityHistoryRead);
		}

		if (_frameResources.DdgiProbeStateRead.IsValid)
		{
			_ddgiProbeStateStates[_ddgiHistoryReadIndex] = _resources.GetResourceState(_frameResources.DdgiProbeStateRead);
		}
		if (_frameResources.DdgiProbeActivity.IsValid)
		{
			_ddgiProbeActivityState = _resources.GetResourceState(_frameResources.DdgiProbeActivity);
		}

		var ddgiWriteIndex = 1 - _ddgiHistoryReadIndex;
		UpdateDdgiIrradianceState(0, _frameResources.DdgiIrradianceL0HistoryWrite, ddgiWriteIndex);
		UpdateDdgiIrradianceState(1, _frameResources.DdgiIrradianceLyHistoryWrite, ddgiWriteIndex);
		UpdateDdgiIrradianceState(2, _frameResources.DdgiIrradianceLzHistoryWrite, ddgiWriteIndex);
		UpdateDdgiIrradianceState(3, _frameResources.DdgiIrradianceLxHistoryWrite, ddgiWriteIndex);
		_ddgiVisibilityStates[ddgiWriteIndex] = _resources.GetResourceState(_frameResources.DdgiVisibilityHistoryWrite);
		_ddgiProbeStateStates[ddgiWriteIndex] = _resources.GetResourceState(_frameResources.DdgiProbeStateWrite);
		_ddgiIrradianceEstimatorState = _resources.GetResourceState(_frameResources.DdgiIrradianceEstimator);
		_ddgiHistoryReadIndex = ddgiWriteIndex;
		_ddgiHistoryValid = true;
		_ddgiCommittedRuntimeOrigin = _frameResources.DdgiRuntimeOrigin;
		_ddgiCommittedStorageOffset = _frameResources.DdgiStorageOffset;
		_ddgiCommittedPlacementValid = true;
	}

	private void UpdateDdgiIrradianceState(int coefficientIndex, RenderGraphResourceHandle handle, int historyIndex)
	{
		if (handle.IsValid)
		{
			_ddgiIrradianceStates[coefficientIndex, historyIndex] = _resources.GetResourceState(handle);
		}
	}

	private void EnsureTemporalHistoryResources(IGfxDevice device, Int2 sceneFramebufferSize)
	{
		var deviceChanged = _historyDevice is not null && ReferenceEquals(_historyDevice, device) == false;
		var backendChanged = _historyBackendKind.HasValue && _historyBackendKind.Value != device.BackendKind;
		var sizeChanged = _historySize.X != sceneFramebufferSize.X || _historySize.Y != sceneFramebufferSize.Y;
		if (deviceChanged || backendChanged || sizeChanged)
		{
			ReleaseTemporalHistoryResources();
		}

		if (_historyColorTextures[0] is not null &&
		    _historyColorTextures[1] is not null &&
		    _historyDepthTextures[0] is not null &&
		    _historyDepthTextures[1] is not null &&
		    _fsr3CurrentLumaTextures[0] is not null &&
		    _fsr3CurrentLumaTextures[1] is not null &&
		    _fsr3AccumulationTextures[0] is not null &&
		    _fsr3AccumulationTextures[1] is not null &&
		    _fsr3FrameInfoTexture is not null)
		{
			return;
		}

		for (var i = 0; i < 2; i++)
		{
			_historyColorTextures[i] = device.CreateTexture(new TextureDescriptor(
				sceneFramebufferSize.X,
				sceneFramebufferSize.Y,
				TextureFormat.Rgba16Float,
				TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
				new ColorRGBA(0.0f, 0.0f, 0.0f, 1.0f)));
			_historyDepthTextures[i] = device.CreateTexture(new TextureDescriptor(
				sceneFramebufferSize.X,
				sceneFramebufferSize.Y,
				TextureFormat.Rgba16Float,
				TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
				new ColorRGBA(1.0f, 1.0f, 1.0f, 1.0f)));
			_fsr3CurrentLumaTextures[i] = device.CreateTexture(new TextureDescriptor(
				sceneFramebufferSize.X, sceneFramebufferSize.Y, TextureFormat.Rgba16Float,
				TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
				new ColorRGBA(0.0f, 0.0f, 0.0f, 0.0f)));
			_fsr3AccumulationTextures[i] = device.CreateTexture(new TextureDescriptor(
				sceneFramebufferSize.X, sceneFramebufferSize.Y, TextureFormat.Rgba16Float,
				TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
				new ColorRGBA(0.0f, 0.0f, 0.0f, 0.0f)));
			_historyColorStates[i] = ResourceState.UnorderedAccess;
			_historyDepthStates[i] = ResourceState.UnorderedAccess;
			_fsr3CurrentLumaStates[i] = ResourceState.UnorderedAccess;
			_fsr3AccumulationStates[i] = ResourceState.UnorderedAccess;
		}

		_fsr3FrameInfoTexture = device.CreateTexture(new TextureDescriptor(
			1, 1, TextureFormat.Rgba16Float,
			TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
			new ColorRGBA(0.0f, 0.0f, 0.0f, 0.0f)));
		_fsr3FrameInfoState = ResourceState.UnorderedAccess;

		_historyDevice = device;
		_historyBackendKind = device.BackendKind;
		_historySize = sceneFramebufferSize;
		_historyReadIndex = 0;
		_historyValid = false;
		_resetTaaHistoryThisFrame = true;
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
		var deviceChanged = _colorPyramidDevice is not null && ReferenceEquals(_colorPyramidDevice, device) == false;
		var backendChanged = _colorPyramidBackendKind.HasValue && _colorPyramidBackendKind.Value != device.BackendKind;
		var sizeChanged = _colorPyramidSize.X != sceneFramebufferSize.X || _colorPyramidSize.Y != sceneFramebufferSize.Y;
		if (deviceChanged || backendChanged || sizeChanged)
		{
			ReleaseColorPyramidResources();
		}

		if (_colorPyramidTextures.Length > 0)
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

		_colorPyramidTextures = textures;
		_colorPyramidStates = states;
		_colorPyramidDevice = device;
		_colorPyramidBackendKind = device.BackendKind;
		_colorPyramidSize = sceneFramebufferSize;
		_colorPyramidValid = false;
	}

	private void ReleaseColorPyramidResources()
	{
		for (var level = 0; level < _colorPyramidTextures.Length; level++)
		{
			EnqueueTemporalRelease(_colorPyramidDevice, _colorPyramidTextures[level], _colorPyramidStates[level]);
		}

		_colorPyramidTextures = Array.Empty<IGfxTexture>();
		_colorPyramidStates = Array.Empty<ResourceState>();
		_colorPyramidDevice = null;
		_colorPyramidBackendKind = null;
		_colorPyramidSize = Int2.Zero;
		_colorPyramidValid = false;
	}

	private void ReleaseTemporalHistoryResources()
	{
		for (var i = 0; i < 2; i++)
		{
			if (_historyColorTextures[i] is IGfxTexture colorTexture)
			{
				EnqueueTemporalRelease(_historyDevice, colorTexture, _historyColorStates[i]);
			}

			if (_historyDepthTextures[i] is IGfxTexture depthTexture)
			{
				EnqueueTemporalRelease(_historyDevice, depthTexture, _historyDepthStates[i]);
			}
			if (_fsr3CurrentLumaTextures[i] is IGfxTexture currentLumaTexture)
			{
				EnqueueTemporalRelease(_historyDevice, currentLumaTexture, _fsr3CurrentLumaStates[i]);
			}
			if (_fsr3AccumulationTextures[i] is IGfxTexture accumulationTexture)
			{
				EnqueueTemporalRelease(_historyDevice, accumulationTexture, _fsr3AccumulationStates[i]);
			}

			_historyColorTextures[i] = null;
			_historyDepthTextures[i] = null;
			_fsr3CurrentLumaTextures[i] = null;
			_fsr3AccumulationTextures[i] = null;
			_historyColorStates[i] = ResourceState.Common;
			_historyDepthStates[i] = ResourceState.Common;
			_fsr3CurrentLumaStates[i] = ResourceState.Common;
			_fsr3AccumulationStates[i] = ResourceState.Common;
		}
		if (_fsr3FrameInfoTexture is not null)
		{
			EnqueueTemporalRelease(_historyDevice, _fsr3FrameInfoTexture, _fsr3FrameInfoState);
			_fsr3FrameInfoTexture = null;
			_fsr3FrameInfoState = ResourceState.Common;
		}

		_historyBackendKind = null;
		_historyDevice = null;
		_historySize = Int2.Zero;
		_historyReadIndex = 0;
		_historyValid = false;
		_fsr3FrameIndex = 0;
	}

	private void EnsureDdgiHistoryResources(
		IGfxDevice device,
		DdgiGridShape gridShape,
		Vector3 latticeAnchor,
		float probeSpacing)
	{
		var deviceChanged = _ddgiHistoryDevice is not null && ReferenceEquals(_ddgiHistoryDevice, device) == false;
		var backendChanged = _ddgiHistoryBackendKind.HasValue && _ddgiHistoryBackendKind.Value != device.BackendKind;
		var shapeChanged = _ddgiHistoryGridShape.Equals(gridShape) == false;
		var latticeAnchorChanged = _ddgiHistoryDevice is not null && _ddgiHistoryLatticeAnchor != latticeAnchor;
		var probeSpacingChanged = _ddgiHistoryDevice is not null &&
		                          MathF.Abs(_ddgiHistoryProbeSpacing - probeSpacing) > 1e-6f;
		if (deviceChanged || backendChanged || shapeChanged || latticeAnchorChanged || probeSpacingChanged)
		{
			ReleaseDdgiHistoryResources();
		}

		var irradianceTexturesReady = true;
		for (var coefficientIndex = 0; coefficientIndex < DdgiShCoefficientCount; coefficientIndex++)
		{
			irradianceTexturesReady &= _ddgiIrradianceTextures[coefficientIndex, 0] is not null &&
			                           _ddgiIrradianceTextures[coefficientIndex, 1] is not null;
		}

		if (irradianceTexturesReady &&
		    _ddgiVisibilityTextures[0] is not null &&
		    _ddgiVisibilityTextures[1] is not null &&
		    _ddgiProbeStateTextures[0] is not null &&
		    _ddgiProbeStateTextures[1] is not null &&
		    _ddgiProbeActivityTexture is not null &&
		    _ddgiIrradianceEstimatorBuffer is not null)
		{
			return;
		}

		var visibilityAtlasSize = DdgiUtilities.GetAtlasSize(gridShape, DdgiUtilities.VisibilityTileInteriorSize);
		var shCoefficientTextureSize = DdgiUtilities.GetShCoefficientTextureSize(gridShape);
		for (var i = 0; i < 2; i++)
		{
			for (var coefficientIndex = 0; coefficientIndex < DdgiShCoefficientCount; coefficientIndex++)
			{
				_ddgiIrradianceTextures[coefficientIndex, i] = device.CreateTexture(new TextureDescriptor(
					shCoefficientTextureSize.X,
					shCoefficientTextureSize.Y,
					TextureFormat.Rgba16Float,
					TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
					new ColorRGBA(0.0f, 0.0f, 0.0f, 0.0f)));
				_ddgiIrradianceStates[coefficientIndex, i] = ResourceState.UnorderedAccess;
			}
			_ddgiVisibilityTextures[i] = device.CreateTexture(new TextureDescriptor(
				visibilityAtlasSize.X,
				visibilityAtlasSize.Y,
				TextureFormat.Rgba16Float,
				TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
				new ColorRGBA(1.0f, 1.0f, 0.0f, 1.0f)));
			_ddgiProbeStateTextures[i] = device.CreateTexture(new TextureDescriptor(
				gridShape.AtlasColumns,
				gridShape.AtlasRows,
				TextureFormat.Rgba16Float,
				TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
				new ColorRGBA(0.0f, 0.0f, 0.0f, 0.0f)));
			_ddgiVisibilityStates[i] = ResourceState.UnorderedAccess;
			_ddgiProbeStateStates[i] = ResourceState.UnorderedAccess;
		}
		_ddgiProbeActivityTexture = device.CreateTexture(new TextureDescriptor(
			shCoefficientTextureSize.X,
			shCoefficientTextureSize.Y,
			TextureFormat.Rgba16Float,
			TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
			new ColorRGBA(0.0f, 0.0f, 0.0f, 0.0f)));
		_ddgiProbeActivityState = ResourceState.UnorderedAccess;
		_ddgiIrradianceEstimatorBuffer = device.CreateBuffer(new BufferDescriptor(
			DdgiUtilities.GetIrradianceEstimatorBufferSize(gridShape),
			BufferUsage.Structured,
			BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource));
		_ddgiIrradianceEstimatorState = ResourceState.UnorderedAccess;

		_ddgiHistoryDevice = device;
		_ddgiHistoryBackendKind = device.BackendKind;
		_ddgiHistoryGridShape = gridShape;
		_ddgiHistoryLatticeAnchor = latticeAnchor;
		_ddgiHistoryProbeSpacing = probeSpacing;
		_ddgiHistoryReadIndex = 0;
		_ddgiHistoryValid = false;
	}

	private void ReleaseDdgiHistoryResources()
	{
		for (var i = 0; i < 2; i++)
		{
			for (var coefficientIndex = 0; coefficientIndex < DdgiShCoefficientCount; coefficientIndex++)
			{
				if (_ddgiIrradianceTextures[coefficientIndex, i] is IGfxTexture irradianceTexture)
				{
					EnqueueTemporalRelease(
						_ddgiHistoryDevice,
						irradianceTexture,
						_ddgiIrradianceStates[coefficientIndex, i]);
				}
				_ddgiIrradianceTextures[coefficientIndex, i] = null;
				_ddgiIrradianceStates[coefficientIndex, i] = ResourceState.Common;
			}

			if (_ddgiVisibilityTextures[i] is IGfxTexture visibilityTexture)
			{
				EnqueueTemporalRelease(_ddgiHistoryDevice, visibilityTexture, _ddgiVisibilityStates[i]);
			}

			if (_ddgiProbeStateTextures[i] is IGfxTexture probeStateTexture)
			{
				EnqueueTemporalRelease(_ddgiHistoryDevice, probeStateTexture, _ddgiProbeStateStates[i]);
			}

			_ddgiVisibilityTextures[i] = null;
			_ddgiProbeStateTextures[i] = null;
			_ddgiVisibilityStates[i] = ResourceState.Common;
			_ddgiProbeStateStates[i] = ResourceState.Common;
		}

		if (_ddgiProbeActivityTexture is IGfxTexture activityTexture)
		{
			EnqueueTemporalRelease(_ddgiHistoryDevice, activityTexture, _ddgiProbeActivityState);
		}
		_ddgiProbeActivityTexture = null;
		_ddgiProbeActivityState = ResourceState.Common;

		if (_ddgiIrradianceEstimatorBuffer is IGfxBuffer estimatorBuffer)
		{
			EnqueueTemporalBufferRelease(_ddgiHistoryDevice, estimatorBuffer);
		}
		_ddgiIrradianceEstimatorBuffer = null;
		_ddgiIrradianceEstimatorState = ResourceState.Common;

		_ddgiHistoryBackendKind = null;
		_ddgiHistoryDevice = null;
		_ddgiHistoryGridShape = default;
		_ddgiHistoryLatticeAnchor = Vector3.Zero;
		_ddgiHistoryProbeSpacing = 0.0f;
		_ddgiHistoryReadIndex = 0;
		_ddgiHistoryValid = false;
		_ddgiCommittedRuntimeOrigin = Vector3.Zero;
		_ddgiCommittedStorageOffset = default;
		_ddgiCommittedPlacementValid = false;
	}

	private void EnqueueTemporalRelease(IGfxDevice? device, IGfxTexture texture, ResourceState lastKnownState)
	{
		if (device is null)
		{
			(texture as IDisposable)?.Dispose();
			return;
		}

		var texturePoolDevice = device as ITexturePoolDevice;
		device.Retire(
			() =>
			{
				var pooled = texturePoolDevice?.ReturnTexture(texture, lastKnownState) ?? false;
				if (pooled == false)
				{
					(texture as IDisposable)?.Dispose();
				}
			},
			texture.Name ?? "Temporal render-graph texture");
	}

	private void EnqueueTemporalBufferRelease(IGfxDevice? device, IGfxBuffer buffer)
	{
		if (buffer is not IDisposable disposableBuffer)
		{
			return;
		}

		if (device is null)
		{
			disposableBuffer.Dispose();
			return;
		}

		device.Retire(disposableBuffer, buffer.Name ?? "Temporal render-graph buffer");
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

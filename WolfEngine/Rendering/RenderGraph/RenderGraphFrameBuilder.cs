#nullable enable

using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;
using WolfEngine.Rendering.UI;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering;

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
	public RenderGraphResourceHandle ResolvedSceneColor { get; init; }
	public RenderGraphResourceHandle[] BloomDownsampleLevels { get; init; }
	public RenderGraphResourceHandle[] BloomUpsampleLevels { get; init; }
	public RenderGraphResourceHandle BloomCompositeSceneColor { get; init; }
	public RenderGraphResourceHandle HistoryColorRead { get; init; }
	public RenderGraphResourceHandle HistoryColorWrite { get; init; }
	public RenderGraphResourceHandle HistoryDepthRead { get; init; }
	public RenderGraphResourceHandle HistoryDepthWrite { get; init; }
	public RenderGraphResourceHandle SkyboxEnvironment { get; init; }
	public RenderGraphResourceHandle SkyboxIrradiance { get; init; }
	public RenderGraphResourceHandle SkyboxPrefilter { get; init; }
	public RenderGraphResourceHandle SkyboxBrdfLut { get; init; }
	public RenderConfig Config { get; init; }
}

internal sealed class RenderGraphFrameBuilder
{
	private readonly RenderGraphPassSet _passSet;
	private readonly record struct PendingTemporalTextureRelease(
		IGfxTexture Texture,
		ulong RetireSubmissionId,
		ResourceState LastKnownState);

	private readonly record struct PendingTemporalBufferRelease(
		IGfxBuffer Buffer,
		ulong RetireSubmissionId);

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
	private readonly TemporalAntiAliasingPass _temporalAntiAliasingPass;
	private readonly TemporalHistoryStorePass _temporalHistoryStorePass;
	private readonly TransparentForwardPass _transparentForwardPass;
	private readonly BloomPass _bloomPass;
	private readonly TonemappingPass _tonemappingPass;
	private readonly CasSharpenPass _casSharpenPass;
	private readonly CopyToFinalPass _copyToFinalPass;
	private readonly ShadowMapPass _shadowMapPass;
	private readonly GpuDrawPass _gpuDrawPass;
	private readonly GpuDrawResources _gpuDrawResources;
	private readonly RayTracingSceneResources _rayTracingSceneResources;
	private readonly SkyboxPass _skyboxPass;
	private readonly IImGuiRenderer _imGuiRenderer;
	private SkyboxResources? _externalSkybox;
	private RenderGraphFrameResources _frameResources;
	private UiFrameData _uiFrame = UiFrameData.Empty;
	private bool _loggedUnsupportedRayTracing;
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
	private readonly Queue<PendingTemporalTextureRelease> _pendingTemporalReleases = new();
	private readonly Queue<PendingTemporalBufferRelease> _pendingTemporalBufferReleases = new();
	private readonly IGfxTexture?[] _historyColorTextures = new IGfxTexture?[2];
	private readonly IGfxTexture?[] _historyDepthTextures = new IGfxTexture?[2];
	private readonly ResourceState[] _historyColorStates = new ResourceState[2];
	private readonly ResourceState[] _historyDepthStates = new ResourceState[2];
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
	private readonly Action<RenderGraphContext> _taaResolveExecute;
	private readonly Action<RenderGraphContext> _taaHistoryStoreExecute;
	private readonly Action<RenderGraphContext> _transparentForwardExecute;
	private readonly Action<RenderGraphContext> _bloomCompositeExecute;
	private readonly Action<RenderGraphContext> _tonemappingExecute;
	private readonly Action<RenderGraphContext> _casSharpenExecute;
	private readonly Action<RenderGraphContext> _copyToFinalExecute;
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
		IShaderCompiler shaderCompiler)
	{
		_passSet = passSet;
		_rayTracingSceneResources = new RayTracingSceneResources(shaderCompiler);
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
		_temporalAntiAliasingPass = passSet.TemporalAntiAliasingPass;
		_temporalHistoryStorePass = passSet.TemporalHistoryStorePass;
		_transparentForwardPass = passSet.TransparentForwardPass;
		_bloomPass = passSet.BloomPass;
		_tonemappingPass = passSet.TonemappingPass;
		_casSharpenPass = passSet.CasSharpenPass;
		_copyToFinalPass = passSet.CopyToFinalPass;
		_shadowMapPass = passSet.ShadowMapPass;
		_gpuDrawPass = passSet.GpuDrawPass;
		_gpuDrawResources = gpuDrawResources;
		_skyboxPass = passSet.SkyboxPass;
		_imGuiRenderer = imGuiRenderer;

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
		_taaResolveExecute = ExecuteTemporalResolve;
		_taaHistoryStoreExecute = ExecuteTemporalHistoryStore;
		_transparentForwardExecute = ExecuteTransparentForward;
		_bloomCompositeExecute = ExecuteBloomComposite;
		_tonemappingExecute = ExecuteTonemapping;
		_casSharpenExecute = ExecuteCasSharpen;
		_copyToFinalExecute = ExecuteCopyToFinal;
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
		if (RequiresRayTracingScene(config) && (device.SupportsRayTracing == false || _renderer.GetPackedMeshIndexBuffer() is null))
		{
			config = CreateRayTracingDisabledConfig(config);
		}

		var taaEnabled = config.TemporalAntiAliasing.Enabled;
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
		RetirePendingTemporalReleases(_renderer.GetGfxDevice());

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
			lightingHandle = taaEnabled
				? _resources.CreateTransientTexture(new TextureDescriptor(
					sceneFramebufferSize.X,
					sceneFramebufferSize.Y,
					TextureFormat.Rgba16Float,
					TextureUsage.RenderTarget | TextureUsage.ShaderResource | TextureUsage.UnorderedAccess))
				: resolvedSceneColorHandle;

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
				TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
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
				TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
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
			ResolvedSceneColor = resolvedSceneColorHandle,
			HistoryColorRead = historyColorReadHandle,
			HistoryColorWrite = historyColorWriteHandle,
			HistoryDepthRead = historyDepthReadHandle,
			HistoryDepthWrite = historyDepthWriteHandle,
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
			RegisterSceneDebugView(SceneDebugViewIds.MotionVectors, "Motion Vectors", gbufferVelocityHandle, SceneDebugViewKind.Color);
			_sceneDebugViewOptions = BuildSceneDebugViewOptions();
		}
	}

	public void SetUiFrame(UiFrameData uiFrame)
	{
		_uiFrame = uiFrame;
	}

	public void SetSceneViewportSelection(string requestedDebugViewId)
	{
		_requestedSceneDebugViewId = NormalizeSceneDebugViewId(requestedDebugViewId);
	}

	public SceneViewportRenderState GetSceneViewportRenderState() => _resolvedSceneViewportState;
	

	[SuppressMessage("ReSharper", "RedundantArgumentDefaultValue")]
	public void Build(RenderGraph graph)
	{
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

			graph.AddPass("GBuffer", PassKind.Graphics)
				.WriteTexture(_frameResources.DecalSourceGBufferAlbedo.IsValid ? _frameResources.DecalSourceGBufferAlbedo : _frameResources.GBufferAlbedo, ResourceState.RenderTarget)
				.WriteTexture(_frameResources.DecalSourceGBufferNormal.IsValid ? _frameResources.DecalSourceGBufferNormal : _frameResources.GBufferNormal, ResourceState.RenderTarget)
				.WriteTexture(_frameResources.DecalSourceGBufferMaterial.IsValid ? _frameResources.DecalSourceGBufferMaterial : _frameResources.GBufferMaterial, ResourceState.RenderTarget)
				.WriteTexture(_frameResources.DecalSourceGBufferEmissive.IsValid ? _frameResources.DecalSourceGBufferEmissive : _frameResources.GBufferEmissive, ResourceState.RenderTarget)
				.WriteTexture(_frameResources.GBufferVelocity, ResourceState.RenderTarget)
				.WriteTexture(_frameResources.GBufferDepth, ResourceState.DepthWrite)
				.SetExecute(_gbufferExecute);

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

			if (_frameResources.Config.TemporalAntiAliasing.Enabled &&
			    _frameResources.HistoryColorRead.IsValid &&
			    _frameResources.HistoryColorWrite.IsValid &&
			    _frameResources.HistoryDepthRead.IsValid &&
			    _frameResources.HistoryDepthWrite.IsValid)
			{
				graph.AddPass("TAA Resolve", PassKind.Compute)
					.ReadTexture(_frameResources.LightingBuffer, ResourceState.ShaderResource)
					.ReadTexture(_frameResources.GBufferVelocity, ResourceState.ShaderResource)
					.ReadTexture(_frameResources.GBufferDepth, ResourceState.ShaderResource)
					.ReadTexture(_frameResources.HistoryColorRead, ResourceState.ShaderResource)
					.ReadTexture(_frameResources.HistoryDepthRead, ResourceState.ShaderResource)
					.WriteTexture(_frameResources.ResolvedSceneColor, ResourceState.UnorderedAccess)
					.SetExecute(_taaResolveExecute);

				graph.AddPass("TAA History Store", PassKind.Compute)
					.ReadTexture(_frameResources.ResolvedSceneColor, ResourceState.ShaderResource)
					.ReadTexture(_frameResources.GBufferDepth, ResourceState.ShaderResource)
					.WriteTexture(_frameResources.HistoryColorWrite, ResourceState.UnorderedAccess)
					.WriteTexture(_frameResources.HistoryDepthWrite, ResourceState.UnorderedAccess)
					.SetExecute(_taaHistoryStoreExecute);
			}

			var transparentForwardBuilder = graph.AddPass("Transparent Forward", PassKind.Graphics)
				.ReadTexture(_frameResources.GBufferDepth, ResourceState.DepthWrite)
				.ReadTexture(_frameResources.ShadowMapDepth0, ResourceState.ShaderResource)
				.ReadTexture(_frameResources.ShadowMapDepth1, ResourceState.ShaderResource)
				.ReadTexture(_frameResources.ShadowMapDepth2, ResourceState.ShaderResource)
				.WriteTexture(_frameResources.ResolvedSceneColor, ResourceState.RenderTarget);
			if (_frameResources.DdgiProbeStateWrite.IsValid)
			{
				transparentForwardBuilder.ReadTexture(
					_frameResources.DdgiProbeStateWrite,
					ResourceState.ShaderResource);
			}
			
			ReadSkyboxTextures(transparentForwardBuilder);
			transparentForwardBuilder.SetExecute(_transparentForwardExecute);

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

			graph.AddPass("CAS Sharpen", PassKind.Compute)
				.ReadTexture(_frameResources.TonemappedLinearSceneColor, ResourceState.ShaderResource)
				.WriteTexture(_frameResources.DisplayLinearSceneColor, ResourceState.UnorderedAccess)
				.SetExecute(_casSharpenExecute);

			graph.AddPass("Copy To Final", PassKind.Compute)
				.ReadTexture(_frameResources.DisplayLinearSceneColor, ResourceState.ShaderResource)
				.WriteTexture(_frameResources.EncodedSceneColor, ResourceState.UnorderedAccess)
				.WriteTexture(_frameResources.FinalColor, ResourceState.UnorderedAccess)
				.SetExecute(_copyToFinalExecute);
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
		_gpuDrawPass.RecordUpdate(context);
		if (RequiresRayTracingScene(_frameResources.Config))
		{
			_rayTracingSceneResources.RecordUpdate(context, _renderer, _frameGpuDrawUpdates);
		}
	}

	private void ExecuteGpuDrawCullShadow(RenderGraphContext context)
	{
		var sceneData = context.SceneData!;
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
	}

	private void ExecuteShadowMap(RenderGraphContext context)
	{
		var device = _renderer.GetGfxDevice();
		var cascadeCount = _shadowMapPass.GetCurrentFrameData().CascadeCount;
		for (var cascadeIndex = 0; cascadeIndex < cascadeCount; cascadeIndex++)
		{
			var shadowMapHandle = GetShadowMapHandle(_frameResources, cascadeIndex);
			var depthTexture = context.GetTexture(shadowMapHandle);
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
		_gpuDrawPass.RecordCull(context, context.SceneData!);
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
			CameraLayout = _gpuDrawResources.GBufferCameraLayout,
			CameraBuffer = _gpuDrawResources.CameraBuffer,
			SkyboxEnvironment = DescriptorHandle.Invalid,
			SkyboxSampler = DescriptorHandle.Invalid
		};

		GBufferPass.Record(context, gbufferConfig, context.SceneData!);
	}

	private void ExecuteScreenSpaceDecal(RenderGraphContext context)
	{
		var config = _screenSpaceDecalPass.BuildConfig(
			context,
			_frameResources,
			_renderer.GetGfxDevice(),
			_gpuDrawResources,
			context.SceneData!);
		_screenSpaceDecalPass.Record(context, in config, context.SceneData!);
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
			context.SceneData!);
		_deferredLightingPass.Record(context, ref config, context.SceneData!);
	}

	private void ExecuteClusteredLightingBuild(RenderGraphContext context)
	{
		var config = _clusteredLightingPass.BuildConfig(
			_renderer.GetGfxDevice(),
			_gpuDrawResources,
			_frameResources.SceneFramebufferSize);
		_clusteredLightingPass.Record(context, in config, context.SceneData!, ClusteredLightingPass.Stage.BuildClusters);
	}

	private void ExecuteClusteredLightingWrite(RenderGraphContext context)
	{
		var config = _clusteredLightingPass.BuildConfig(
			_renderer.GetGfxDevice(),
			_gpuDrawResources,
			_frameResources.SceneFramebufferSize);
		_clusteredLightingPass.Record(context, in config, context.SceneData!, ClusteredLightingPass.Stage.WriteLightIndices);
	}

	private void ExecuteTemporalResolve(RenderGraphContext context)
	{
		var config = _temporalAntiAliasingPass.BuildConfig(
			context,
			_frameResources,
			_renderer.GetGfxDevice(),
			_historyValid,
			_resetTaaHistoryThisFrame || context.SceneData!.ResetHistory);
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
		_ambientOcclusionPass.Record(context, in config, context.SceneData!);
	}

	private void ExecuteAmbientOcclusionBlurHorizontal(RenderGraphContext context)
	{
		var config = _ambientOcclusionBlurPass.BuildConfig(
			context,
			_frameResources,
			_renderer.GetGfxDevice(),
			blurHorizontally: true);
		_ambientOcclusionBlurPass.Record(context, in config, context.SceneData!);
	}

	private void ExecuteAmbientOcclusionBlurVertical(RenderGraphContext context)
	{
		var config = _ambientOcclusionBlurPass.BuildConfig(
			context,
			_frameResources,
			_renderer.GetGfxDevice(),
			blurHorizontally: false);
		_ambientOcclusionBlurPass.Record(context, in config, context.SceneData!);
	}

	private void ExecuteAmbientOcclusionUpsample(RenderGraphContext context)
	{
		var config = _ambientOcclusionUpsamplePass.BuildConfig(
			context,
			_frameResources,
			_renderer.GetGfxDevice());
		_ambientOcclusionUpsamplePass.Record(context, in config, context.SceneData!);
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
			context.SceneData!,
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
			context.SceneData!);
		_transparentForwardPass.Record(context, in config, context.SceneData!);
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

	private void ExecuteImGui(RenderGraphContext context)
	{
		var finalColor = context.GetTexture(_frameResources.FinalColor);
		_imGuiRenderer.EnsureResources(_renderer.GetGfxDevice(), _uiFrame);
		_imGuiRenderer.Record(context, _uiFrame, finalColor, clearTarget: _frameResources.SceneEnabled == false);
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
		if (_frameResources.Config.TemporalAntiAliasing.Enabled == false || _frameResources.SceneEnabled == false)
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
			_historyReadIndex = writeIndex;
			_historyValid = true;
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
		    _historyDepthTextures[1] is not null)
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
			_historyColorStates[i] = ResourceState.UnorderedAccess;
			_historyDepthStates[i] = ResourceState.UnorderedAccess;
		}

		_historyDevice = device;
		_historyBackendKind = device.BackendKind;
		_historySize = sceneFramebufferSize;
		_historyReadIndex = 0;
		_historyValid = false;
		_resetTaaHistoryThisFrame = true;
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

			_historyColorTextures[i] = null;
			_historyDepthTextures[i] = null;
			_historyColorStates[i] = ResourceState.Common;
			_historyDepthStates[i] = ResourceState.Common;
		}

		_historyBackendKind = null;
		_historyDevice = null;
		_historySize = Int2.Zero;
		_historyReadIndex = 0;
		_historyValid = false;
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
		var retireSubmissionId = 0UL;
		if (device is IGpuSubmissionTimeline submissionTimeline)
		{
			retireSubmissionId = submissionTimeline.LastSubmittedId;
		}

		_pendingTemporalReleases.Enqueue(new PendingTemporalTextureRelease(texture, retireSubmissionId, lastKnownState));
	}

	private void EnqueueTemporalBufferRelease(IGfxDevice? device, IGfxBuffer buffer)
	{
		var retireSubmissionId = device is IGpuSubmissionTimeline submissionTimeline
			? submissionTimeline.LastSubmittedId
			: 0UL;
		_pendingTemporalBufferReleases.Enqueue(new PendingTemporalBufferRelease(buffer, retireSubmissionId));
	}

	private void RetirePendingTemporalReleases(IGfxDevice device)
	{
		ArgumentNullException.ThrowIfNull(device);

		var completedId = ulong.MaxValue;
		ITexturePoolDevice? texturePoolDevice = null;
		if (device is IGpuSubmissionTimeline submissionTimeline)
		{
			submissionTimeline.PumpCompleted();
			completedId = submissionTimeline.CompletedId;
		}

		if (device is ITexturePoolDevice pooledDevice)
		{
			texturePoolDevice = pooledDevice;
		}

		while (_pendingTemporalReleases.Count > 0)
		{
			var pending = _pendingTemporalReleases.Peek();
			if (pending.RetireSubmissionId > completedId)
			{
				break;
			}

			var pooled = texturePoolDevice?.ReturnTexture(pending.Texture, pending.LastKnownState) ?? false;
			if (pooled == false && pending.Texture is IDisposable disposableTexture)
			{
				disposableTexture.Dispose();
			}

			_pendingTemporalReleases.Dequeue();
		}

		while (_pendingTemporalBufferReleases.Count > 0)
		{
			var pending = _pendingTemporalBufferReleases.Peek();
			if (pending.RetireSubmissionId > completedId)
			{
				break;
			}

			if (pending.Buffer is IDisposable disposableBuffer)
			{
				disposableBuffer.Dispose();
			}

			_pendingTemporalBufferReleases.Dequeue();
		}
	}

	private static bool HasAmbientOcclusion(RenderConfig config)
	{
		return config.AmbientOcclusion.Enabled &&
		       (config.AmbientOcclusion.Mode == AmbientOcclusionMode.RayTraced ||
		        (config.AmbientOcclusion.VisibilityBitmaskSettings.SliceCount > 0 &&
		         config.AmbientOcclusion.VisibilityBitmaskSettings.StepCount > 0));
	}

	private static bool HasRayTracedDdgi(RenderConfig config) => DdgiUtilities.IsRayTracedDdgiEnabled(config);

	private static bool RequiresRayTracingScene(RenderConfig config)
	{
		return (config.AmbientOcclusion.Enabled && config.AmbientOcclusion.Mode == AmbientOcclusionMode.RayTraced) ||
		       HasRayTracedDdgi(config);
	}

	private static RenderConfig CreateRayTracingDisabledConfig(RenderConfig source)
	{
		var ambientOcclusion = source.AmbientOcclusion;
		ambientOcclusion.Enabled = false;
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

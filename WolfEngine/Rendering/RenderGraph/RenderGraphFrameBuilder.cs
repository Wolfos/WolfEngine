#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Rendering;

public readonly struct RenderGraphFrameResources
{
	public Int2 FramebufferSize { get; init; }
	public Int2 SceneFramebufferSize { get; init; }
	public bool SceneEnabled { get; init; }
	public RenderGraphResourceHandle TonemappedLinearSceneColor { get; init; }
	public RenderGraphResourceHandle TonemappedSceneColor { get; init; }
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
	public RenderGraphResourceHandle ShadowMapDepth0 { get; init; }
	public RenderGraphResourceHandle ShadowMapDepth1 { get; init; }
	public RenderGraphResourceHandle ShadowMapDepth2 { get; init; }
	public RenderGraphResourceHandle LightingBuffer { get; init; }
	public RenderGraphResourceHandle ResolvedSceneColor { get; init; }
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
	private readonly record struct PendingTemporalTextureRelease(
		IGfxTexture Texture,
		ulong RetireSubmissionId,
		ResourceState LastKnownState);

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
	private readonly ClusteredLightingPass _clusteredLightingPass;
	private readonly GBufferDecalSeedPass _gBufferDecalSeedPass;
	private readonly ScreenSpaceDecalPass _screenSpaceDecalPass;
	private readonly DeferredLightingPass _deferredLightingPass;
	private readonly TemporalAntiAliasingPass _temporalAntiAliasingPass;
	private readonly TemporalHistoryStorePass _temporalHistoryStorePass;
	private readonly TransparentForwardPass _transparentForwardPass;
	private readonly TonemappingPass _tonemappingPass;
	private readonly CasSharpenPass _casSharpenPass;
	private readonly CopyToFinalPass _copyToFinalPass;
	private readonly ShadowMapPass _shadowMapPass;
	private readonly GpuDrawPass _gpuDrawPass;
	private readonly GpuDrawResources _gpuDrawResources;
	private readonly RayTracingSceneResources _rayTracingSceneResources = new();
	private readonly SkyboxPass _skyboxPass;
	private readonly IImGuiRenderer _imGuiRenderer;
	private SkyboxResources? _externalSkybox;
	private RenderGraphFrameResources _frameResources;
	private UiFrameData _uiFrame = UiFrameData.Empty;
	private readonly List<SceneDebugViewRegistration> _sceneDebugViews = [];
	private readonly List<GpuDrawUpdate> _frameGpuDrawUpdates = [];
	private SceneDebugViewOption[] _sceneDebugViewOptions = Array.Empty<SceneDebugViewOption>();
	private string _requestedSceneDebugViewId = SceneDebugViewIds.FinalColor;
	private SceneViewportRenderState _resolvedSceneViewportState = SceneViewportRenderState.Empty;
	private bool _hasPreviousFrameShape;
	private Int2 _previousFramebufferSize;
	private Int2 _previousSceneFramebufferSize;
	private bool _previousSceneEnabled;
	private bool _previousTaaEnabled;
	private bool _historyValid;
	private bool _resetTaaHistoryThisFrame;
	private IGfxDevice? _historyDevice;
	private GraphicsBackendKind? _historyBackendKind;
	private Int2 _historySize;
	private int _historyReadIndex;
	private readonly Queue<PendingTemporalTextureRelease> _pendingTemporalReleases = new();
	private readonly IGfxTexture?[] _historyColorTextures = new IGfxTexture?[2];
	private readonly IGfxTexture?[] _historyDepthTextures = new IGfxTexture?[2];
	private readonly ResourceState[] _historyColorStates = new ResourceState[2];
	private readonly ResourceState[] _historyDepthStates = new ResourceState[2];
	
	private readonly Action<RenderGraphContext> _gbufferExecute;
	private readonly Action<RenderGraphContext> _ambientOcclusionExecute;
	private readonly Action<RenderGraphContext> _ambientOcclusionBlurHorizontalExecute;
	private readonly Action<RenderGraphContext> _ambientOcclusionBlurVerticalExecute;
	private readonly Action<RenderGraphContext> _ambientOcclusionUpsampleExecute;
	private readonly Action<RenderGraphContext> _clusteredLightingBuildExecute;
	private readonly Action<RenderGraphContext> _clusteredLightingWriteExecute;
	private readonly Action<RenderGraphContext> _gBufferDecalSeedExecute;
	private readonly Action<RenderGraphContext> _screenSpaceDecalExecute;
	private readonly Action<RenderGraphContext> _deferredLightingExecute;
	private readonly Action<RenderGraphContext> _taaResolveExecute;
	private readonly Action<RenderGraphContext> _taaHistoryStoreExecute;
	private readonly Action<RenderGraphContext> _transparentForwardExecute;
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
		IImGuiRenderer imGuiRenderer)
	{
		_resources = resources;
		_renderer = renderer;
		_ambientOcclusionPass = passSet.AmbientOcclusionPass;
		_ambientOcclusionBlurPass = passSet.AmbientOcclusionBlurPass;
		_ambientOcclusionUpsamplePass = passSet.AmbientOcclusionUpsamplePass;
		_clusteredLightingPass = passSet.ClusteredLightingPass;
		_gBufferDecalSeedPass = passSet.GBufferDecalSeedPass;
		_screenSpaceDecalPass = passSet.ScreenSpaceDecalPass;
		_deferredLightingPass = passSet.DeferredLightingPass;
		_temporalAntiAliasingPass = passSet.TemporalAntiAliasingPass;
		_temporalHistoryStorePass = passSet.TemporalHistoryStorePass;
		_transparentForwardPass = passSet.TransparentForwardPass;
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
		_clusteredLightingBuildExecute = ExecuteClusteredLightingBuild;
		_clusteredLightingWriteExecute = ExecuteClusteredLightingWrite;
		_gBufferDecalSeedExecute = ExecuteGBufferDecalSeed;
		_screenSpaceDecalExecute = ExecuteScreenSpaceDecal;
		_deferredLightingExecute = ExecuteDeferredLighting;
		_taaResolveExecute = ExecuteTemporalResolve;
		_taaHistoryStoreExecute = ExecuteTemporalHistoryStore;
		_transparentForwardExecute = ExecuteTransparentForward;
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
		RenderConfig config)
	{
		var taaEnabled = config.TemporalAntiAliasing.Enabled;
		var frameShapeChanged = _hasPreviousFrameShape == false ||
		                        _previousFramebufferSize.X != framebufferSize.X ||
		                        _previousFramebufferSize.Y != framebufferSize.Y ||
		                        _previousSceneFramebufferSize.X != sceneFramebufferSize.X ||
		                        _previousSceneFramebufferSize.Y != sceneFramebufferSize.Y ||
		                        _previousSceneEnabled != sceneEnabled;
		InvalidateTransientPoolIfFrameShapeChanged(framebufferSize, sceneFramebufferSize, sceneEnabled);
		_sceneDebugViews.Clear();
		_sceneDebugViewOptions = Array.Empty<SceneDebugViewOption>();
		_resolvedSceneViewportState = SceneViewportRenderState.Empty;
		_resetTaaHistoryThisFrame = frameShapeChanged || (taaEnabled && _previousTaaEnabled == false);
		_previousTaaEnabled = taaEnabled;
		RetirePendingTemporalReleases(_renderer.GetGfxDevice());

		_skyboxPass.PrepareFrame(_renderer.GetGfxDevice(), sunDirection, sunIntensityScale);
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
		var resolvedSceneColorHandle = default(RenderGraphResourceHandle);
		var historyColorReadHandle = default(RenderGraphResourceHandle);
		var historyColorWriteHandle = default(RenderGraphResourceHandle);
		var historyDepthReadHandle = default(RenderGraphResourceHandle);
		var historyDepthWriteHandle = default(RenderGraphResourceHandle);
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
				TextureFormat.Rgba8Unorm,
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
					TextureFormat.Rgba8Unorm,
					TextureUsage.RenderTarget | TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
					new ColorRGBA(0.0f, 0.0f, 0.0f, 1.0f)));
			}
			shadowMapHandle0 = _resources.CreateTransientTexture(new TextureDescriptor(
				ShadowMapPass.CascadeResolution,
				ShadowMapPass.CascadeResolution,
				TextureFormat.D32Float,
				TextureUsage.DepthStencil | TextureUsage.ShaderResource,
				default(ColorRGBA),
				1.0f));
			shadowMapHandle1 = _resources.CreateTransientTexture(new TextureDescriptor(
				ShadowMapPass.CascadeResolution,
				ShadowMapPass.CascadeResolution,
				TextureFormat.D32Float,
				TextureUsage.DepthStencil | TextureUsage.ShaderResource,
				default(ColorRGBA),
				1.0f));
			shadowMapHandle2 = _resources.CreateTransientTexture(new TextureDescriptor(
				ShadowMapPass.CascadeResolution,
				ShadowMapPass.CascadeResolution,
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
				}
			}
		}

		var tonemappedLinearSceneColorHandle = sceneEnabled
			? _resources.CreateTransientTexture(new TextureDescriptor(
				framebufferSize.X,
				framebufferSize.Y,
				TextureFormat.Rgba16Float,
				TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
				new ColorRGBA(0.05f, 0.05f, 0.05f, 1.0f)))
			: default;

		var tonemappedSceneColorHandle = sceneEnabled
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
			TonemappedSceneColor = tonemappedSceneColorHandle,
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
			ShadowMapDepth0 = shadowMapHandle0,
			ShadowMapDepth1 = shadowMapHandle1,
			ShadowMapDepth2 = shadowMapHandle2,
			LightingBuffer = lightingHandle,
			ResolvedSceneColor = resolvedSceneColorHandle,
			HistoryColorRead = historyColorReadHandle,
			HistoryColorWrite = historyColorWriteHandle,
			HistoryDepthRead = historyDepthReadHandle,
			HistoryDepthWrite = historyDepthWriteHandle,
			SkyboxEnvironment = skyboxEnvHandle,
			SkyboxIrradiance = skyboxIrrHandle,
			SkyboxPrefilter = skyboxPrefilterHandle,
			SkyboxBrdfLut = skyboxBrdfHandle,
			Config = config
		};

		if (sceneEnabled)
		{
			RegisterSceneDebugView(SceneDebugViewIds.FinalColor, "Final Color", _frameResources.TonemappedSceneColor, SceneDebugViewKind.Color);
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
			
			ReadSkyboxTextures(transparentForwardBuilder);
			
			transparentForwardBuilder.SetExecute(_transparentForwardExecute);

			graph.AddPass("Tonemapping", PassKind.Compute)
				.ReadTexture(_frameResources.ResolvedSceneColor, ResourceState.ShaderResource)
				.WriteTexture(_frameResources.TonemappedLinearSceneColor, ResourceState.UnorderedAccess)
				.SetExecute(_tonemappingExecute);

			graph.AddPass("CAS Sharpen", PassKind.Compute)
				.ReadTexture(_frameResources.TonemappedLinearSceneColor, ResourceState.ShaderResource)
				.WriteTexture(_frameResources.TonemappedSceneColor, ResourceState.UnorderedAccess)
				.SetExecute(_casSharpenExecute);

			graph.AddPass("Copy To Final", PassKind.Compute)
				.ReadTexture(_frameResources.TonemappedSceneColor, ResourceState.ShaderResource)
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
		if (_frameResources.Config.AmbientOcclusion.Enabled &&
		    _frameResources.Config.AmbientOcclusion.Mode == AmbientOcclusionMode.RayTraced)
		{
			_rayTracingSceneResources.RecordUpdate(context, _renderer, _frameGpuDrawUpdates);
		}

		_gpuDrawPass.RecordUpdate(context);
	}

	private void ExecuteGpuDrawCullShadow(RenderGraphContext context)
	{
		var sceneData = context.SceneData!;
		_shadowMapPass.PrepareFrame(sceneData);
		var shadowData = _shadowMapPass.GetCurrentFrameData();
		if (shadowData.Enabled == false)
		{
			return;
		}

		_gpuDrawPass.RecordCullForView(
			context,
			shadowData.GetCascadeViewProjection(ShadowMapPass.CascadeCount - 1),
			sceneData.CameraOrigin,
			useShadowBuffers: true);
	}

	private void ExecuteShadowMap(RenderGraphContext context)
	{
		var device = _renderer.GetGfxDevice();
		for (var cascadeIndex = 0; cascadeIndex < ShadowMapPass.CascadeCount; cascadeIndex++)
		{
			var shadowMapHandle = GetShadowMapHandle(_frameResources, cascadeIndex);
			var depthTexture = context.GetTexture(shadowMapHandle);
			_shadowMapPass.EnsureIndirectResources(device, cascadeIndex);
			_gpuDrawPass.EnsureIndirectCommandsForPass(
				context.GpuDrawDatabase,
				_shadowMapPass.GetIndirectCommandSet(cascadeIndex),
				DrawPassParticipation.ShadowCaster,
				SharedDrawIndirectEncodeResources.FromGpuDrawResources(_gpuDrawResources, _gpuDrawResources.ShadowCameraBuffer),
				lane => _shadowMapPass.HasIndirectLane(cascadeIndex, lane),
				lane => _shadowMapPass.GetBufferBindings(cascadeIndex, lane));
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
			VisibleDrawIdsPerExecutionLaneBuffer = _gpuDrawResources.VisibleDrawIdsPerExecutionLaneBuffer,
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

	private void ExecuteTransparentForward(RenderGraphContext context)
	{
		var device = _renderer.GetGfxDevice();
		_transparentForwardPass.EnsureIndirectResources(device);
		_gpuDrawPass.EnsureIndirectCommandsForPass(
			context.GpuDrawDatabase,
			_transparentForwardPass.IndirectCommandSet,
			DrawPassParticipation.ForwardTransparent,
			SharedDrawIndirectEncodeResources.FromGpuDrawResources(_gpuDrawResources, _gpuDrawResources.CameraBuffer),
			lane => _transparentForwardPass.HasIndirectLane(lane),
			lane => _transparentForwardPass.GetBufferBindings(lane));
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

	private void InvalidateTransientPoolIfFrameShapeChanged(Int2 framebufferSize, Int2 sceneFramebufferSize, bool sceneEnabled)
	{
		var changed = _hasPreviousFrameShape == false ||
		              _previousFramebufferSize.X != framebufferSize.X ||
		              _previousFramebufferSize.Y != framebufferSize.Y ||
		              _previousSceneFramebufferSize.X != sceneFramebufferSize.X ||
		              _previousSceneFramebufferSize.Y != sceneFramebufferSize.Y ||
		              _previousSceneEnabled != sceneEnabled;
		if (changed == false)
		{
			return;
		}

		_resources.InvalidateTransientTexturePool();
		_previousFramebufferSize = framebufferSize;
		_previousSceneFramebufferSize = sceneFramebufferSize;
		_previousSceneEnabled = sceneEnabled;
		_hasPreviousFrameShape = true;
	}

	public void CompleteFrame()
	{
		if (_frameResources.Config.TemporalAntiAliasing.Enabled == false || _frameResources.SceneEnabled == false)
		{
			_historyValid = false;
			return;
		}

		if (_frameResources.HistoryColorWrite.IsValid == false ||
		    _frameResources.HistoryDepthWrite.IsValid == false)
		{
			_historyValid = false;
			return;
		}

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

	private void EnqueueTemporalRelease(IGfxDevice? device, IGfxTexture texture, ResourceState lastKnownState)
	{
		var retireSubmissionId = 0UL;
		if (device is IGpuSubmissionTimeline submissionTimeline)
		{
			retireSubmissionId = submissionTimeline.LastSubmittedId;
		}

		_pendingTemporalReleases.Enqueue(new PendingTemporalTextureRelease(texture, retireSubmissionId, lastKnownState));
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
	}

	private static bool HasAmbientOcclusion(RenderConfig config)
	{
		return config.AmbientOcclusion.Enabled &&
		       (config.AmbientOcclusion.Mode == AmbientOcclusionMode.RayTraced ||
		        (config.AmbientOcclusion.VisibilityBitmaskSettings.SliceCount > 0 &&
		         config.AmbientOcclusion.VisibilityBitmaskSettings.StepCount > 0));
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

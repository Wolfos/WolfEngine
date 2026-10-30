using System.Numerics;
using WolfEngine.ECS;
using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Rendering;

/// <summary>
/// Everything one <see cref="RenderViewId"/> carries from frame to frame: its temporal anti-aliasing and
/// FSR3 history, its volumetric fog froxel history, its colour pyramid, its DDGI probe volume, and the
/// previous-frame shape those are only valid against.
/// </summary>
/// <remarks>
/// This state used to live on <see cref="RenderGraphFrameBuilder"/> as one set of fields, which is what made
/// a second concurrent view impossible: two views resolving at different sizes, with different cameras, would
/// have overwritten each other's history every frame and read back the other's pixels. It is grouped here so
/// each view owns its own, and so a closed view can retire its GPU resources on its own schedule through
/// <see cref="ReleaseAll"/> rather than living until the renderer shuts down.
///
/// The fields are public because this is a state bag for one collaborator, not an abstraction. Each ping-pong
/// history is a <see cref="ViewHistory{TShape}"/>, which owns its own allocation, swap, invalidation and release;
/// the frame builder only decides each frame which shape an effect needs and whether it runs. The colour pyramid
/// and the companion resources beside some histories are still allocated by the frame builder.
/// </remarks>
internal sealed class RenderViewState
{
	public RenderViewState(RenderViewId view)
	{
		View = view;
		Name = view == RenderViewId.Primary ? "primary" : view.ToString();
	}

	public RenderViewId View { get; }

	/// <summary>This frame's render features, from the view's snapshot. Valid from BeginViewFrame until recording ends.</summary>
	public IReadOnlyList<Features.RenderFeatureFrame> RenderFeatureFrames { get; set; } = [];

	/// <summary>Changes whenever this slot is bound to a new view, even if the same world is rebound.</summary>
	public long BindingGeneration;

	/// <summary>
	/// The binding generation this view's temporal history was built for. Render thread only: when it differs
	/// from <see cref="BindingGeneration"/> the view was rebound to another world, and history accumulated
	/// against the old world's camera and content is dropped before the view records again.
	/// </summary>
	public long HistoryBindingGeneration;

	/// <summary>
	/// Drops everything this view carried over from its previous world: temporal, fog, colour-pyramid and DDGI
	/// history, and the projection motion vectors reproject through. The output target is kept, because the UI
	/// keeps sampling it across the change.
	/// </summary>
	public void ResetHistoryForNewBinding()
	{
		ReleaseTemporalHistoryResources();
		ReleaseFogHistoryResources();
		ReleaseColorPyramidResources();
		ReleaseDdgiHistoryResources();
		ReleaseFinalGatherHistoryResources();
		HasPreviousFrameShape = false;
		HasPreviousResolvedProjection = false;
		PreviousResolvedProjection = Matrix4x4.Identity;
		PreviousJitterPhaseCount = 0;
		HistoryBindingGeneration = BindingGeneration;
	}

	/// <summary>The world this view renders, or null for the primary view before anything bound one.</summary>
	public World? World;

	/// <summary>Short name, qualified into pass names so a crash log names the view a pass belonged to.</summary>
	public string Name
	{
		get => _name;
		set
		{
			_name = value;
			_qualifiedPassNames.Clear();
		}
	}

	private string _name = string.Empty;

	// Pass names qualified with this view's name, cached because passes are recorded every frame.
	private readonly Dictionary<string, string> _qualifiedPassNames = new(StringComparer.Ordinal);


	/// <summary>
	/// <paramref name="passName"/> as recorded for this view. DRED breadcrumbs and GPU profiler scopes are keyed by
	/// pass name, so two views recording the same unqualified name would make a device-removal log unattributable
	/// and merge unrelated timings. The primary view keeps plain names, so existing names stay stable.
	/// </summary>
	public string QualifyPassName(string passName)
	{
		if (View == RenderViewId.Primary)
		{
			return passName;
		}

		if (_qualifiedPassNames.TryGetValue(passName, out var qualified) == false)
		{
			qualified = $"{passName} [{Name}]";
			_qualifiedPassNames.Add(passName, qualified);
		}

		return qualified;
	}

	/// <summary>What this view resolved to for the UI to sample, as of the last frame it was recorded.</summary>
	public SceneViewportRenderState ResolvedSceneViewportState = SceneViewportRenderState.Empty;

	/// <summary>
	/// The texture this view is drawn into and handed to the UI. One per view: views size independently, and
	/// a shared target would mean the last view recorded overwrote what every earlier one had drawn.
	/// </summary>
	public readonly EditorSceneRenderTargetManager SceneRenderTarget = new();

	/// <summary>
	/// The finished, display-encoded image of a texture-output view: what the UI shows for it. It persists across
	/// frames so the UI can keep showing the last image on frames that draw no scene, such as when the editor
	/// limits how often the viewport renders. Backbuffer views encode into a transient texture instead.
	/// </summary>
	public readonly EditorSceneRenderTargetManager PresentedSceneTarget = new(TextureFormat.Bgra8Unorm);

	// This frame's transient resources and debug-view selection. They are rebuilt every frame, but they are
	// held here rather than on the frame builder because passes read them when they execute, not when they
	// are recorded: with several views in one graph, a builder field would hold whichever view was recorded
	// last by the time any pass ran.
	public RenderViewResources FrameResources;
	public readonly List<RenderGraphFrameBuilder.SceneDebugViewRegistration> SceneDebugViews = [];
	public SceneDebugViewOption[] SceneDebugViewOptions = Array.Empty<SceneDebugViewOption>();
	public string RequestedSceneDebugViewId = SceneDebugViewIds.FinalColor;

	/// <summary>
	/// This frame's lights, made relative to this view's camera. Scene data holds the list by reference until
	/// the frame's passes have run, so each view needs its own rather than one list rebuilt per view.
	/// </summary>
	public readonly List<LightPacket> RenderLights = new();

	/// <summary>This frame's draw changes for this view's world, kept for its ray-tracing update.</summary>
	public readonly List<GpuDrawUpdate> RayTracingUpdates = new();

	/// <summary>
	/// Whether this view's image is the window's presentation: it writes the shared final target and carries the
	/// gameplay screen UI. The primary view does — it is the editor's scene view and the standalone game — as does
	/// any view that renders to the backbuffer. Every other view writes only its own image.
	/// </summary>
	public bool OwnsPresentation => View == RenderViewId.Primary || Output == RenderViewOutput.Backbuffer;

	/// <summary>Where this view's image goes. Fixed for the life of the view.</summary>
	public RenderViewOutput Output = RenderViewOutput.Texture;

	/// <summary>The size this view last rendered at, which its camera's jitter and projection derive from.</summary>
	public Int2 SceneRenderSize;

	public Matrix4x4 ResolvedProjection = Matrix4x4.Identity;
	public Matrix4x4 PreviousResolvedProjection = Matrix4x4.Identity;
	public bool HasPreviousResolvedProjection;

	public int PreviousJitterPhaseCount;
	public bool SceneDataPreviousTaaEnabled;
	public AntiAliasingMode SceneDataPreviousAntiAliasingMode;

	// Previous-frame shape. History is only valid while the shape it was produced at still holds, so each
	// view tracks its own: one view resizing must not invalidate another's history. The window framebuffer
	// size is included because a view can be sized relative to it.
	public bool HasPreviousFrameShape;
	public Int2 PreviousFramebufferSize;
	public Int2 PreviousSceneFramebufferSize;
	public int PreviousShadowMapResolution;
	public bool PreviousSceneEnabled;
	public bool PreviousTaaEnabled;
	public AntiAliasingMode PreviousAntiAliasingMode;

	// Temporal anti-aliasing and FSR3 history. FSR3 keeps two more ping-pong textures alongside TAA's colour and
	// depth; both histories are allocated, swapped and released together.
	public bool ResetTaaHistoryThisFrame;
	public readonly ViewHistory<TemporalHistoryShape> TemporalHistory = new("Temporal AA", TemporalHistorySlot.Count, DescribeTemporalHistory);
	public readonly ViewHistory<Int2> Fsr3History = new("FSR3", Fsr3HistorySlot.Count, DescribeFsr3History);
	public IGfxTexture? Fsr3FrameInfoTexture;
	public ResourceState Fsr3FrameInfoState = ResourceState.UnorderedAccess;
	public uint Fsr3FrameIndex;

	/// <summary>
	/// Seconds since this view last recorded, which is what temporal effects advance by. It is not the presented
	/// frame's time: frames that run no graph, such as when the editor limits the viewport's rate, still present.
	/// </summary>
	public float SceneFrameDeltaSeconds;

	/// <summary><see cref="System.Diagnostics.Stopwatch"/> timestamp of the frame this view last recorded, or zero.</summary>
	public long LastSceneFrameTimestamp;

	// Volumetric fog froxel history. The grid is camera-frustum aligned, so it is per view by construction.
	public readonly ViewHistory<FogHistoryShape> FogHistory = new("Volumetric fog", 1, DescribeFogHistory);

	// Colour pyramid.
	public IGfxDevice? ColorPyramidDevice;
	public GraphicsBackendKind? ColorPyramidBackendKind;
	public Int2 ColorPyramidSize;
	public bool ColorPyramidValid;
	public IGfxTexture[] ColorPyramidTextures = Array.Empty<IGfxTexture>();
	public ResourceState[] ColorPyramidStates = Array.Empty<ResourceState>();

	// DDGI probe volume. One world backs one view, so the volume belongs to the view that renders it. The probe
	// activity texture and irradiance estimator are not ping-ponged, but share the history's lifetime.
	public readonly ViewHistory<DdgiHistoryShape> DdgiHistory = new("DDGI", DdgiHistorySlot.Count, DescribeDdgiHistory);
	public IGfxTexture? DdgiProbeActivityTexture;
	public IGfxBuffer? DdgiIrradianceEstimatorBuffer;
	public ResourceState DdgiProbeActivityState = ResourceState.Common;
	public ResourceState DdgiIrradianceEstimatorState = ResourceState.Common;
	public Vector3 DdgiCommittedRuntimeOrigin;
	public Int3 DdgiCommittedStorageOffset;
	public bool DdgiCommittedPlacementValid;
	public DdgiPassConfig CurrentDdgiConfig;
	public bool CurrentDdgiConfigValid;

	/// <summary>
	/// Ping-pong history of the DDGI final gather's SVGF denoiser at its trace resolution, one slot per
	/// <see cref="DdgiFinalGatherHistorySlot"/>.
	/// </summary>
	public readonly ViewHistory<Int2> FinalGatherHistory = new(
		"DDGI final gather",
		DdgiFinalGatherHistorySlot.Count,
		DescribeFinalGatherHistory);

	/// <summary>
	/// Ends the frame for every history: those this frame imported swap, the rest become invalid. Companion state a
	/// family keeps beside its history is committed by the caller first, while <see cref="ViewHistory.IsImported"/>
	/// still says whether this frame ran the effect.
	/// </summary>
	public void CompleteHistories(RenderGraphResourceRegistry resources)
	{
		TemporalHistory.Complete(resources);
		Fsr3History.Complete(resources);
		FogHistory.Complete(resources);
		DdgiHistory.Complete(resources);
		FinalGatherHistory.Complete(resources);
	}

	/// <summary>
	/// Marks screen-space history unusable because the frame's shape changed. DDGI is world-space and keeps its
	/// history across a resize.
	/// </summary>
	public void InvalidateScreenSpaceHistories()
	{
		TemporalHistory.Invalidate();
		Fsr3History.Invalidate();
		FogHistory.Invalidate();
		FinalGatherHistory.Invalidate();
	}

	/// <summary>
	/// Retires every GPU resource this view holds. Called when the view closes; each release goes through the
	/// device's retirement queue, so resources outlive any submission still referencing them.
	/// </summary>
	/// <param name="device">
	/// The device the view's output target was created on. The history resources carry their own device, but
	/// the target manager does not, and releasing its texture without a device would dispose it immediately —
	/// while a UI frame still referencing it may be in flight.
	/// </param>
	public void ReleaseAll(IGfxDevice? device)
	{
		ReleaseTemporalHistoryResources();
		ReleaseFogHistoryResources();
		ReleaseColorPyramidResources();
		ReleaseDdgiHistoryResources();
		ReleaseFinalGatherHistoryResources();
		SceneRenderTarget.Release(device);
		PresentedSceneTarget.Release(device);
		SceneRenderSize = Int2.Zero;
		PreviousJitterPhaseCount = 0;
		ResolvedProjection = Matrix4x4.Identity;
		PreviousResolvedProjection = Matrix4x4.Identity;
		HasPreviousResolvedProjection = false;
		ResolvedSceneViewportState = SceneViewportRenderState.Empty;
	}

	public void ReleaseFogHistoryResources()
	{
		FogHistory.Release();
	}

	public void ReleaseColorPyramidResources()
	{
		for (var level = 0; level < ColorPyramidTextures.Length; level++)
		{
			TemporalResourceRetirement.RetireTexture(ColorPyramidDevice, ColorPyramidTextures[level], ColorPyramidStates[level]);
		}

		ColorPyramidTextures = Array.Empty<IGfxTexture>();
		ColorPyramidStates = Array.Empty<ResourceState>();
		ColorPyramidDevice = null;
		ColorPyramidBackendKind = null;
		ColorPyramidSize = Int2.Zero;
		ColorPyramidValid = false;
	}

	public void ReleaseTemporalHistoryResources()
	{
		TemporalHistory.Release();
		ReleaseFsr3HistoryResources();
	}

	/// <summary>Releases FSR3's own history, for when temporal AA stays on but switches to another method.</summary>
	public void ReleaseFsr3HistoryResources()
	{
		var frameInfoDevice = Fsr3History.Device;
		Fsr3History.Release();

		if (Fsr3FrameInfoTexture is not null)
		{
			TemporalResourceRetirement.RetireTexture(frameInfoDevice, Fsr3FrameInfoTexture, Fsr3FrameInfoState);
			Fsr3FrameInfoTexture = null;
			Fsr3FrameInfoState = ResourceState.Common;
		}

		Fsr3FrameIndex = 0;
	}

	public void ReleaseFinalGatherHistoryResources()
	{
		FinalGatherHistory.Release();
	}

	public void ReleaseDdgiHistoryResources()
	{
		var device = DdgiHistory.Device;
		DdgiHistory.Release();
		ReleaseDdgiCompanionResources(device);
	}

	/// <summary>Releases the DDGI resources that share the history's lifetime but are not ping-ponged.</summary>
	public void ReleaseDdgiCompanionResources(IGfxDevice? device)
	{
		if (DdgiProbeActivityTexture is IGfxTexture activityTexture)
		{
			TemporalResourceRetirement.RetireTexture(device, activityTexture, DdgiProbeActivityState);
		}
		DdgiProbeActivityTexture = null;
		DdgiProbeActivityState = ResourceState.Common;

		if (DdgiIrradianceEstimatorBuffer is IGfxBuffer estimatorBuffer)
		{
			TemporalResourceRetirement.RetireBuffer(device, estimatorBuffer);
		}
		DdgiIrradianceEstimatorBuffer = null;
		DdgiIrradianceEstimatorState = ResourceState.Common;

		DdgiCommittedRuntimeOrigin = Vector3.Zero;
		DdgiCommittedStorageOffset = default;
		DdgiCommittedPlacementValid = false;
	}

	private static TextureDescriptor DescribeTemporalHistory(TemporalHistoryShape shape, int slot)
	{
		// Colour is cleared to opaque black. TAA stores view depth in the depth slot, zero for sky; FSR3 reuses it
		// as its luma history.
		var clearAlpha = slot == TemporalHistorySlot.Color ? 1.0f : 0.0f;

		return new TextureDescriptor(
			shape.Size.X,
			shape.Size.Y,
			TextureFormat.Rgba16Float,
			TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
			new ColorRGBA(0.0f, 0.0f, 0.0f, clearAlpha));
	}

	private static TextureDescriptor DescribeFsr3History(Int2 size, int slot)
	{
		return new TextureDescriptor(
			size.X,
			size.Y,
			TextureFormat.Rgba16Float,
			TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
			new ColorRGBA(0.0f, 0.0f, 0.0f, 0.0f));
	}

	private static TextureDescriptor DescribeFogHistory(FogHistoryShape shape, int slot)
	{
		return RenderGraphFrameBuilder.CreateFogTextureDescriptor(shape.Grid);
	}

	private static TextureDescriptor DescribeFinalGatherHistory(Int2 traceSize, int slot)
	{
		return new TextureDescriptor(
			traceSize.X,
			traceSize.Y,
			DdgiFinalGatherHistorySlot.GetFormat(slot),
			TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
			new ColorRGBA(0.0f, 0.0f, 0.0f, 0.0f));
	}

	private static TextureDescriptor DescribeDdgiHistory(DdgiHistoryShape shape, int slot)
	{
		if (slot == DdgiHistorySlot.Visibility)
		{
			var visibilityAtlasSize = DdgiUtilities.GetAtlasSize(shape.GridShape, shape.VisibilityTileInteriorSize);

			return new TextureDescriptor(
				visibilityAtlasSize.X,
				visibilityAtlasSize.Y,
				TextureFormat.Rgba16Float,
				TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
				new ColorRGBA(1.0f, 1.0f, 0.0f, 1.0f));
		}

		if (slot == DdgiHistorySlot.ProbeState)
		{
			return new TextureDescriptor(
				shape.GridShape.AtlasColumns,
				shape.GridShape.AtlasRows,
				TextureFormat.Rgba16Float,
				TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
				new ColorRGBA(0.0f, 0.0f, 0.0f, 0.0f));
		}

		var shCoefficientTextureSize = DdgiUtilities.GetShCoefficientTextureSize(shape.GridShape);

		return new TextureDescriptor(
			shCoefficientTextureSize.X,
			shCoefficientTextureSize.Y,
			TextureFormat.Rgba16Float,
			TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
			new ColorRGBA(0.0f, 0.0f, 0.0f, 0.0f));
	}
}

/// <summary>What temporal AA and FSR3 history is only valid against.</summary>
internal readonly record struct TemporalHistoryShape(Int2 Size, AntiAliasingMode Mode);

/// <summary>
/// What volumetric fog history is only valid against. The maximum distance does not change the grid, but it
/// rescales the slices, so last frame's froxels no longer line up.
/// </summary>
internal readonly record struct FogHistoryShape(Int3 Grid, float MaxDistance);

/// <summary>
/// What the DDGI probe volume is only valid against. The anchor and spacing do not change the textures, but they
/// move every probe, so last frame's irradiance belongs to other positions.
/// </summary>
internal readonly record struct DdgiHistoryShape(
	DdgiGridShape GridShape,
	Vector3 LatticeAnchor,
	float ProbeSpacing,
	int VisibilityTileInteriorSize);

/// <summary>The slots of <see cref="RenderViewState.TemporalHistory"/>.</summary>
internal static class TemporalHistorySlot
{
	public const int Color = 0;
	public const int Depth = 1;
	public const int Count = 2;
}

/// <summary>The slots of <see cref="RenderViewState.Fsr3History"/>.</summary>
internal static class Fsr3HistorySlot
{
	public const int CurrentLuma = 0;
	public const int Accumulation = 1;
	public const int Count = 2;
}

/// <summary>
/// The slots of <see cref="RenderViewState.DdgiHistory"/>: the spherical-harmonic irradiance coefficients in
/// L0, Ly, Lz, Lx order, then visibility and probe state.
/// </summary>
internal static class DdgiHistorySlot
{
	public const int Irradiance = 0;
	public const int Visibility = DdgiUtilities.ShCoefficientCount;
	public const int ProbeState = Visibility + 1;
	public const int Count = ProbeState + 1;
}

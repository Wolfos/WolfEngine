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
/// The fields are public because this is a state bag for one collaborator, not an abstraction. Allocation
/// still belongs to the frame builder, which knows the frame's config and sizes; teardown belongs here,
/// because knowing which resources exist and what state they were left in is exactly what this type holds.
/// </remarks>
internal sealed class RenderViewState
{
	private const int DdgiShCoefficientCount = DdgiUtilities.ShCoefficientCount;

	public RenderViewState(RenderViewId view)
	{
		View = view;
	}

	public RenderViewId View { get; }

	/// <summary>The world this view renders, or null for the primary view before anything bound one.</summary>
	public World? World;

	/// <summary>Short name, qualified into pass names so a crash log names the view a pass belonged to.</summary>
	public string Name = "primary";

	/// <summary>What this view resolved to for the UI to sample, as of the last frame it was recorded.</summary>
	public SceneViewportRenderState ResolvedSceneViewportState = SceneViewportRenderState.Empty;

	/// <summary>
	/// The texture this view is drawn into and handed to the UI. One per view: views size independently, and
	/// a shared target would mean the last view recorded overwrote what every earlier one had drawn.
	/// </summary>
	public readonly EditorSceneRenderTargetManager SceneRenderTarget = new();

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

	// Temporal anti-aliasing and FSR3 history.
	public AntiAliasingMode HistoryMode;
	public bool HistoryValid;
	public bool ResetTaaHistoryThisFrame;
	public IGfxDevice? HistoryDevice;
	public GraphicsBackendKind? HistoryBackendKind;
	public Int2 HistorySize;
	public int HistoryReadIndex;
	public readonly IGfxTexture?[] HistoryColorTextures = new IGfxTexture?[2];
	public readonly IGfxTexture?[] HistoryDepthTextures = new IGfxTexture?[2];
	public readonly ResourceState[] HistoryColorStates = new ResourceState[2];
	public readonly ResourceState[] HistoryDepthStates = new ResourceState[2];
	public readonly IGfxTexture?[] Fsr3CurrentLumaTextures = new IGfxTexture?[2];
	public readonly IGfxTexture?[] Fsr3AccumulationTextures = new IGfxTexture?[2];
	public readonly ResourceState[] Fsr3CurrentLumaStates = new ResourceState[2];
	public readonly ResourceState[] Fsr3AccumulationStates = new ResourceState[2];
	public IGfxTexture? Fsr3FrameInfoTexture;
	public ResourceState Fsr3FrameInfoState = ResourceState.UnorderedAccess;
	public uint Fsr3FrameIndex;

	// Volumetric fog froxel history. The grid is camera-frustum aligned, so it is per view by construction.
	public bool FogHistoryValid;
	public readonly IGfxTexture?[] FogHistoryTextures = new IGfxTexture?[2];
	public readonly ResourceState[] FogHistoryStates = new ResourceState[2];
	public IGfxDevice? FogHistoryDevice;
	public GraphicsBackendKind? FogHistoryBackendKind;
	public Int3 FogHistoryGrid;
	public float FogHistoryMaxDistance;
	public int FogHistoryReadIndex;

	// Colour pyramid.
	public IGfxDevice? ColorPyramidDevice;
	public GraphicsBackendKind? ColorPyramidBackendKind;
	public Int2 ColorPyramidSize;
	public bool ColorPyramidValid;
	public IGfxTexture[] ColorPyramidTextures = Array.Empty<IGfxTexture>();
	public ResourceState[] ColorPyramidStates = Array.Empty<ResourceState>();

	// DDGI probe volume. One world backs one view, so the volume belongs to the view that renders it.
	public bool DdgiHistoryValid;
	public IGfxDevice? DdgiHistoryDevice;
	public GraphicsBackendKind? DdgiHistoryBackendKind;
	public DdgiGridShape DdgiHistoryGridShape;
	public int DdgiHistoryReadIndex;
	public readonly IGfxTexture?[,] DdgiIrradianceTextures = new IGfxTexture?[DdgiShCoefficientCount, 2];
	public readonly IGfxTexture?[] DdgiVisibilityTextures = new IGfxTexture?[2];
	public readonly IGfxTexture?[] DdgiProbeStateTextures = new IGfxTexture?[2];
	public IGfxTexture? DdgiProbeActivityTexture;
	public IGfxBuffer? DdgiIrradianceEstimatorBuffer;
	public readonly ResourceState[,] DdgiIrradianceStates = new ResourceState[DdgiShCoefficientCount, 2];
	public readonly ResourceState[] DdgiVisibilityStates = new ResourceState[2];
	public readonly ResourceState[] DdgiProbeStateStates = new ResourceState[2];
	public ResourceState DdgiProbeActivityState = ResourceState.Common;
	public ResourceState DdgiIrradianceEstimatorState = ResourceState.Common;
	public Vector3 DdgiHistoryLatticeAnchor;
	public float DdgiHistoryProbeSpacing;
	public Vector3 DdgiCommittedRuntimeOrigin;
	public Int3 DdgiCommittedStorageOffset;
	public bool DdgiCommittedPlacementValid;
	public DdgiPassConfig CurrentDdgiConfig;
	public bool CurrentDdgiConfigValid;

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
		SceneRenderTarget.Release(device);
		SceneRenderSize = Int2.Zero;
		PreviousJitterPhaseCount = 0;
		ResolvedProjection = Matrix4x4.Identity;
		PreviousResolvedProjection = Matrix4x4.Identity;
		HasPreviousResolvedProjection = false;
		ResolvedSceneViewportState = SceneViewportRenderState.Empty;
	}

	public void ReleaseFogHistoryResources()
	{
		for (var i = 0; i < 2; i++)
		{
			if (FogHistoryTextures[i] is IGfxTexture texture)
			{
				EnqueueTemporalRelease(FogHistoryDevice, texture, FogHistoryStates[i]);
			}
			FogHistoryTextures[i] = null;
			FogHistoryStates[i] = ResourceState.Common;
		}
		FogHistoryDevice = null;
		FogHistoryBackendKind = null;
		FogHistoryGrid = default;
		FogHistoryMaxDistance = 0.0f;
		FogHistoryReadIndex = 0;
		FogHistoryValid = false;
	}

	public void ReleaseColorPyramidResources()
	{
		for (var level = 0; level < ColorPyramidTextures.Length; level++)
		{
			EnqueueTemporalRelease(ColorPyramidDevice, ColorPyramidTextures[level], ColorPyramidStates[level]);
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
		for (var i = 0; i < 2; i++)
		{
			if (HistoryColorTextures[i] is IGfxTexture colorTexture)
			{
				EnqueueTemporalRelease(HistoryDevice, colorTexture, HistoryColorStates[i]);
			}

			if (HistoryDepthTextures[i] is IGfxTexture depthTexture)
			{
				EnqueueTemporalRelease(HistoryDevice, depthTexture, HistoryDepthStates[i]);
			}
			if (Fsr3CurrentLumaTextures[i] is IGfxTexture currentLumaTexture)
			{
				EnqueueTemporalRelease(HistoryDevice, currentLumaTexture, Fsr3CurrentLumaStates[i]);
			}
			if (Fsr3AccumulationTextures[i] is IGfxTexture accumulationTexture)
			{
				EnqueueTemporalRelease(HistoryDevice, accumulationTexture, Fsr3AccumulationStates[i]);
			}

			HistoryColorTextures[i] = null;
			HistoryDepthTextures[i] = null;
			Fsr3CurrentLumaTextures[i] = null;
			Fsr3AccumulationTextures[i] = null;
			HistoryColorStates[i] = ResourceState.Common;
			HistoryDepthStates[i] = ResourceState.Common;
			Fsr3CurrentLumaStates[i] = ResourceState.Common;
			Fsr3AccumulationStates[i] = ResourceState.Common;
		}
		if (Fsr3FrameInfoTexture is not null)
		{
			EnqueueTemporalRelease(HistoryDevice, Fsr3FrameInfoTexture, Fsr3FrameInfoState);
			Fsr3FrameInfoTexture = null;
			Fsr3FrameInfoState = ResourceState.Common;
		}

		HistoryBackendKind = null;
		HistoryDevice = null;
		HistorySize = Int2.Zero;
		HistoryReadIndex = 0;
		HistoryValid = false;
		Fsr3FrameIndex = 0;
	}

	public void ReleaseDdgiHistoryResources()
	{
		for (var i = 0; i < 2; i++)
		{
			for (var coefficientIndex = 0; coefficientIndex < DdgiShCoefficientCount; coefficientIndex++)
			{
				if (DdgiIrradianceTextures[coefficientIndex, i] is IGfxTexture irradianceTexture)
				{
					EnqueueTemporalRelease(
						DdgiHistoryDevice,
						irradianceTexture,
						DdgiIrradianceStates[coefficientIndex, i]);
				}
				DdgiIrradianceTextures[coefficientIndex, i] = null;
				DdgiIrradianceStates[coefficientIndex, i] = ResourceState.Common;
			}

			if (DdgiVisibilityTextures[i] is IGfxTexture visibilityTexture)
			{
				EnqueueTemporalRelease(DdgiHistoryDevice, visibilityTexture, DdgiVisibilityStates[i]);
			}

			if (DdgiProbeStateTextures[i] is IGfxTexture probeStateTexture)
			{
				EnqueueTemporalRelease(DdgiHistoryDevice, probeStateTexture, DdgiProbeStateStates[i]);
			}

			DdgiVisibilityTextures[i] = null;
			DdgiProbeStateTextures[i] = null;
			DdgiVisibilityStates[i] = ResourceState.Common;
			DdgiProbeStateStates[i] = ResourceState.Common;
		}

		if (DdgiProbeActivityTexture is IGfxTexture activityTexture)
		{
			EnqueueTemporalRelease(DdgiHistoryDevice, activityTexture, DdgiProbeActivityState);
		}
		DdgiProbeActivityTexture = null;
		DdgiProbeActivityState = ResourceState.Common;

		if (DdgiIrradianceEstimatorBuffer is IGfxBuffer estimatorBuffer)
		{
			EnqueueTemporalBufferRelease(DdgiHistoryDevice, estimatorBuffer);
		}
		DdgiIrradianceEstimatorBuffer = null;
		DdgiIrradianceEstimatorState = ResourceState.Common;

		DdgiHistoryBackendKind = null;
		DdgiHistoryDevice = null;
		DdgiHistoryGridShape = default;
		DdgiHistoryLatticeAnchor = Vector3.Zero;
		DdgiHistoryProbeSpacing = 0.0f;
		DdgiHistoryReadIndex = 0;
		DdgiHistoryValid = false;
		DdgiCommittedRuntimeOrigin = Vector3.Zero;
		DdgiCommittedStorageOffset = default;
		DdgiCommittedPlacementValid = false;
	}

	private static void EnqueueTemporalRelease(IGfxDevice? device, IGfxTexture texture, ResourceState lastKnownState)
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

	private static void EnqueueTemporalBufferRelease(IGfxDevice? device, IGfxBuffer buffer)
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
}

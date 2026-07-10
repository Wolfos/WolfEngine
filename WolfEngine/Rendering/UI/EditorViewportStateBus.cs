using System;
using System.Numerics;
using WolfEngine.Mathematics;

namespace WolfEngine.Rendering.UI;

public static class SceneDebugViewIds
{
	public const string FinalColor = "final-color";
	public const string AmbientOcclusion = "ambient-occlusion";
	public const string RayTracingHitMask = "ray-tracing-hit-mask";
	public const string RayTracingHitDistance = "ray-tracing-hit-distance";
	public const string DdgiIrradiance = "ddgi-irradiance";
	public const string DdgiProbeBaseWeight = "ddgi-probe-base-weight";
	public const string DdgiWeightedVisibility = "ddgi-weighted-visibility";
	public const string DdgiDominantProbe = "ddgi-dominant-probe";
	public const string DdgiDominantProbeCoord = "ddgi-dominant-probe-coord";
	public const string DdgiProbeRelocation = "ddgi-probe-relocation";
	public const string DdgiProbeRelocationDecision = "ddgi-probe-relocation-decision";
	public const string DdgiVisibility = "ddgi-visibility";
	public const string DdgiFinalContribution = "ddgi-final-contribution";
	public const string GBufferAlbedo = "gbuffer-albedo";
	public const string GBufferNormal = "gbuffer-normal";
	public const string MotionVectors = "motion-vectors";
	public const string BloomPrefilter = "bloom-prefilter";
	public const string BloomContribution = "bloom-contribution";
}

public enum SceneDebugViewKind
{
	Color,
	Depth
}

public readonly struct SceneDebugViewOption
{
	public SceneDebugViewOption(string id, string label, SceneDebugViewKind kind)
	{
		Id = id ?? throw new ArgumentNullException(nameof(id));
		Label = label ?? throw new ArgumentNullException(nameof(label));
		Kind = kind;
	}

	public string Id { get; }
	public string Label { get; }
	public SceneDebugViewKind Kind { get; }
}

public readonly struct SceneViewportUiState
{
	public static readonly SceneViewportUiState Hidden = new(
		visible: false,
		contentSizePixels: Int2.Zero,
		resolutionScale: 1.0f,
		requestedDebugViewId: SceneDebugViewIds.FinalColor,
		hovered: false,
		focused: false,
		rightMousePressStartedHere: false,
		imageMin: Vector2.Zero,
		imageMax: Vector2.Zero);

	public SceneViewportUiState(
		bool visible,
		Int2 contentSizePixels,
		float resolutionScale,
		string requestedDebugViewId,
		bool hovered,
		bool focused,
		bool rightMousePressStartedHere,
		Vector2 imageMin,
		Vector2 imageMax)
	{
		Visible = visible;
		ContentSizePixels = contentSizePixels;
		ResolutionScale = resolutionScale;
		RequestedDebugViewId = string.IsNullOrWhiteSpace(requestedDebugViewId)
			? SceneDebugViewIds.FinalColor
			: requestedDebugViewId;
		Hovered = hovered;
		Focused = focused;
		RightMousePressStartedHere = rightMousePressStartedHere;
		ImageMin = imageMin;
		ImageMax = imageMax;
	}

	public bool Visible { get; }
	public Int2 ContentSizePixels { get; }
	public float ResolutionScale { get; }
	public string RequestedDebugViewId { get; }
	public bool Hovered { get; }
	public bool Focused { get; }
	public bool RightMousePressStartedHere { get; }
	public Vector2 ImageMin { get; }
	public Vector2 ImageMax { get; }
}

public readonly struct SceneViewportRenderState
{
	public static readonly SceneViewportRenderState Empty = new(
		textureId: 0,
		renderSizePixels: Int2.Zero,
		debugViews: Array.Empty<SceneDebugViewOption>(),
		activeDebugViewId: SceneDebugViewIds.FinalColor);

	public SceneViewportRenderState(
		nint textureId,
		Int2 renderSizePixels,
		SceneDebugViewOption[] debugViews,
		string activeDebugViewId)
	{
		TextureId = textureId;
		RenderSizePixels = renderSizePixels;
		DebugViews = debugViews ?? Array.Empty<SceneDebugViewOption>();
		ActiveDebugViewId = string.IsNullOrWhiteSpace(activeDebugViewId)
			? SceneDebugViewIds.FinalColor
			: activeDebugViewId;
	}

	public nint TextureId { get; }
	public Int2 RenderSizePixels { get; }
	public SceneDebugViewOption[] DebugViews { get; }
	public string ActiveDebugViewId { get; }
}

public sealed class EditorViewportStateBus
{
	private readonly object _sync = new();
	private SceneViewportUiState _uiState = SceneViewportUiState.Hidden;
	private SceneViewportRenderState _renderState = SceneViewportRenderState.Empty;
	private bool _gizmoDragging;

	public SceneViewportUiState GetUiState()
	{
		lock (_sync)
		{
			return _uiState;
		}
	}

	public void PublishUiState(SceneViewportUiState state)
	{
		lock (_sync)
		{
			_uiState = state;
		}
	}

	public SceneViewportRenderState GetRenderState()
	{
		lock (_sync)
		{
			return _renderState;
		}
	}

	public void PublishRenderState(SceneViewportRenderState state)
	{
		lock (_sync)
		{
			_renderState = state;
		}
	}

	public bool IsGizmoDragging()
	{
		lock (_sync)
		{
			return _gizmoDragging;
		}
	}

	public void PublishGizmoDragging(bool dragging)
	{
		lock (_sync)
		{
			_gizmoDragging = dragging;
		}
	}
}

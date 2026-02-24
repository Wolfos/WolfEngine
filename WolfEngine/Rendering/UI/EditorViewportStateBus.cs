using System.Numerics;
using WolfEngine.Mathematics;

namespace WolfEngine.Rendering.UI;

public readonly struct SceneViewportUiState
{
	public static readonly SceneViewportUiState Hidden = new(
		visible: false,
		contentSizePixels: Int2.Zero,
		resolutionScale: 1.0f,
		hovered: false,
		focused: false,
		imageMin: Vector2.Zero,
		imageMax: Vector2.Zero);

	public SceneViewportUiState(
		bool visible,
		Int2 contentSizePixels,
		float resolutionScale,
		bool hovered,
		bool focused,
		Vector2 imageMin,
		Vector2 imageMax)
	{
		Visible = visible;
		ContentSizePixels = contentSizePixels;
		ResolutionScale = resolutionScale;
		Hovered = hovered;
		Focused = focused;
		ImageMin = imageMin;
		ImageMax = imageMax;
	}

	public bool Visible { get; }
	public Int2 ContentSizePixels { get; }
	public float ResolutionScale { get; }
	public bool Hovered { get; }
	public bool Focused { get; }
	public Vector2 ImageMin { get; }
	public Vector2 ImageMax { get; }
}

public readonly struct SceneViewportRenderState
{
	public static readonly SceneViewportRenderState Empty = new(textureId: 0, renderSizePixels: Int2.Zero);

	public SceneViewportRenderState(nint textureId, Int2 renderSizePixels)
	{
		TextureId = textureId;
		RenderSizePixels = renderSizePixels;
	}

	public nint TextureId { get; }
	public Int2 RenderSizePixels { get; }
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

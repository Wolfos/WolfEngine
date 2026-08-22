using System.Numerics;
using ImGuiNET;
using WolfEngine.Rendering;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor;

public interface IGizmoLineRenderer
{
	void BeginFrame();
	void DrawLine(Vector3 startWorld, Vector3 endWorld, ColorRGBA color, float thickness = 2.0f);
}

public sealed class GizmoLineRenderer : IGizmoLineRenderer
{
	private readonly EditorViewportStateBus _viewportStateBus;
	private readonly EditorCameraContext _cameraContext;

	private ImDrawListPtr _drawList;
	private Matrix4x4 _viewProjection;
	private Vector2 _viewportMin;
	private Vector2 _viewportMax;
	private bool _canDraw;

	public GizmoLineRenderer(EditorViewportStateBus viewportStateBus, EditorCameraContext cameraContext)
	{
		_viewportStateBus = viewportStateBus ?? throw new ArgumentNullException(nameof(viewportStateBus));
		_cameraContext = cameraContext ?? throw new ArgumentNullException(nameof(cameraContext));
	}

	public void BeginFrame()
	{
		_canDraw = false;
		var viewportState = _viewportStateBus.GetUiState();
		if (viewportState.Visible == false ||
		    viewportState.ContentSizePixels.X <= 0 ||
		    viewportState.ContentSizePixels.Y <= 0 ||
		    viewportState.ImageMax.X <= viewportState.ImageMin.X ||
		    viewportState.ImageMax.Y <= viewportState.ImageMin.Y ||
		    _cameraContext.TryGet(out var camera, out var cameraWorldTransform) == false ||
		    camera.ScreenResolution.X <= 0 ||
		    camera.ScreenResolution.Y <= 0 ||
		    Matrix4x4.Invert(cameraWorldTransform.LocalToWorld, out var view) == false)
		{
			return;
		}

		_drawList = ImGui.GetWindowDrawList();
		_viewProjection = view * camera.Perspective;
		_viewportMin = viewportState.ImageMin;
		_viewportMax = viewportState.ImageMax;
		_canDraw = true;
	}

	public void DrawLine(Vector3 startWorld, Vector3 endWorld, ColorRGBA color, float thickness = 2.0f)
	{
		if (_canDraw == false ||
		    TryProjectToScreen(startWorld, out var startScreen) == false ||
		    TryProjectToScreen(endWorld, out var endScreen) == false)
		{
			return;
		}

		_drawList.AddLine(startScreen, endScreen, ImGui.ColorConvertFloat4ToU32(color), thickness);
	}

	private bool TryProjectToScreen(Vector3 worldPoint, out Vector2 screenPoint)
	{
		screenPoint = Vector2.Zero;
		var clip = Vector4.Transform(new Vector4(worldPoint, 1.0f), _viewProjection);
		if (clip.W <= 1e-6f)
		{
			return false;
		}

		var ndc = new Vector3(clip.X, clip.Y, clip.Z) / clip.W;
		if (ndc.Z < 0.0f || ndc.Z > 1.0f)
		{
			return false;
		}

		var width = _viewportMax.X - _viewportMin.X;
		var height = _viewportMax.Y - _viewportMin.Y;
		if (width <= 1e-5f || height <= 1e-5f)
		{
			return false;
		}

		var x = _viewportMin.X + (ndc.X * 0.5f + 0.5f) * width;
		var y = _viewportMin.Y + (1.0f - (ndc.Y * 0.5f + 0.5f)) * height;
		screenPoint = new Vector2(x, y);
		return float.IsFinite(x) && float.IsFinite(y);
	}
}

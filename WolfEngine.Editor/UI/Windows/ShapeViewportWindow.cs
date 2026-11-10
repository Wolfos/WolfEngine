using System.Numerics;
using ImGuiNET;
using WolfEngine.Mathematics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor.UI;

/// <summary>The Model workspace's viewport: the compiled shape, an orbit camera, picking and the transform gizmo.</summary>
public sealed class ShapeViewportWindow : EditorWindow
{
	private const float OrbitRadiansPerPixel = 0.008f;
	private const float ZoomPerWheelStep = 0.88f;
	private const float ClickDragThresholdPixels = 3.0f;
	private const float GridSpacing = 0.5f;
	private const int GridHalfLineCount = 8;

	private readonly ShapeEditorSession _session;
	private readonly EditorViewportStateBus _viewUi;
	private readonly IIconManager _icons;
	private readonly TransformGizmo _gizmo = new();
	private bool _showGrid = true;
	private TransformGizmoMode _gizmoMode = TransformGizmoMode.Translate;
	private TransformSpace _gizmoSpace = TransformSpace.Local;

	// Set while a left press started a gizmo drag, so releasing it does not also pick.
	private bool _gizmoOwnsPress;

	public ShapeViewportWindow(ShapeEditorSession session, EditorViewportStateBus viewUi, IIconManager icons)
	{
		_session = session;
		_viewUi = viewUi;
		_icons = icons;
	}

	public override string Name => "Shape Viewport";

	public override void OnHidden()
	{
		_gizmo.Cancel();
		_gizmoOwnsPress = false;
		_session.HidePreview();
	}

	public override void Draw(EditorScene scene)
	{
		Begin();
		if (ImGui.IsWindowDocked() && IsSelectedTab == false)
		{
			OnHidden();
			ImGui.End();
			return;
		}

		_session.Tick();
		ShapeEditorPanels.HandleFocus(_session);
		HandleShortcuts();
		ShapeEditorPanels.DrawDiagnostic(_session);

		if (_session.HasShape == false)
		{
			OnHidden();
			ImGui.TextDisabled("Open a shape from Assets, or create one with Create > Shape.");
			ImGui.End();
			return;
		}

		var preview = _session.EnsurePreview();
		if (preview is null)
		{
			ImGui.End();
			return;
		}

		DrawToolbar();

		var size = ImGui.GetContentRegionAvail();
		if (size.X < 1.0f || size.Y < 1.0f)
		{
			OnHidden();
			ImGui.End();
			return;
		}

		var imageMin = ImGui.GetCursorScreenPos();
		var imageMax = imageMin + size;
		ImGui.InvisibleButton(
			"##ShapeViewport",
			size,
			ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
		var hovered = ImGui.IsItemHovered();
		var active = ImGui.IsItemActive();

		// The view publishes no texture until the render graph has recorded it; sampling it before shows magenta.
		var renderState = _viewUi.GetRenderState(preview.View);
		var drawList = ImGui.GetWindowDrawList();
		if (renderState.TextureId != 0)
		{
			drawList.AddImage(UiTextureIds.Viewport(preview.View), imageMin, imageMax);
		}

		var framebufferScale = ImGui.GetIO().DisplayFramebufferScale;
		var viewportState = new SceneViewportUiState(
			visible: ImGui.IsWindowCollapsed() == false,
			contentSizePixels: new Int2((int)(size.X * framebufferScale.X), (int)(size.Y * framebufferScale.Y)),
			resolutionScale: 1.0f,
			requestedDebugViewId: SceneDebugViewIds.FinalColor,
			hovered: hovered,
			focused: ImGui.IsWindowFocused(),
			pointerAvailable: hovered,
			pointerCaptured: active,
			rightMousePressStartedHere: active && ImGui.IsMouseDown(ImGuiMouseButton.Right),
			imageMin: imageMin,
			imageMax: imageMax);
		_viewUi.PublishUiState(preview.View, viewportState);

		HandleCamera(preview, hovered, active, size);

		var viewProjection = renderState.ViewProjection;
		if (viewProjection.IsValid)
		{
			drawList.PushClipRect(imageMin, imageMax, true);
			if (_showGrid)
			{
				DrawGrid(drawList, viewProjection.WorldToClip, imageMin, imageMax);
			}

			UpdateGizmo(new TransformGizmoView(viewportState, viewProjection), viewportState);
			_gizmo.Draw(drawList);
			drawList.PopClipRect();
			HandlePicking(preview, hovered, imageMin, imageMax);
		}
		else
		{
			_gizmo.Cancel();
		}

		ImGui.End();
	}

	private void DrawToolbar()
	{
		if (_session.CanEdit)
		{
			if (TransformToolbar.DrawModeButtons(_icons, _gizmoMode) is { } clickedMode)
			{
				_gizmoMode = clickedMode;
			}

			ImGui.SameLine();
			TransformToolbar.DrawSpaceDropdown(ref _gizmoSpace);
			ImGui.SameLine();
		}

		ImGui.Checkbox("Grid", ref _showGrid);
		ImGui.SameLine();
		ImGui.TextDisabled(_session.IsCompiling ? "Compiling..." : string.Empty);
	}

	private void HandleShortcuts()
	{
		var io = ImGui.GetIO();
		if (ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) == false ||
		    io.WantTextInput ||
		    io.KeyCtrl ||
		    io.KeySuper ||
		    _gizmo.IsDragging)
		{
			return;
		}

		if (ImGui.IsKeyPressed(ImGuiKey.W, false))
		{
			_gizmoMode = TransformGizmoMode.Translate;
		}

		if (ImGui.IsKeyPressed(ImGuiKey.E, false))
		{
			_gizmoMode = TransformGizmoMode.Rotate;
		}

		if (ImGui.IsKeyPressed(ImGuiKey.R, false))
		{
			_gizmoMode = TransformGizmoMode.Scale;
		}

		if (ImGui.IsKeyPressed(ImGuiKey.F, false))
		{
			_session.FrameSelection();
		}
	}

	private void UpdateGizmo(in TransformGizmoView view, in SceneViewportUiState viewportState)
	{
		if (_session.CanEdit == false ||
		    _session.SelectedNodeId is not { } nodeId ||
		    _session.Document!.Tree.TryGetShapeMatrix(nodeId, out var matrix) == false)
		{
			_gizmo.Cancel();
			return;
		}

		var target = new ShapeNodeGizmoTarget(_session, nodeId);
		var options = new TransformGizmoOptions(_gizmoMode, _gizmoSpace, matrix.Translation, RotateAroundPivot: false);
		_gizmo.Update(target, view, TransformGizmoPointer.FromImGui(viewportState), options);
		if (_gizmo.IsDragging)
		{
			_gizmoOwnsPress = true;
		}
	}

	private void HandleCamera(ShapePreviewScene preview, bool hovered, bool active, Vector2 size)
	{
		var io = ImGui.GetIO();
		if (active && ImGui.IsMouseDown(ImGuiMouseButton.Right))
		{
			// Dragging right swings the camera left around the shape, so the shape turns with the pointer.
			preview.Orbit(-io.MouseDelta.X * OrbitRadiansPerPixel, io.MouseDelta.Y * OrbitRadiansPerPixel);
		}

		if (active && ImGui.IsMouseDown(ImGuiMouseButton.Middle))
		{
			preview.Pan(-io.MouseDelta.X / size.Y, io.MouseDelta.Y / size.Y);
		}

		if (hovered && io.MouseWheel != 0.0f)
		{
			preview.Zoom(MathF.Pow(ZoomPerWheelStep, io.MouseWheel));
		}
	}

	private void HandlePicking(ShapePreviewScene preview, bool hovered, Vector2 imageMin, Vector2 imageMax)
	{
		if (ImGui.IsMouseDown(ImGuiMouseButton.Left) == false && ImGui.IsMouseReleased(ImGuiMouseButton.Left) == false)
		{
			_gizmoOwnsPress = false;
		}

		if (_gizmoOwnsPress)
		{
			if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
			{
				_gizmoOwnsPress = false;
			}

			return;
		}

		if (hovered == false ||
		    ImGui.IsMouseReleased(ImGuiMouseButton.Left) == false ||
		    ImGui.GetMouseDragDelta(ImGuiMouseButton.Left, 0.0f).Length() > ClickDragThresholdPixels)
		{
			return;
		}

		var mouse = ImGui.GetIO().MousePos;
		var relative = (mouse - imageMin) / (imageMax - imageMin);
		var ndc = new Vector2(relative.X * 2.0f - 1.0f, 1.0f - relative.Y * 2.0f);
		if (preview.TryGetRay(ndc, out var origin, out var direction) == false)
		{
			return;
		}

		_session.Select(_session.Pick(origin, direction));
	}

	private static void DrawGrid(ImDrawListPtr drawList, Matrix4x4 worldToClip, Vector2 imageMin, Vector2 imageMax)
	{
		var color = ImGui.ColorConvertFloat4ToU32(new ColorRGBA(1.0f, 1.0f, 1.0f, 0.12f));
		var axisColor = ImGui.ColorConvertFloat4ToU32(new ColorRGBA(1.0f, 1.0f, 1.0f, 0.25f));
		var extent = GridSpacing * GridHalfLineCount;
		for (var i = -GridHalfLineCount; i <= GridHalfLineCount; i++)
		{
			var offset = i * GridSpacing;
			var lineColor = i == 0 ? axisColor : color;
			DrawWorldLine(drawList, new Vector3(offset, 0.0f, -extent), new Vector3(offset, 0.0f, extent), worldToClip, imageMin, imageMax, lineColor);
			DrawWorldLine(drawList, new Vector3(-extent, 0.0f, offset), new Vector3(extent, 0.0f, offset), worldToClip, imageMin, imageMax, lineColor);
		}
	}

	private static void DrawWorldLine(
		ImDrawListPtr drawList,
		Vector3 start,
		Vector3 end,
		Matrix4x4 worldToClip,
		Vector2 imageMin,
		Vector2 imageMax,
		uint color)
	{
		if (TransformGizmo.TryProjectToScreen(start, worldToClip, imageMin, imageMax, out var startScreen) &&
		    TransformGizmo.TryProjectToScreen(end, worldToClip, imageMin, imageMax, out var endScreen))
		{
			drawList.AddLine(startScreen, endScreen, color, 1.0f);
		}
	}
}

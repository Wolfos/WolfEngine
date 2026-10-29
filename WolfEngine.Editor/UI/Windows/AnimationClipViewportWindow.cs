using System.Numerics;
using ImGuiNET;
using WolfEngine.Mathematics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor.UI;

/// <summary>The clip editor's preview: the posed model with its bone overlay, orbit camera and bone picking.</summary>
public sealed class AnimationClipViewportWindow : EditorWindow
{
	private const float OrbitRadiansPerPixel = 0.008f;
	private const float ZoomPerWheelStep = 0.88f;
	private const float ClickDragThresholdPixels = 3.0f;
	private const float GridSpacing = 0.5f;
	private const int GridHalfLineCount = 8;

	private readonly AnimationClipEditorSession _session;
	private readonly EditorViewportStateBus _viewUi;
	private readonly IIconManager _icons;
	private readonly BoneOverlay _boneOverlay = new();
	private readonly List<Vector3> _jointWorld = new();
	private readonly TransformGizmo _gizmo = new();
	private bool _showBones = true;
	private bool _showGrid = true;
	private TransformGizmoMode _gizmoMode = TransformGizmoMode.Rotate;
	private TransformSpace _gizmoSpace = TransformSpace.Local;

	// Set while a left press started a gizmo drag, so releasing it does not also pick a bone.
	private bool _gizmoOwnsPress;

	public AnimationClipViewportWindow(AnimationClipEditorSession session, EditorViewportStateBus viewUi, IIconManager icons)
	{
		_icons = icons;
		_session = session;
		_viewUi = viewUi;
	}

	public override string Name => "Clip Viewport";

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
		AnimationClipEditorPanels.HandleFocusAndShortcuts(_session);
		HandleGizmoShortcuts();
		AnimationClipEditorPanels.DrawDiagnostic(_session);

		if (_session.HasClip == false)
		{
			OnHidden();
			ImGui.TextDisabled("Select an animation clip in Assets, or use Open in Animate.");
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
			"##ClipViewport",
			size,
			ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
		var hovered = ImGui.IsItemHovered();
		var active = ImGui.IsItemActive();

		var drawList = ImGui.GetWindowDrawList();
		drawList.AddImage(UiTextureIds.Viewport(preview.View), imageMin, imageMax);

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

		var viewProjection = _viewUi.GetRenderState(preview.View).ViewProjection;
		if (viewProjection.IsValid)
		{
			drawList.PushClipRect(imageMin, imageMax, true);
			if (_showGrid)
			{
				DrawGrid(drawList, viewProjection.WorldToClip, imageMin, imageMax);
			}

			ProjectBones(preview, viewProjection.WorldToClip, imageMin, imageMax);
			if (_showBones)
			{
				_boneOverlay.Draw(drawList, preview.Skeleton.ParentIndices, GetSelectedBoneIndex(preview));
			}

			UpdateGizmo(preview, new TransformGizmoView(viewportState, viewProjection), viewportState);
			_gizmo.Draw(drawList);
			drawList.PopClipRect();
			HandlePicking(preview, hovered);
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
			var autoKey = _session.AutoKey;
			if (ImGui.Checkbox("Auto Key", ref autoKey))
			{
				_session.AutoKey = autoKey;
			}

			ImGui.SameLine();
		}

		ImGui.Checkbox("Bones", ref _showBones);
		ImGui.SameLine();
		ImGui.Checkbox("Grid", ref _showGrid);
		// ImGui.SameLine();
		// ImGui.TextDisabled("RMB orbit, MMB pan, wheel zoom, F frame, W/E/R tools, K key");
	}

	private void HandleGizmoShortcuts()
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
	}

	private void UpdateGizmo(AnimationClipPreviewScene preview, in TransformGizmoView view, in SceneViewportUiState viewportState)
	{
		var boneIndex = GetSelectedBoneIndex(preview);
		if (_session.CanEdit == false || _session.IsPlaying || boneIndex < 0)
		{
			_gizmo.Cancel();
			return;
		}

		var bone = preview.Skeleton.BoneNames[boneIndex];
		var target = new BoneGizmoTarget(preview, _session, bone, boneIndex);
		var options = new TransformGizmoOptions(
			_gizmoMode,
			_gizmoSpace,
			preview.GetBoneWorldMatrix(boneIndex).Translation,
			RotateAroundPivot: false);

		_gizmo.Update(target, view, TransformGizmoPointer.FromImGui(viewportState), options);
		if (_gizmo.IsDragging)
		{
			_gizmoOwnsPress = true;
		}
	}

	private void HandleCamera(AnimationClipPreviewScene preview, bool hovered, bool active, Vector2 size)
	{
		var io = ImGui.GetIO();
		if (active && ImGui.IsMouseDown(ImGuiMouseButton.Right))
		{
			// Dragging right swings the camera left around the model, so the model turns with the pointer.
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

		if (ImGui.IsWindowFocused() &&
		    io.WantTextInput == false &&
		    ImGui.IsKeyPressed(ImGuiKey.F, false))
		{
			FrameSelection(preview);
		}
	}

	private void FrameSelection(AnimationClipPreviewScene preview)
	{
		var bone = GetSelectedBoneIndex(preview);
		if (bone < 0)
		{
			preview.FrameAll();
			return;
		}

		var position = preview.GetBoneWorldMatrix(bone).Translation;
		var parentPosition = preview.GetBoneParentWorldMatrix(bone).Translation;
		var radius = MathF.Max(0.15f, Vector3.Distance(position, parentPosition) * 2.0f);
		preview.Frame(position, radius);
	}

	private void ProjectBones(AnimationClipPreviewScene preview, Matrix4x4 worldToClip, Vector2 imageMin, Vector2 imageMax)
	{
		_jointWorld.Clear();
		for (var i = 0; i < preview.Skeleton.BoneCount; i++)
		{
			_jointWorld.Add(preview.GetBoneWorldMatrix(i).Translation);
		}

		_boneOverlay.Project(_jointWorld, worldToClip, imageMin, imageMax);
	}

	private void HandlePicking(AnimationClipPreviewScene preview, bool hovered)
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

		if (_showBones == false ||
		    hovered == false ||
		    ImGui.IsMouseReleased(ImGuiMouseButton.Left) == false ||
		    ImGui.GetMouseDragDelta(ImGuiMouseButton.Left, 0.0f).Length() > ClickDragThresholdPixels)
		{
			return;
		}

		var picked = _boneOverlay.Pick(ImGui.GetIO().MousePos, preview.Skeleton.ParentIndices);
		_session.SelectBone(picked >= 0 ? preview.Skeleton.BoneNames[picked] : null);
	}

	private int GetSelectedBoneIndex(AnimationClipPreviewScene preview)
	{
		if (_session.SelectedBone is { } bone && preview.Skeleton.TryGetBoneIndex(bone, out var index))
		{
			return index;
		}

		return -1;
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

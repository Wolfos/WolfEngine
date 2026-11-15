using System.Numerics;
using ImGuiNET;
using WolfEngine.Animation;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.UI;

/// <summary>Transport and dopesheet for the open clip.</summary>
public sealed class AnimationClipTimelineWindow : EditorWindow
{
	private const float NameColumnWidth = 200.0f;
	private const float IndentWidth = 12.0f;
	private const float KeyAreaMargin = 10.0f;
	private const float DiamondRadius = 5.0f;
	private const float DiamondHitRadius = 7.0f;
	private const float DragThresholdPixels = 3.0f;
	private const float MinPixelsPerFrame = 1.0f;
	private const float MaxPixelsPerFrame = 80.0f;
	private const float ZoomPerWheelStep = 1.15f;
	private const float MinLabelSpacingPixels = 48.0f;

	private static readonly int[] TickSteps = [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1200];

	private static readonly uint RowHighlightColor = Pack(new ColorRGBA(0.26f, 0.42f, 0.66f, 0.35f));
	private static readonly uint RowStripeColor = Pack(new ColorRGBA(1.0f, 1.0f, 1.0f, 0.03f));
	private static readonly uint KeyColor = Pack(new ColorRGBA(0.62f, 0.76f, 0.95f, 1.0f));
	private static readonly uint SummaryKeyColor = Pack(new ColorRGBA(0.75f, 0.75f, 0.78f, 1.0f));
	private static readonly uint SelectedKeyColor = Pack(new ColorRGBA(1.0f, 0.78f, 0.25f, 1.0f));
	private static readonly uint GhostKeyColor = Pack(new ColorRGBA(1.0f, 0.78f, 0.25f, 0.45f));
	private static readonly uint KeyOutlineColor = Pack(new ColorRGBA(0.08f, 0.08f, 0.1f, 1.0f));
	private static readonly uint PlayheadColor = Pack(new ColorRGBA(0.95f, 0.95f, 0.95f, 0.9f));
	private static readonly uint TickColor = Pack(new ColorRGBA(1.0f, 1.0f, 1.0f, 0.25f));
	private static readonly uint GridColor = Pack(new ColorRGBA(1.0f, 1.0f, 1.0f, 0.06f));
	private static readonly uint ClipEndColor = Pack(new ColorRGBA(1.0f, 1.0f, 1.0f, 0.18f));
	private static readonly uint BoxFillColor = Pack(new ColorRGBA(0.4f, 0.6f, 1.0f, 0.15f));
	private static readonly uint BoxBorderColor = Pack(new ColorRGBA(0.4f, 0.6f, 1.0f, 0.8f));

	private readonly AnimationClipEditorSession _session;
	private readonly DopesheetModel _model = new();
	private readonly HashSet<string> _collapsedBones = new(StringComparer.Ordinal);
	private bool _keyedOnly;
	private int _collapsedVersion;
	private ModelKey _modelKey;
	private DopesheetView _view;
	private bool _fitView = true;
	private PointerGesture _gesture;

	public AnimationClipTimelineWindow(AnimationClipEditorSession session)
	{
		_session = session;
	}

	public override string Name => "Timeline";

	public override void Draw(EditorScene scene)
	{
		Begin();
		_session.Tick();
		AnimationClipEditorPanels.HandleFocusAndShortcuts(_session);

		if (_session.HasClip == false)
		{
			_gesture = default;
			ImGui.TextDisabled("No clip open.");
			ImGui.End();
			return;
		}

		DrawTransport();
		DrawDopesheet();
		ImGui.End();
	}

	private void DrawTransport()
	{
		var length = _session.LengthFrames;
		var currentFrame = _session.CurrentFrame;

		if (ImGui.Button("|<"))
		{
			_session.SetPlayhead(0);
		}

		ImGui.SameLine();
		if (ImGui.Button("<"))
		{
			_session.SetPlayhead(currentFrame - 1);
		}

		ImGui.SameLine();
		if (ImGui.Button(_session.IsPlaying ? "Pause" : "Play"))
		{
			_session.TogglePlayback();
		}

		ImGui.SameLine();
		if (ImGui.Button("Stop"))
		{
			_session.Stop();
		}

		ImGui.SameLine();
		if (ImGui.Button(">"))
		{
			_session.SetPlayhead(currentFrame + 1);
		}

		ImGui.SameLine();
		if (ImGui.Button(">|"))
		{
			_session.SetPlayhead(length);
		}

		ImGui.SameLine();
		ImGui.TextUnformatted(
			$"{currentFrame} / {length}   " +
			$"{AnimationClipEditorPanels.FormatTime(_session.PlayheadFrame, _session.FramesPerSecond)} / " +
			$"{AnimationClipEditorPanels.FormatTime(length, _session.FramesPerSecond)}   " +
			$"{_session.FramesPerSecond} FPS");

		var speed = _session.PlaybackSpeed;
		ImGui.SetNextItemWidth(120.0f);
		if (ImGui.SliderFloat("Speed", ref speed, 0.1f, 2.0f, "%.2fx"))
		{
			_session.PlaybackSpeed = speed;
		}

		ImGui.SameLine();
		if (ImGui.Checkbox("Keyed only", ref _keyedOnly))
		{
			_modelKey = default;
		}

		ImGui.SameLine();
		if (ImGui.Button("Fit"))
		{
			_fitView = true;
		}

		if (_session.CanEdit == false)
		{
			return;
		}

		ImGui.SameLine();
		var loop = _session.Loop;
		ImGui.BeginDisabled(_session.IsEditing);
		if (ImGui.Checkbox("Loop", ref loop))
		{
			_session.SetLoop(loop);
		}

		ImGui.EndDisabled();

		ImGui.SameLine();
		var autoKey = _session.AutoKey;
		if (ImGui.Checkbox("Auto Key", ref autoKey))
		{
			_session.AutoKey = autoKey;
		}

		ImGui.SameLine();
		ImGui.BeginDisabled(_session.SelectedBone is null || _session.IsPlaying);
		if (ImGui.Button("Set Key"))
		{
			_session.SetKeyOnSelectedBone();
		}

		ImGui.EndDisabled();
	}

	private void DrawDopesheet()
	{
		var skeleton = _session.Skeleton;
		if (skeleton is null)
		{
			return;
		}

		RebuildModelIfNeeded(skeleton);

		var origin = ImGui.GetCursorScreenPos();
		var available = ImGui.GetContentRegionAvail();
		var keyAreaLeft = origin.X + NameColumnWidth;
		var keyAreaWidth = available.X - NameColumnWidth;
		if (keyAreaWidth < 20.0f || available.Y < 20.0f)
		{
			return;
		}

		if (_fitView)
		{
			_view = DopesheetView.Fit(keyAreaLeft, keyAreaWidth, _session.LengthFrames, KeyAreaMargin);
		}
		else
		{
			_view = _view with { KeyAreaLeft = keyAreaLeft };
		}

		var rowHeight = ImGui.GetFrameHeight();
		DrawRuler(origin, available.X, keyAreaLeft, keyAreaWidth, rowHeight);
		DrawRows(keyAreaLeft, keyAreaWidth, rowHeight);
	}

	private void DrawRuler(Vector2 origin, float width, float keyAreaLeft, float keyAreaWidth, float height)
	{
		ImGui.InvisibleButton("##Ruler", new Vector2(width, height));
		var drawList = ImGui.GetWindowDrawList();
		var keyAreaRight = keyAreaLeft + keyAreaWidth;
		var bottom = origin.Y + height;

		drawList.PushClipRect(new Vector2(keyAreaLeft, origin.Y), new Vector2(keyAreaRight, bottom), true);
		var step = ChooseTickStep();
		var first = Math.Max(0, (int)MathF.Floor(_view.XToFrame(keyAreaLeft) / step) * step);
		var last = Math.Min(_session.LengthFrames, (int)MathF.Ceiling(_view.XToFrame(keyAreaRight)));
		for (var frame = first; frame <= last; frame += step)
		{
			var x = _view.FrameToX(frame);
			drawList.AddLine(new Vector2(x, bottom - 6.0f), new Vector2(x, bottom), TickColor);
			drawList.AddText(new Vector2(x + 2.0f, origin.Y + 1.0f), TickColor, frame.ToString());
		}

		DrawPlayheadLine(drawList, origin.Y, bottom);
		drawList.PopClipRect();

		// Clicking or dragging on the ruler scrubs.
		if (ImGui.IsItemActive() && ImGui.GetMousePos().X >= keyAreaLeft - KeyAreaMargin)
		{
			_session.SetPlayhead(_view.XToNearestFrame(ImGui.GetMousePos().X));
		}

		if (ImGui.IsItemHovered())
		{
			HandleZoomAndPan(keyAreaLeft);
		}
	}

	private void DrawRows(float keyAreaLeft, float keyAreaWidth, float rowHeight)
	{
		// The wheel zooms or pans with a modifier held, so it must not also scroll the rows then.
		var io = ImGui.GetIO();
		var windowFlags = io.KeyCtrl || io.KeySuper || io.KeyShift
			? ImGuiWindowFlags.NoScrollWithMouse
			: ImGuiWindowFlags.None;
		if (ImGui.BeginChild("##DopesheetRows", Vector2.Zero, ImGuiChildFlags.None, windowFlags) == false)
		{
			ImGui.EndChild();
			return;
		}

		var drawList = ImGui.GetWindowDrawList();
		var rows = _model.Rows;
		var rowsTop = ImGui.GetCursorScreenPos().Y;
		var nameLeft = ImGui.GetCursorScreenPos().X;
		var keyAreaRight = keyAreaLeft + keyAreaWidth;
		var visibleTop = ImGui.GetWindowPos().Y;
		var visibleBottom = visibleTop + ImGui.GetWindowSize().Y;
		var selectedKeys = _session.SelectedKeys;
		var dragOffset = GetKeyDragOffset();

		for (var i = 0; i < rows.Count; i++)
		{
			var top = rowsTop + i * rowHeight;
			ImGui.SetCursorScreenPos(new Vector2(nameLeft, top));
			if (top + rowHeight < visibleTop || top > visibleBottom)
			{
				ImGui.Dummy(new Vector2(1.0f, rowHeight));
				continue;
			}

			DrawRowBackground(drawList, rows[i], i, nameLeft, keyAreaRight, top, rowHeight);
			DrawRowName(rows[i], i, nameLeft, top, rowHeight);
		}

		var rowsHeight = rows.Count * rowHeight;
		ImGui.SetCursorScreenPos(new Vector2(keyAreaLeft, rowsTop));
		ImGui.InvisibleButton("##Keys", new Vector2(keyAreaWidth, MathF.Max(rowsHeight, 1.0f)));
		var keysHovered = ImGui.IsItemHovered();
		var keysActive = ImGui.IsItemActive();

		drawList.PushClipRect(new Vector2(keyAreaLeft, visibleTop), new Vector2(keyAreaRight, visibleBottom), true);
		DrawFrameGrid(drawList, keyAreaLeft, keyAreaRight, rowsTop, rowsTop + rowsHeight);
		for (var i = 0; i < rows.Count; i++)
		{
			var centerY = rowsTop + i * rowHeight + rowHeight * 0.5f;
			if (centerY + rowHeight < visibleTop || centerY - rowHeight > visibleBottom)
			{
				continue;
			}

			DrawRowKeys(drawList, rows[i], i, centerY, selectedKeys, dragOffset);
		}

		DrawPlayheadLine(drawList, rowsTop, MathF.Max(rowsTop + rowsHeight, visibleBottom));
		HandleKeyPointer(keysHovered, keysActive, rowsTop, rowHeight);
		DrawBoxSelection(drawList);
		drawList.PopClipRect();

		if (keysHovered)
		{
			HandleZoomAndPan(keyAreaLeft);
		}

		ImGui.EndChild();
	}

	private void DrawRowBackground(ImDrawListPtr drawList, DopesheetRow row, int index, float left, float right, float top, float height)
	{
		var min = new Vector2(left, top);
		var max = new Vector2(right, top + height);
		if (row.Kind == DopesheetRowKind.Bone &&
		    string.Equals(row.Name, _session.SelectedBone, StringComparison.Ordinal))
		{
			drawList.AddRectFilled(min, max, RowHighlightColor);
		}
		else if (index % 2 == 1)
		{
			drawList.AddRectFilled(min, max, RowStripeColor);
		}
	}

	private void DrawRowName(DopesheetRow row, int index, float left, float top, float height)
	{
		var drawList = ImGui.GetWindowDrawList();
		var textColor = ImGui.GetColorU32(row.Kind == DopesheetRowKind.Summary ? ImGuiCol.TextDisabled : ImGuiCol.Text);
		var x = left + row.Depth * IndentWidth;

		if (row.HasChildren)
		{
			ImGui.SetCursorScreenPos(new Vector2(x, top));
			if (ImGui.InvisibleButton($"##Fold{index}", new Vector2(height, height)))
			{
				ToggleCollapsed(row.Name);
			}

			var center = new Vector2(x + height * 0.5f, top + height * 0.5f);
			var size = height * 0.22f;
			if (row.IsExpanded)
			{
				drawList.AddTriangleFilled(
					center + new Vector2(-size, -size * 0.6f),
					center + new Vector2(size, -size * 0.6f),
					center + new Vector2(0.0f, size * 0.8f),
					textColor);
			}
			else
			{
				drawList.AddTriangleFilled(
					center + new Vector2(-size * 0.6f, -size),
					center + new Vector2(-size * 0.6f, size),
					center + new Vector2(size * 0.8f, 0.0f),
					textColor);
			}
		}

		var labelLeft = x + height;
		var labelWidth = MathF.Max(1.0f, left + NameColumnWidth - labelLeft - 4.0f);
		ImGui.SetCursorScreenPos(new Vector2(labelLeft, top));
		if (ImGui.InvisibleButton($"##Name{index}", new Vector2(labelWidth, height)) &&
		    row.Kind == DopesheetRowKind.Bone)
		{
			_session.SelectBone(row.Name);
		}

		drawList.PushClipRect(new Vector2(labelLeft, top), new Vector2(labelLeft + labelWidth, top + height), true);
		drawList.AddText(new Vector2(labelLeft, top + (height - ImGui.GetTextLineHeight()) * 0.5f), textColor, row.Name);
		drawList.PopClipRect();
	}

	private void DrawRowKeys(
		ImDrawListPtr drawList,
		DopesheetRow row,
		int rowIndex,
		float centerY,
		IReadOnlySet<AnimationKeyRef> selectedKeys,
		int dragOffset)
	{
		var baseColor = row.Kind == DopesheetRowKind.Summary ? SummaryKeyColor : KeyColor;
		foreach (var frame in _model.GetRowFrames(rowIndex))
		{
			var selected = _model.IsRowKeySelected(rowIndex, frame, selectedKeys);
			var x = _view.FrameToX(frame);
			DrawDiamond(drawList, new Vector2(x, centerY), selected ? SelectedKeyColor : baseColor);

			if (selected && dragOffset != 0)
			{
				DrawDiamond(drawList, new Vector2(_view.FrameToX(frame + dragOffset), centerY), GhostKeyColor);
			}
		}
	}

	private static void DrawDiamond(ImDrawListPtr drawList, Vector2 center, uint color)
	{
		var top = center + new Vector2(0.0f, -DiamondRadius);
		var right = center + new Vector2(DiamondRadius, 0.0f);
		var bottom = center + new Vector2(0.0f, DiamondRadius);
		var left = center + new Vector2(-DiamondRadius, 0.0f);
		drawList.AddQuadFilled(top, right, bottom, left, color);
		drawList.AddQuad(top, right, bottom, left, KeyOutlineColor, 1.0f);
	}

	private void DrawFrameGrid(ImDrawListPtr drawList, float left, float right, float top, float bottom)
	{
		var step = ChooseTickStep();
		var first = Math.Max(0, (int)MathF.Floor(_view.XToFrame(left) / step) * step);
		var last = Math.Min(_session.LengthFrames, (int)MathF.Ceiling(_view.XToFrame(right)));
		for (var frame = first; frame <= last; frame += step)
		{
			var x = _view.FrameToX(frame);
			drawList.AddLine(new Vector2(x, top), new Vector2(x, bottom), GridColor);
		}

		var endX = _view.FrameToX(_session.LengthFrames);
		drawList.AddLine(new Vector2(endX, top), new Vector2(endX, bottom), ClipEndColor, 2.0f);
	}

	private void DrawPlayheadLine(ImDrawListPtr drawList, float top, float bottom)
	{
		var x = _view.FrameToX(_session.PlayheadFrame);
		drawList.AddLine(new Vector2(x, top), new Vector2(x, bottom), PlayheadColor, 1.5f);
	}

	/// <summary>
	/// Press on a diamond to select it and drag to move the selection (hold Ctrl or Cmd to copy
	/// instead). Press on empty space and drag to box-select. Shift adds, Ctrl or Cmd toggles.
	/// </summary>
	private void HandleKeyPointer(bool hovered, bool active, float rowsTop, float rowHeight)
	{
		var io = ImGui.GetIO();
		var mouse = ImGui.GetMousePos();
		var primaryModifier = io.KeyCtrl || io.KeySuper;

		if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
		{
			BeginGesture(mouse, rowsTop, rowHeight, io.KeyShift, primaryModifier);
		}

		if (_gesture.Kind == GestureKind.None)
		{
			return;
		}

		if (Vector2.Distance(mouse, _gesture.Start) > DragThresholdPixels)
		{
			_gesture = _gesture with { Dragged = true };
		}

		if (active && ImGui.IsMouseDown(ImGuiMouseButton.Left))
		{
			return;
		}

		EndGesture(mouse, rowsTop, rowHeight, io.KeyShift, primaryModifier);
		_gesture = default;
	}

	private void BeginGesture(Vector2 mouse, float rowsTop, float rowHeight, bool shift, bool primaryModifier)
	{
		var row = (int)MathF.Floor((mouse.Y - rowsTop) / rowHeight);
		if (TryHitKey(row, mouse.X, out var frame))
		{
			var keys = _model.GetRowKeysAt(row, frame);
			var rowEntry = _model.Rows[row];
			if (rowEntry.Kind == DopesheetRowKind.Bone)
			{
				_session.SelectBone(rowEntry.Name);
			}

			var wasSelected = _model.IsRowKeySelected(row, frame, _session.SelectedKeys);
			var addedOnPress = false;
			if (shift || (primaryModifier && wasSelected == false))
			{
				// Added now so a Ctrl or Cmd drag copies this key, not just whatever was selected before.
				_session.SelectKeys(keys, KeySelectionMode.Add);
				addedOnPress = wasSelected == false;
			}
			else if (primaryModifier == false && wasSelected == false)
			{
				_session.SelectKeys(keys, KeySelectionMode.Replace);
			}

			_gesture = new PointerGesture(GestureKind.Key, mouse, false, row, frame, addedOnPress);
			return;
		}

		_gesture = new PointerGesture(GestureKind.Box, mouse, false, row, 0, false);
	}

	private void EndGesture(Vector2 mouse, float rowsTop, float rowHeight, bool shift, bool primaryModifier)
	{
		if (_gesture.Kind == GestureKind.Key)
		{
			if (_gesture.Dragged)
			{
				ApplyKeyDrag(primaryModifier);
				return;
			}

			var keys = _model.GetRowKeysAt(_gesture.Row, _gesture.Frame);
			if (primaryModifier)
			{
				if (_gesture.AddedOnPress == false)
				{
					_session.SelectKeys(keys, KeySelectionMode.Toggle);
				}
			}
			else if (shift == false)
			{
				// A plain click on a key that was already part of a larger selection narrows it to that key.
				_session.SelectKeys(keys, KeySelectionMode.Replace);
			}

			return;
		}

		if (_gesture.Dragged == false)
		{
			if (shift == false && primaryModifier == false)
			{
				_session.ClearKeySelection();
			}

			return;
		}

		var firstRow = (int)MathF.Floor((MathF.Min(_gesture.Start.Y, mouse.Y) - rowsTop) / rowHeight);
		var lastRow = (int)MathF.Floor((MathF.Max(_gesture.Start.Y, mouse.Y) - rowsTop) / rowHeight);
		var firstFrame = (int)MathF.Ceiling(_view.XToFrame(MathF.Min(_gesture.Start.X, mouse.X)));
		var lastFrame = (int)MathF.Floor(_view.XToFrame(MathF.Max(_gesture.Start.X, mouse.X)));
		var boxed = _model.GetKeysInBox(firstRow, lastRow, firstFrame, lastFrame);
		_session.SelectKeys(boxed, shift || primaryModifier ? KeySelectionMode.Add : KeySelectionMode.Replace);
	}

	private void ApplyKeyDrag(bool duplicate)
	{
		var offset = GetKeyDragOffset();
		if (offset == 0)
		{
			return;
		}

		if (duplicate)
		{
			_session.DuplicateSelectedKeys(offset);
		}
		else
		{
			_session.MoveSelectedKeys(offset);
		}
	}

	/// <summary>Frames the selection would move by if the current key drag were released now.</summary>
	private int GetKeyDragOffset()
	{
		if (_gesture.Kind != GestureKind.Key || _gesture.Dragged == false || _session.CanEdit == false)
		{
			return 0;
		}

		var frames = (int)MathF.Round((ImGui.GetMousePos().X - _gesture.Start.X) / _view.PixelsPerFrame);
		return _session.ClampSelectionOffset(frames);
	}

	private void DrawBoxSelection(ImDrawListPtr drawList)
	{
		if (_gesture.Kind != GestureKind.Box || _gesture.Dragged == false)
		{
			return;
		}

		var mouse = ImGui.GetMousePos();
		var min = Vector2.Min(_gesture.Start, mouse);
		var max = Vector2.Max(_gesture.Start, mouse);
		drawList.AddRectFilled(min, max, BoxFillColor);
		drawList.AddRect(min, max, BoxBorderColor);
	}

	private bool TryHitKey(int row, float mouseX, out int frame)
	{
		frame = 0;
		if (row < 0 || row >= _model.Rows.Count)
		{
			return false;
		}

		var bestDistance = DiamondHitRadius;
		var found = false;
		foreach (var candidate in _model.GetRowFrames(row))
		{
			var distance = MathF.Abs(_view.FrameToX(candidate) - mouseX);
			if (distance <= bestDistance)
			{
				bestDistance = distance;
				frame = candidate;
				found = true;
			}
		}

		return found;
	}

	/// <summary>Ctrl or Cmd with the wheel zooms around the pointer; Shift with the wheel pans.</summary>
	private void HandleZoomAndPan(float keyAreaLeft)
	{
		var io = ImGui.GetIO();
		var wheel = io.MouseWheel;
		if (wheel == 0.0f)
		{
			return;
		}

		if (io.KeyCtrl || io.KeySuper)
		{
			_fitView = false;
			_view = _view.ZoomAround(ImGui.GetMousePos().X, MathF.Pow(ZoomPerWheelStep, wheel), MinPixelsPerFrame, MaxPixelsPerFrame);
		}
		else if (io.KeyShift)
		{
			_fitView = false;
			_view = _view with { StartFrame = _view.StartFrame - wheel * 40.0f / _view.PixelsPerFrame };
		}
	}

	private int ChooseTickStep()
	{
		foreach (var step in TickSteps)
		{
			if (step * _view.PixelsPerFrame >= MinLabelSpacingPixels)
			{
				return step;
			}
		}

		return TickSteps[^1];
	}

	private void ToggleCollapsed(string bone)
	{
		if (_collapsedBones.Remove(bone) == false)
		{
			_collapsedBones.Add(bone);
		}

		_collapsedVersion++;
	}

	private void RebuildModelIfNeeded(Skeleton skeleton)
	{
		var keys = _session.GetBoneKeyFrames();
		var key = new ModelKey(skeleton, keys, _collapsedVersion, _keyedOnly);
		if (key == _modelKey)
		{
			return;
		}

		_model.Rebuild(skeleton, keys, _collapsedBones, _keyedOnly);
		_modelKey = key;
	}

	private static uint Pack(ColorRGBA color) => ImGui.ColorConvertFloat4ToU32(color);

	private enum GestureKind
	{
		None,
		Key,
		Box
	}

	/// <param name="Row">Row pressed on.</param>
	/// <param name="Frame">Frame of the diamond pressed, for a key gesture.</param>
	/// <param name="AddedOnPress">The press added the diamond to the selection, so releasing must not toggle it off again.</param>
	private readonly record struct PointerGesture(
		GestureKind Kind,
		Vector2 Start,
		bool Dragged,
		int Row,
		int Frame,
		bool AddedOnPress);

	private readonly record struct ModelKey(
		Skeleton? Skeleton,
		IReadOnlyDictionary<string, int[]>? Keys,
		int CollapsedVersion,
		bool KeyedOnly);
}

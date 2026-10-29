using System.Numerics;
using ImGuiNET;

namespace WolfEngine.Editor.UI;

/// <summary>
/// Lays out a compact Material-style field, or a labeled group of compound inputs.
/// Native ImGui controls still own input, keyboard navigation and editing.
/// </summary>
internal readonly struct EditorField : IDisposable
{
	internal const float ControlPaddingY = 3.0f;
	internal const float ComponentSpacing = 6.0f;

	private readonly string _label;
	private readonly Vector2 _min;
	private readonly Vector2 _max;
	private readonly float _padding;
	private readonly float _labelSize;
	private readonly bool _compound;
	private readonly EditorReferenceKind? _referenceKind;
	private readonly Vector4 _accent;
	private readonly float _prefixWidth;

	public float ContentWidth => MathF.Max(1.0f, _max.X - _min.X - _padding * 2.0f - _prefixWidth);

	public EditorField(
		string label,
		bool compound = false,
		int controlRows = 1,
		EditorReferenceKind? referenceKind = null)
	{
		_label = label;
		_compound = compound;
		_referenceKind = referenceKind;
		_accent = referenceKind == EditorReferenceKind.Entity ? EditorPalette.EntityReference : EditorPalette.Primary;
		_prefixWidth = referenceKind.HasValue ? ImGui.GetFontSize() * 1.3f : 0.0f;
		_padding = compound ? 0.0f : ImGui.GetFontSize() * 0.65f;
		_labelSize = ImGui.GetFontSize() * 0.8f;
		_min = ImGui.GetCursorScreenPos();
		var width = MathF.Max(1.0f, ImGui.GetContentRegionAvail().X);
		var controlHeight = ImGui.GetFontSize() + ControlPaddingY * 2.0f;
		var height = _labelSize + 12.0f + controlHeight * controlRows + ComponentSpacing * (controlRows - 1);
		_max = _min + new Vector2(width, height);

		ImGui.PushID(label);
		ImGui.BeginGroup();
		ImGui.Dummy(new Vector2(width, 0.0f));
		if (compound == false)
		{
			var background = ImGui.GetStyle().Colors[(int)ImGuiCol.FrameBg];
			if (referenceKind.HasValue)
			{
				background = EditorPalette.Tint(background, _accent, 0.09f);
			}

			ImGui.GetWindowDrawList().AddRectFilled(
				_min,
				_max,
				ImGui.GetColorU32(background),
				ImGui.GetStyle().FrameRounding,
				referenceKind.HasValue ? ImDrawFlags.None : ImDrawFlags.RoundCornersTop);
		}

		ImGui.SetCursorScreenPos(_min + new Vector2(_padding + _prefixWidth, _labelSize + 8.0f));
		ImGui.SetNextItemWidth(ContentWidth);
	}

	public void Dispose()
	{
		End();
	}

	public void End(bool popupOpen = false)
	{
		// Include the label and bottom padding in layout and the aggregate item.
		// EndGroup preserves the native controls' active/edited state for callers.
		ImGui.SetCursorScreenPos(new Vector2(_min.X, _max.Y));
		ImGui.Dummy(Vector2.Zero);
		ImGui.EndGroup();

		var focused = popupOpen || ImGui.IsItemActive() || ImGui.IsItemFocused();
		var hovered = ImGui.IsItemHovered();
		var labelColor = ImGui.GetColorU32(focused ? ImGuiCol.CheckMark : ImGuiCol.TextDisabled);
		var lineColor = focused
			? ImGui.GetColorU32(ImGuiCol.CheckMark)
			: ImGui.GetColorU32(ImGuiCol.TextDisabled, hovered ? 0.9f : 0.45f);
		var drawList = ImGui.GetWindowDrawList();
		var badge = _referenceKind == EditorReferenceKind.Entity ? "Entity" : "Asset";
		var badgeWidth = _referenceKind.HasValue
			? ImGui.CalcTextSize(badge).X * _labelSize / ImGui.GetFontSize() + 10.0f
			: 0.0f;

		drawList.PushClipRect(
			_min + new Vector2(_padding, 0.0f),
			new Vector2(MathF.Max(_min.X + _padding, _max.X - _padding - badgeWidth), _max.Y),
			true);
		drawList.AddText(
			ImGui.GetFont(),
			_labelSize,
			_min + new Vector2(_padding, 4.0f),
			labelColor,
			_label);
		drawList.PopClipRect();
		if (_referenceKind.HasValue)
		{
			var border = focused ? EditorPalette.Primary : _accent;
			border.W *= focused ? 1.0f : 0.50f;
			drawList.AddRect(
				_min + new Vector2(0.5f),
				_max - new Vector2(0.5f),
				ImGui.GetColorU32(border),
				ImGui.GetStyle().FrameRounding,
				ImDrawFlags.None,
				focused ? 2.0f : 1.0f);
			drawList.PushClipRect(_min, _max, true);
			drawList.AddText(
				ImGui.GetFont(),
				_labelSize,
				new Vector2(_max.X - _padding - badgeWidth + 10.0f, _min.Y + 4.0f),
				ImGui.GetColorU32(_accent),
				badge);
			DrawReferenceIcon(drawList);
			drawList.PopClipRect();
		}
		else if (_compound == false)
		{
			drawList.AddLine(
				new Vector2(_min.X, _max.Y - 1.0f),
				new Vector2(_max.X, _max.Y - 1.0f),
				lineColor,
				focused ? 2.0f : 1.0f);
		}

		ImGui.PopID();
	}

	private void DrawReferenceIcon(ImDrawListPtr drawList)
	{
		var min = _min + new Vector2(_padding, _labelSize + 8.0f + ControlPaddingY);
		var size = ImGui.GetFontSize() * 0.8f;
		var color = ImGui.GetColorU32(_accent);
		if (_referenceKind == EditorReferenceKind.Entity)
		{
			var top = min + new Vector2(size * 0.5f, 0.0f);
			var left = min + new Vector2(0.0f, size * 0.25f);
			var right = min + new Vector2(size, size * 0.25f);
			var center = min + new Vector2(size * 0.5f, size * 0.5f);
			drawList.AddQuad(top, right, center, left, color, 1.2f);
			drawList.AddLine(left, left + new Vector2(0.0f, size * 0.5f), color, 1.2f);
			drawList.AddLine(right, right + new Vector2(0.0f, size * 0.5f), color, 1.2f);
			drawList.AddLine(center, center + new Vector2(0.0f, size * 0.5f), color, 1.2f);
			drawList.AddLine(left + new Vector2(0.0f, size * 0.5f), center + new Vector2(0.0f, size * 0.5f), color, 1.2f);
			drawList.AddLine(right + new Vector2(0.0f, size * 0.5f), center + new Vector2(0.0f, size * 0.5f), color, 1.2f);
		}
		else
		{
			drawList.AddRect(min, min + new Vector2(size * 0.75f, size), color, 1.0f, ImDrawFlags.None, 1.2f);
			drawList.AddLine(min + new Vector2(size * 0.2f, size * 0.4f), min + new Vector2(size * 0.55f, size * 0.4f), color, 1.0f);
			drawList.AddLine(min + new Vector2(size * 0.2f, size * 0.65f), min + new Vector2(size * 0.55f, size * 0.65f), color, 1.0f);
		}
	}
}

/// <summary>
/// Applies compact control padding. Simple fields use a transparent native frame;
/// compound controls retain their individual input backgrounds. For simple fields,
/// end this scope before drawing popup contents so those retain the normal theme.
/// </summary>
internal readonly struct EditorFieldControlStyle : IDisposable
{
	public EditorFieldControlStyle() : this(false)
	{
	}

	public EditorFieldControlStyle(bool preserveFrames)
	{
		ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(preserveFrames ? 5.0f : 0.0f, EditorField.ControlPaddingY));
		ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0.0f);
		ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, preserveFrames ? ImGui.GetStyle().FrameRounding : 0.0f);
		var style = ImGui.GetStyle();
		ImGui.PushStyleColor(ImGuiCol.FrameBg, preserveFrames ? style.Colors[(int)ImGuiCol.FrameBg] : Vector4.Zero);
		ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, preserveFrames ? style.Colors[(int)ImGuiCol.FrameBgHovered] : Vector4.Zero);
		ImGui.PushStyleColor(ImGuiCol.FrameBgActive, preserveFrames ? style.Colors[(int)ImGuiCol.FrameBgActive] : Vector4.Zero);
		ImGui.PushStyleColor(ImGuiCol.Button, preserveFrames ? style.Colors[(int)ImGuiCol.Button] : Vector4.Zero);
	}

	public void Dispose()
	{
		ImGui.PopStyleColor(4);
		ImGui.PopStyleVar(3);
	}
}

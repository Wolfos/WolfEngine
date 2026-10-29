using System.Numerics;
using ImGuiNET;

namespace WolfEngine.Editor.UI;

internal static class EditorCompoundFields
{
	private static readonly string[] AxisLabels = ["X", "Y", "Z", "W"];

	public static bool InputVector(string label, Span<float> values)
	{
		var availableWidth = MathF.Max(1.0f, ImGui.GetContentRegionAvail().X);
		var minimumCellWidth = ImGui.GetFontSize() * 4.5f;
		var columns = Math.Clamp(
			(int)((availableWidth + EditorField.ComponentSpacing) / (minimumCellWidth + EditorField.ComponentSpacing)),
			1,
			values.Length);

		// Keep wrapped vectors in balanced rows, rather than leaving a lone axis
		// beneath a three-column row or separating Z from an X/Y pair.
		if (columns < values.Length)
		{
			columns = values.Length == 4 && columns >= 2 ? 2 : 1;
		}

		var rows = (values.Length + columns - 1) / columns;
		var background = ImGui.GetColorU32(ImGuiCol.FrameBg);
		var hoveredBackground = ImGui.GetColorU32(ImGuiCol.FrameBgHovered);
		var rounding = ImGui.GetStyle().FrameRounding;
		using var field = new EditorField(label, compound: true, controlRows: rows);
		using var controlStyle = new EditorFieldControlStyle();
		var origin = ImGui.GetCursorScreenPos();
		var width = MathF.Max(1.0f, (field.ContentWidth - EditorField.ComponentSpacing * (columns - 1)) / columns);
		var height = ImGui.GetFrameHeight();
		var changed = false;

		for (var index = 0; index < values.Length; index++)
		{
			ImGui.SetCursorScreenPos(origin + new Vector2(
				index % columns * (width + EditorField.ComponentSpacing),
				index / columns * (height + EditorField.ComponentSpacing)));
			ImGui.PushID(index);
			try
			{
				changed |= InputAxis(
					AxisLabels[index],
					ref values[index],
					width,
					background,
					hoveredBackground,
					rounding,
					EditorPalette.Axis(index));
			}
			finally
			{
				ImGui.PopID();
			}
		}

		return changed;
	}

	private static bool InputAxis(
		string axis,
		ref float value,
		float width,
		uint background,
		uint hoveredBackground,
		float rounding,
		Vector4 accent)
	{
		var min = ImGui.GetCursorScreenPos();
		var max = min + new Vector2(width, ImGui.GetFrameHeight());
		var padding = MathF.Min(ImGui.GetFontSize() * 0.35f, width * 0.08f);
		var prefixWidth = MathF.Min(ImGui.CalcTextSize(axis).X + padding * 2.0f, width * 0.4f);
		var hovered = ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(min, max);
		var drawList = ImGui.GetWindowDrawList();
		drawList.AddRectFilled(min, max, hovered ? hoveredBackground : background, rounding, ImDrawFlags.RoundCornersTop);

		ImGui.BeginGroup();
		ImGui.Dummy(new Vector2(width, 0.0f));
		ImGui.SetCursorScreenPos(min + new Vector2(prefixWidth, 0.0f));
		ImGui.SetNextItemWidth(MathF.Max(1.0f, width - prefixWidth - padding));
		var changed = ImGui.InputFloat("##value", ref value);
		var focused = ImGui.IsItemActive() || ImGui.IsItemFocused();
		var axisColor = ImGui.GetColorU32(accent);
		var lineColor = focused
			? ImGui.GetColorU32(ImGuiCol.CheckMark)
			: ImGui.GetColorU32(new Vector4(accent.X, accent.Y, accent.Z, hovered ? 0.9f : 0.50f));

		drawList.PushClipRect(min, max, true);
		drawList.AddText(min + new Vector2(padding, EditorField.ControlPaddingY), axisColor, axis);
		drawList.AddLine(
			new Vector2(min.X, max.Y - 1.0f),
			new Vector2(max.X, max.Y - 1.0f),
			lineColor,
			focused ? 2.0f : 1.0f);
		drawList.PopClipRect();

		ImGui.SetCursorScreenPos(new Vector2(min.X, max.Y));
		ImGui.Dummy(Vector2.Zero);
		ImGui.EndGroup();

		return changed;
	}

	public static bool UseCompactColorPreview(float width, int channels)
	{
		return width < ImGui.GetFontSize() * 5.0f * channels + ImGui.GetFrameHeightWithSpacing();
	}

	public static void DrawColorPreview(Vector4 color, bool alpha)
	{
		var inRange = color.X >= 0.0f && color.X <= 1.0f &&
			color.Y >= 0.0f && color.Y <= 1.0f &&
			color.Z >= 0.0f && color.Z <= 1.0f &&
			color.W >= 0.0f && color.W <= 1.0f;
		var preview = inRange
			? $"#{ToByte(color.X):X2}{ToByte(color.Y):X2}{ToByte(color.Z):X2}"
			: "HDR color";

		if (inRange && alpha)
		{
			preview += $"{ToByte(color.W):X2}";
		}

		ImGui.SameLine();
		var min = ImGui.GetCursorScreenPos();
		var width = MathF.Max(1.0f, ImGui.GetContentRegionAvail().X);
		var max = min + new Vector2(width, ImGui.GetTextLineHeight());
		var drawList = ImGui.GetWindowDrawList();
		drawList.PushClipRect(min, max, true);
		drawList.AddText(min, ImGui.GetColorU32(ImGuiCol.Text), preview);
		drawList.PopClipRect();
		ImGui.Dummy(new Vector2(MathF.Min(width, ImGui.CalcTextSize(preview).X), ImGui.GetTextLineHeight()));
	}

	private static int ToByte(float value)
	{
		return (int)MathF.Round(value * 255.0f);
	}
}

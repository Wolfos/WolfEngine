using System.Numerics;
using ImGuiNET;

namespace WolfEngine.Editor.UI;

internal static class AssetsWindowDrawing
{
	public static uint SeparatorColor()
	{
		return ImGui.GetColorU32(ImGuiCol.TitleBg);
	}

	public static void PushPaneStyle()
	{
		ImGui.PushStyleColor(ImGuiCol.ChildBg, ImGui.GetColorU32(ImGuiCol.WindowBg));
		ImGui.PushStyleColor(ImGuiCol.Border, SeparatorColor());
	}

	public static void AdvanceTable(ref int columnIndex, int columnCount)
	{
		if (columnIndex == 0)
		{
			ImGui.TableNextRow();
		}

		ImGui.TableSetColumnIndex(columnIndex);
		columnIndex = (columnIndex + 1) % columnCount;
	}

	public static void DrawCardTextBlock(ImDrawListPtr drawList, Vector2 itemMin, Vector2 itemMax, float startY,
		string title, string? subtitle)
	{
		var textInset = 8.0f;
		var textMinX = itemMin.X + textInset;
		var textMaxX = itemMax.X - textInset;
		var titleText = ClipTextToWidth(title, textMaxX - textMinX);
		DrawCenteredText(drawList, titleText, textMinX, textMaxX, startY, ImGui.GetColorU32(ImGuiCol.Text));

		if (string.IsNullOrWhiteSpace(subtitle))
		{
			return;
		}

		var subtitleY = startY + ImGui.GetTextLineHeightWithSpacing();
		var subtitleText = ClipTextToWidth(subtitle, textMaxX - textMinX);
		DrawCenteredText(
			drawList,
			subtitleText,
			textMinX,
			textMaxX,
			subtitleY,
			ImGui.GetColorU32(ImGuiCol.TextDisabled));
	}

	public static void DrawHorizontalPaneSeparator(float thickness)
	{
		var cursorPosition = ImGui.GetCursorScreenPos();
		var windowPosition = ImGui.GetWindowPos();
		var windowSize = ImGui.GetWindowSize();
		var separatorSize = new Vector2(
			MathF.Max(windowPosition.X + windowSize.X - cursorPosition.X, 1.0f),
			thickness);
		ImGui.GetWindowDrawList().AddLine(
			cursorPosition,
			new Vector2(cursorPosition.X + separatorSize.X, cursorPosition.Y),
			SeparatorColor(),
			thickness);
		ImGui.Dummy(separatorSize);
	}

	public static void DrawVerticalPaneSeparator(float thickness)
	{
		var cursorPosition = ImGui.GetCursorScreenPos();
		var windowPosition = ImGui.GetWindowPos();
		var windowSize = ImGui.GetWindowSize();
		ImGui.GetWindowDrawList().AddLine(
			new Vector2(cursorPosition.X, windowPosition.Y),
			new Vector2(cursorPosition.X, windowPosition.Y + windowSize.Y),
			SeparatorColor(),
			thickness);
		ImGui.Dummy(new Vector2(thickness, MathF.Max(ImGui.GetContentRegionAvail().Y, 1.0f)));
	}

	public static void DrawDragPreview(AssetBrowserDragTarget dragTarget, float rounding)
	{
		var label = Path.GetFileName(dragTarget.RelativePath);
		if (string.IsNullOrWhiteSpace(label))
		{
			label = dragTarget.RelativePath;
		}

		var style = ImGui.GetStyle();
		var mousePosition = ImGui.GetIO().MousePos;
		var padding = new Vector2(style.FramePadding.X + 4.0f, style.FramePadding.Y + 2.0f);
		var previewMin = mousePosition + new Vector2(16.0f, 18.0f);
		var previewSize = ImGui.CalcTextSize(label) + (padding * 2.0f);
		var previewMax = previewMin + previewSize;
		var drawList = ImGui.GetForegroundDrawList();

		drawList.AddRectFilled(previewMin, previewMax, ImGui.GetColorU32(ImGuiCol.PopupBg), rounding);
		drawList.AddRect(previewMin, previewMax, ImGui.GetColorU32(ImGuiCol.Border), rounding);
		drawList.AddText(previewMin + padding, ImGui.GetColorU32(ImGuiCol.Text), label);
	}

	private static void DrawCenteredText(ImDrawListPtr drawList, string text, float minX, float maxX, float y,
		uint color)
	{
		var textSize = ImGui.CalcTextSize(text);
		var availableWidth = maxX - minX;
		var textX = minX + MathF.Max((availableWidth - textSize.X) * 0.5f, 0.0f);
		drawList.AddText(new Vector2(textX, y), color, text);
	}

	private static string ClipTextToWidth(string text, float maxWidth)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return string.Empty;
		}

		if (ImGui.CalcTextSize(text).X <= maxWidth)
		{
			return text;
		}

		const string ellipsis = "...";
		for (var length = text.Length - 1; length > 0; length--)
		{
			var candidate = text[..length] + ellipsis;
			if (ImGui.CalcTextSize(candidate).X <= maxWidth)
			{
				return candidate;
			}
		}

		return ellipsis;
	}
}

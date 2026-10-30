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

	public static void DrawCardTitle(ImDrawListPtr draw, string title, Vector2 position, float width)
	{
		if (width <= 0.0f || string.IsNullOrWhiteSpace(title)) return;

		if (ImGui.CalcTextSize(title).X <= width)
		{
			draw.AddText(position, ImGui.GetColorU32(ImGuiCol.Text), title);
			return;
		}

		var length = title.Length;
		while (length > 1 && ImGui.CalcTextSize(title[..length]).X > width)
		{
			length--;
		}

		var separator = title.LastIndexOfAny([' ', '_', '-'], length - 1, length);
		if (separator >= length / 2)
		{
			length = separator + 1;
		}

		if (length > 0 && char.IsHighSurrogate(title[length - 1])) length--;

		draw.AddText(position, ImGui.GetColorU32(ImGuiCol.Text), title[..length]);
		draw.AddText(position + new Vector2(0.0f, ImGui.GetTextLineHeight()), ImGui.GetColorU32(ImGuiCol.Text),
			ClipTextToWidth(title[length..].TrimStart(), width));
	}

	public static string ClipTextToWidth(string text, float maxWidth)
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
			if (char.IsHighSurrogate(text[length - 1])) continue;

			var candidate = text[..length] + ellipsis;
			if (ImGui.CalcTextSize(candidate).X <= maxWidth)
			{
				return candidate;
			}
		}

		return ellipsis;
	}
}

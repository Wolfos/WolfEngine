using System.Numerics;
using ImGuiNET;
using WolfEngine.Rendering;
using WolfEngine.Rendering.UI;

namespace WolfEngine.UI;

internal sealed class UiFrameBuilder
{
	private readonly UiFontAtlas _font;

	public UiFrameBuilder(UiFontAtlas font) => _font = font;

	public UiFrameData Build(UiNode root, int width, int height) =>
		_font.BuildFrame(width, height, draw => Append(draw, root, 1));

	private static void Append(ImDrawListPtr draw, UiNode node, float inheritedOpacity)
	{
		if (!node.Style.Display) return;
		var opacity = inheritedOpacity * node.Style.Opacity;
		if (!node.IsText && node.Width > 0 && node.Height > 0 && node.Style.Background.A * opacity > 0.001f)
		{
			draw.AddRectFilled(
				new Vector2(node.Left, node.Top),
				new Vector2(node.Left + node.Width, node.Top + node.Height),
				Pack(WithOpacity(node.Style.Background, opacity)),
				MathF.Max(0, node.Style.BorderRadius));
		}
		if (node.IsText && !string.IsNullOrEmpty(node.Text))
		{
			BitmapFont.Draw(draw, new Vector2(node.Left, node.Top), node.Style.FontSize,
				Pack(WithOpacity(node.Style.Color, opacity)), node.Text!);
		}
		for (var i = 0; i < node.Children.Count; i++) Append(draw, node.Children[i], opacity);
	}

	private static uint Pack(ColorRGBA value)
	{
		var r = (uint)Math.Clamp((int)MathF.Round(value.R * 255), 0, 255);
		var g = (uint)Math.Clamp((int)MathF.Round(value.G * 255), 0, 255);
		var b = (uint)Math.Clamp((int)MathF.Round(value.B * 255), 0, 255);
		var a = (uint)Math.Clamp((int)MathF.Round(value.A * 255), 0, 255);
		return r | (g << 8) | (b << 16) | (a << 24);
	}

	private static ColorRGBA WithOpacity(ColorRGBA value, float opacity) =>
		new(value.R, value.G, value.B, value.A * opacity);
}

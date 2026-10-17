using HarfBuzzSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.Tooling.Fonts;

/// <summary>Decodes the baked RGB distance field, rather than using a second font renderer.</summary>
internal static class FontPreview
{
	public static void Write(Font font, FontArtifact artifact, string path)
	{
		using var image = new Image<Rgba32>(1920, 1152, new Rgba32(16, 24, 38));
		var glyphs = artifact.Glyphs.ToDictionary(g => g.Id);
		var y = 28f;
		foreach (var size in new[] { 18, 32, 64 })
		{
			DrawLine($"Font / {size}px / MTSDF", size, ref y);
			foreach (var line in FontCompiler.PreviewText.Split('\n')) DrawLine(line, size, ref y);
			y += 24;
		}
		image.SaveAsPng(path);

		void DrawLine(string text, int size, ref float top)
		{
			using var buffer = FontCompiler.ShapeText(font, text);
			var infos = buffer.GlyphInfos;
			var positions = buffer.GlyphPositions;
			var pen = 28f;
			var baseline = top + artifact.Metrics.Ascender * size;
			for (var i = 0; i < infos.Length; i++)
			{
				var glyph = glyphs[infos[i].Codepoint];
				var originX = pen + positions[i].XOffset * size / (float)artifact.Metrics.UnitsPerEm;
				var originY = baseline - positions[i].YOffset * size / (float)artifact.Metrics.UnitsPerEm;
				DrawGlyph(image, artifact, glyph, originX, originY, size);
				pen += positions[i].XAdvance * size / (float)artifact.Metrics.UnitsPerEm;
			}
			top += artifact.Metrics.LineHeight * size;
		}
	}

	private static void DrawGlyph(Image<Rgba32> image, FontArtifact font, FontGlyph glyph, float x, float y, int size)
	{
		if (glyph.AtlasWidth == 0) return;
		var left = x + glyph.Left * size;
		var top = y - glyph.Top * size;
		var width = (glyph.Right - glyph.Left) * size;
		var height = (glyph.Top - glyph.Bottom) * size;
		for (var py = Math.Max(0, (int)MathF.Floor(top)); py < Math.Min(image.Height, MathF.Ceiling(top + height)); py++)
			for (var px = Math.Max(0, (int)MathF.Floor(left)); px < Math.Min(image.Width, MathF.Ceiling(left + width)); px++)
			{
				var u = (px + 0.5f - left) / width * glyph.AtlasWidth - 0.5f;
				var v = (py + 0.5f - top) / height * glyph.AtlasHeight - 0.5f;
				var r = Sample(font, glyph, u, v, 0); var g = Sample(font, glyph, u, v, 1); var b = Sample(font, glyph, u, v, 2);
				var median = Math.Max(Math.Min(r, g), Math.Min(Math.Max(r, g), b));
				var alpha = Math.Clamp((median - 0.5f) * font.Metrics.DistanceRange * size / font.Metrics.PixelsPerEm + 0.5f, 0, 1);
				var previous = image[px, py];
				image[px, py] = new Rgba32((byte)(previous.R + (229 - previous.R) * alpha),
					(byte)(previous.G + (238 - previous.G) * alpha), (byte)(previous.B + (248 - previous.B) * alpha));
			}
	}

	private static float Sample(FontArtifact font, FontGlyph glyph, float u, float v, int channel)
	{
		u = Math.Clamp(u, 0, glyph.AtlasWidth - 1); v = Math.Clamp(v, 0, glyph.AtlasHeight - 1);
		var x = (int)u; var y = (int)v;
		var nextX = Math.Min(x + 1, glyph.AtlasWidth - 1); var nextY = Math.Min(y + 1, glyph.AtlasHeight - 1);
		float Pixel(int px, int py) => font.PixelsRgba[((glyph.AtlasY + py) * font.Metrics.Width + glyph.AtlasX + px) * 4 + channel] / 255f;
		var a = Pixel(x, y) + (Pixel(nextX, y) - Pixel(x, y)) * (u - x);
		var b = Pixel(x, nextY) + (Pixel(nextX, nextY) - Pixel(x, nextY)) * (u - x);
		return a + (b - a) * (v - y);
	}
}

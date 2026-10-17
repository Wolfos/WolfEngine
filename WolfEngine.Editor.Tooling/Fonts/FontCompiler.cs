using System.Numerics;
using HarfBuzzSharp;
using Remora.MSDFGen;
using Remora.MSDFGen.Graphics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using WolfEngine.AssetPipeline;
using WolfEngine.Importing;
using Buffer = HarfBuzzSharp.Buffer;

namespace WolfEngine.Editor.Tooling.Fonts;

/// <summary>Compiles the default font instance into a linear MTSDF atlas and diagnostic preview.</summary>
public sealed class FontCompiler : IFontCompiler
{
	public const string PreviewText = "WolfEngine Gameplay UI\nThe quick brown fox jumps over the lazy dog.\nABCDEFGHIJKLMNOPQRSTUVWXYZ\nabcdefghijklmnopqrstuvwxyz\n0123456789  Health: 100%  Score: 12345\nAVATAR office affinity ffi fl fi";

	public FontCompilationResult Compile(string sourcePath, string outputDirectory, FontImportSettings settings)
	{
		ArgumentNullException.ThrowIfNull(settings);
		if (settings.PixelsPerEm is < 16 or > 128 || settings.DistanceRange is < 2 or > 16 ||
		    settings.AtlasWidth is < 128 or > 4096 || (settings.AtlasWidth & (settings.AtlasWidth - 1)) != 0)
			throw new ArgumentOutOfRangeException(nameof(settings), "Use 16–128 pixels/em, 2–16 distance range, and a power-of-two atlas width (128–4096).");
		using var blob = Blob.FromFile(sourcePath);
		using var face = new Face(blob, 0);
		if (face.GlyphCount == 0 || face.UnitsPerEm == 0) throw new InvalidDataException("Font contains no usable OpenType glyphs.");
		using var font = new Font(face);
		var units = (int)face.UnitsPerEm;
		font.SetFunctionsOpenType();
		font.SetScale(units, units);
		if (!font.TryGetHorizontalFontExtents(out var extents)) throw new InvalidDataException("Font contains no horizontal metrics.");

		var map = new Dictionary<int, uint>();
		var glyphIds = new SortedSet<uint> { 0 };
		for (var cp = 32; cp <= 126; cp++)
		{
			if (!font.TryGetNominalGlyph(cp, out var id)) continue;
			map.Add(cp, id);
			glyphIds.Add(id);
		}
		// Include ligature/context glyphs selected by the proof text without changing the original glyph ID space.
		foreach (var line in PreviewText.Split('\n'))
		{
			using var buffer = ShapeText(font, line);
			foreach (var info in buffer.GlyphInfos) glyphIds.Add(info.Codepoint);
		}
		HarfBuzzGlyphClosure.Expand(face, glyphIds);
		var baked = new List<BakedGlyph>(glyphIds.Count);
		foreach (var id in glyphIds) baked.Add(BakeGlyph(font, units, id, settings));
		// Deterministic shelf packing with a one-texel guard between glyph rectangles.
		var x = 1;
		var y = 1;
		var rowHeight = 0;
		foreach (var glyph in baked)
		{
			if (glyph.Width == 0) continue;
			if (glyph.Width + 2 > settings.AtlasWidth) throw new InvalidDataException("Glyph exceeds the font atlas width.");
			if (x + glyph.Width + 1 > settings.AtlasWidth) { x = 1; y += rowHeight + 1; rowHeight = 0; }
			glyph.X = x;
			glyph.Y = y;
			x += glyph.Width + 1;
			rowHeight = Math.Max(rowHeight, glyph.Height);
		}
		var height = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)(y + rowHeight + 1));
		if (height > 4096) throw new InvalidDataException("Font atlas exceeds 4096 pixels; increase its width or reduce bake resolution.");
		var pixels = new byte[settings.AtlasWidth * height * 4];
		foreach (var glyph in baked)
			for (var row = 0; row < glyph.Height; row++)
				glyph.Pixels.AsSpan(row * glyph.Width * 4, glyph.Width * 4)
					.CopyTo(pixels.AsSpan(((glyph.Y + row) * settings.AtlasWidth + glyph.X) * 4));
		var summary = new FontAssetSummary
		{
			Name = Path.GetFileNameWithoutExtension(sourcePath), SourceHash = AssetHashing.ComputeFileHash(sourcePath),
			UnitsPerEm = units, Ascender = extents.Ascender / (float)units, Descender = extents.Descender / (float)units,
			LineHeight = (extents.Ascender - extents.Descender + extents.LineGap) / (float)units,
			PixelsPerEm = settings.PixelsPerEm, DistanceRange = settings.DistanceRange,
			Width = settings.AtlasWidth, Height = height, GlyphCount = baked.Count
		};
		var artifact = new FontArtifact
		{
			Metrics = summary, CharacterMap = map, PixelsRgba = pixels, FontData = File.ReadAllBytes(sourcePath),
			Glyphs = baked.Select(g => new FontGlyph(g.Id, g.Advance, g.Left, g.Bottom, g.Right, g.Top,
				g.X, g.Y, g.Width, g.Height)).ToArray()
		};
		Directory.CreateDirectory(outputDirectory);
		var artifactPath = Path.Combine(outputDirectory, "font.wolffont");
		FontArtifactSerializer.Write(artifactPath, artifact);
		// Content-address previews so the editor image cache cannot show a stale bake after reimport.
		var revision = AssetHashing.ComputeFileHash(artifactPath);
		var atlasPath = Path.Combine(outputDirectory, $"atlas-{revision}.png");
		var previewPath = Path.Combine(outputDirectory, $"preview-{revision}.png");
		// Opaque RGB export makes the field inspectable; alpha in the artifact is the true signed distance.
		using (var atlas = Image.LoadPixelData<Rgba32>(pixels, summary.Width, summary.Height))
		{
			for (var ay = 0; ay < atlas.Height; ay++) for (var ax = 0; ax < atlas.Width; ax++)
				atlas[ax, ay] = new Rgba32(atlas[ax, ay].R, atlas[ax, ay].G, atlas[ax, ay].B, 255);
			atlas.SaveAsPng(atlasPath);
		}
		FontPreview.Write(font, artifact, previewPath);
		return new FontCompilationResult(artifactPath, atlasPath, previewPath, summary);
	}

	internal static Buffer ShapeText(Font font, string text)
	{
		var buffer = new Buffer();
		buffer.AddUtf16(text);
		buffer.GuessSegmentProperties();
		font.Shape(buffer);
		return buffer;
	}

	private static BakedGlyph BakeGlyph(Font font, int units, uint id, FontImportSettings settings)
	{
		using var reader = new HarfBuzzOutlineReader(units);
		var shape = reader.Read(font, id);
		var advance = font.GetHorizontalGlyphAdvance(id) / (float)units;
		if (shape.Contours.All(c => c.Edges.Count == 0)) return new BakedGlyph { Id = id, Advance = advance };
		MSDF.EdgeColoringSimple(shape, 3, 0);
		double left = double.PositiveInfinity, bottom = double.PositiveInfinity;
		double right = double.NegativeInfinity, top = double.NegativeInfinity;
		shape.GetBounds(ref left, ref bottom, ref right, ref top);
		var scale = settings.PixelsPerEm;
		var padding = settings.DistanceRange / 2f + 1;
		var minX = (float)Math.Floor(left * scale - padding);
		var minY = (float)Math.Floor(bottom * scale - padding);
		var width = checked((int)(Math.Ceiling(right * scale + padding) - minX));
		var height = checked((int)(Math.Ceiling(top * scale + padding) - minY));
		if (width is <= 0 or > 4096 || height is <= 0 or > 4096) throw new InvalidDataException($"Invalid bounds for glyph {id}.");
		var msdf = new Pixmap<Color3>(width, height);
		var sdf = new Pixmap<float>(width, height);
		// Remora 1.0 MSDF adds 0.5 in shape space *after* projection. Compensate that
		// offset, and then sample texel centers; translation here is in pixel units.
		var translate = new Vector2(-minX + scale * 0.5f - 0.5f, -minY + scale * 0.5f - 0.5f);
		MSDF.GenerateMSDF(msdf, shape, settings.DistanceRange / (double)scale, new Vector2(scale), translate);
		FontDistanceField.Generate(sdf, shape, scale, minX, minY, settings.DistanceRange);
		// Multi-channel corner transitions can clash under bilinear filtering. Remora's
		// correction API accepts Color4, so retain float precision until after correction.
		var corrected = new Pixmap<Color4>(width, height);
		for (var cy = 0; cy < height; cy++) for (var cx = 0; cx < width; cx++)
		{
			var rgb = msdf[cx, cy];
			corrected[cx, cy] = new Color4 { R = rgb.R, G = rgb.G, B = rgb.B, A = sdf[cx, cy] };
		}
		MSDF.CorrectErrors(corrected, new System.Drawing.Rectangle(0, 0, width, height), new Vector2(1.001f / settings.DistanceRange));
		CorrectInterpolation(corrected);
		var pixels = new byte[width * height * 4];
		for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
		{
			var color = corrected[x, height - 1 - y];
			var offset = (y * width + x) * 4;
			pixels[offset] = ToByte(color.R); pixels[offset + 1] = ToByte(color.G);
			pixels[offset + 2] = ToByte(color.B); pixels[offset + 3] = ToByte(sdf[x, height - 1 - y]);
		}
		return new BakedGlyph { Id = id, Advance = advance, Left = minX / scale, Bottom = minY / scale,
			Right = (minX + width) / scale, Top = (minY + height) / scale, Width = width, Height = height, Pixels = pixels };
	}
	private static byte ToByte(float value) => (byte)MathF.Round(Math.Clamp(value, 0, 1) * 255);

	private static void CorrectInterpolation(Pixmap<Color4> field)
	{
		// Remora's legacy correction does not catch all channel clashes after UNORM
		// saturation. Check the actual bilinear field against true SDF and collapse only
		// erroneous cells to scalar distance. Valid multi-channel corners stay intact.
		var repair = new bool[field.Width * field.Height];
		for (var y = 0; y < field.Height; y++) for (var x = 0; x < field.Width; x++)
		{
			var c = field[x, y];
			field[x, y] = new Color4 { R = Math.Clamp(c.R, 0, 1), G = Math.Clamp(c.G, 0, 1),
				B = Math.Clamp(c.B, 0, 1), A = Math.Clamp(c.A, 0, 1) };
		}
		for (var y = 0; y < field.Height - 1; y++) for (var x = 0; x < field.Width - 1; x++)
		{
			var a = field[x, y]; var b = field[x + 1, y]; var c = field[x, y + 1]; var d = field[x + 1, y + 1];
			for (var sy = 0; sy <= 4; sy++) for (var sx = 0; sx <= 4; sx++)
			{
				var u = sx / 4f; var v = sy / 4f;
				float Blend(float aa, float bb, float cc, float dd) => (aa + (bb - aa) * u) * (1 - v) + (cc + (dd - cc) * u) * v;
				var r = Blend(a.R, b.R, c.R, d.R); var g = Blend(a.G, b.G, c.G, d.G); var blue = Blend(a.B, b.B, c.B, d.B);
				var median = Math.Max(Math.Min(r, g), Math.Min(Math.Max(r, g), blue));
				var sdf = Blend(a.A, b.A, c.A, d.A);
				if (Math.Abs(median - sdf) <= 0.025f) continue;
				repair[y * field.Width + x] = repair[y * field.Width + x + 1] = true;
				repair[(y + 1) * field.Width + x] = repair[(y + 1) * field.Width + x + 1] = true;
			}
		}
		for (var y = 0; y < field.Height; y++) for (var x = 0; x < field.Width; x++)
		{
			if (!repair[y * field.Width + x]) continue;
			var c = field[x, y];
			c.R = c.G = c.B = c.A;
			field[x, y] = c;
		}
	}
	private sealed class BakedGlyph
	{
		public uint Id;
		public float Advance, Left, Bottom, Right, Top;
		public int X, Y, Width, Height;
		public byte[] Pixels = [];
	}
}

using System.Numerics;
using System.Runtime.InteropServices;
using HarfBuzzSharp;
using Remora.MSDFGen;
using WolfEngine.Editor.Tooling.Fonts;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Splash;
using Buffer = HarfBuzzSharp.Buffer;

namespace WolfEngine.Editor.Tooling.Splash;

/// <summary>
/// Bakes the engine splash screen from its vector sources: the logo SVG and the wordmark font in
/// <c>WolfEngine/BuiltInContent/Splash</c>. The result is laid out in reference units, one pixel on a
/// 1080-pixel-tall screen, centred on the screen.
/// </summary>
/// <remarks>
/// The logo SVG is read by paint order. The first path is the silhouette, which is also the outline the splash
/// traces, from its first point both ways round to the point halfway along. Paths in another colour are the
/// highlight, and later paths in the silhouette colour are details painted over the highlight.
/// </remarks>
public static class SplashScreenBaker
{
	public const string LogoFileName = "WolfEngineLogo.svg";
	public const string FontFileName = "Exo2-VariableFont_wght.ttf";
	public const string PoweredByText = "Powered by";
	public const string WordmarkText = "WOLFENGINE";

	// Layout, in reference units. Text sizes are the design's point sizes at 96 DPI.
	private const float LogoHeight = 300.0f;
	private const float PoweredBySize = 18.0f * 4.0f / 3.0f;
	private const float WordmarkSize = 30.0f * 4.0f / 3.0f;
	private const float LogoToPoweredByGap = 34.0f;
	private const float PoweredByToWordmarkGap = 10.0f;
	private const float FontWeight = 500.0f;

	// Two texels per reference unit is one texel per pixel on a 2160-pixel-tall screen.
	private const float TexelsPerUnit = 2.0f;
	// The logo's distance range doubles as the reach of the trace's glow; the renderer's glow stays inside it.
	private const float LogoMaxDistance = 48.0f;
	private const float TextMaxDistance = 8.0f;
	private const float FlatteningTolerance = 0.1f;

	/// <summary>Where the splash sources live under the engine project directory.</summary>
	public static string GetSourceDirectory(string engineContentRoot) =>
		Path.Combine(engineContentRoot, "BuiltInContent", "Splash");

	public static SplashScreenContent Bake(string sourceDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);

		var logo = BakeLogo(Path.Combine(sourceDirectory, LogoFileName));
		var text = BakeText(Path.Combine(sourceDirectory, FontFileName), logo.Bottom);

		// Centre the whole composition vertically on the screen.
		var offset = new Vector2(0.0f, -(logo.Top + text.Bottom) * 0.5f);

		return new SplashScreenContent
		{
			Logo = Offset(logo.Layer, offset),
			Text = Offset(text.Layer, offset),
			SilhouetteColor = logo.SilhouetteColor,
			HighlightColor = logo.HighlightColor,
			TextColor = logo.HighlightColor
		};
	}

	private static SplashScreenLayer Offset(SplashScreenLayer layer, Vector2 offset) => new()
	{
		Center = layer.Center + offset,
		Size = layer.Size,
		Width = layer.Width,
		Height = layer.Height,
		Format = layer.Format,
		Texels = layer.Texels
	};

	private sealed record BakedLogo(
		SplashScreenLayer Layer,
		float Top,
		float Bottom,
		ColorRGBA SilhouetteColor,
		ColorRGBA HighlightColor);

	private sealed record BakedText(SplashScreenLayer Layer, float Bottom);

	private static BakedLogo BakeLogo(string svgPath)
	{
		// Read once coarsely to size the logo, then again at the tolerance the bake resolution needs.
		var coarse = SvgPathReader.Read(svgPath, 1.0f);
		var (documentMin, documentMax) = GetBounds(coarse.SelectMany(path => path.Path.Contours));
		var documentSize = documentMax - documentMin;
		if (documentSize.X <= 0.0f || documentSize.Y <= 0.0f)
		{
			throw new InvalidDataException($"The logo '{svgPath}' has no area.");
		}

		var unitsPerDocumentUnit = LogoHeight / documentSize.Y;
		var texelsPerDocumentUnit = unitsPerDocumentUnit * TexelsPerUnit;
		var paths = SvgPathReader.Read(svgPath, FlatteningTolerance / texelsPerDocumentUnit);

		var silhouette = paths[0];
		var silhouetteColor = silhouette.Fill;
		var highlight = new List<VectorPath>();
		var detail = new List<VectorPath>();
		ColorRGBA? highlightColor = null;
		for (var i = 1; i < paths.Count; i++)
		{
			var path = paths[i];
			if (SameColor(path.Fill, silhouetteColor))
			{
				if (highlight.Count == 0)
				{
					throw new InvalidDataException(
						"The logo has a path in the silhouette colour before any highlight; merge it into the silhouette.");
				}

				detail.Add(path.Path);
				continue;
			}

			if (highlightColor is { } existing &&
			    SameColor(existing, path.Fill) == false)
			{
				throw new InvalidDataException("The logo uses more than two colours; the splash supports a silhouette and a highlight.");
			}

			highlightColor = path.Fill;
			highlight.Add(path.Path);
		}

		if (highlightColor is null)
		{
			throw new InvalidDataException("The logo needs at least one path in a highlight colour.");
		}

		// Texel space: the logo's bounds inset by the distance range on every side.
		var margin = LogoMaxDistance;
		var width = (int)MathF.Ceiling(documentSize.X * texelsPerDocumentUnit + 2.0f * margin);
		var height = (int)MathF.Ceiling(documentSize.Y * texelsPerDocumentUnit + 2.0f * margin);
		VectorPath ToTexels(VectorPath path) => new(
			path.Contours
				.Select(contour => contour
					.Select(point => (point - documentMin) * texelsPerDocumentUnit + new Vector2(margin))
					.ToArray())
				.ToArray(),
			path.FillRule);

		var silhouetteTexels = ToTexels(silhouette.Path);
		var tracedContour = silhouetteTexels.Contours
			.OrderByDescending(contour => contour.Length)
			.First();
		var silhouetteField = DistanceFieldBaker.BakeSignedDistance([silhouetteTexels], width, height, LogoMaxDistance);
		var highlightField = DistanceFieldBaker.BakeSignedDistance(
			highlight.Select(ToTexels).ToArray(),
			width,
			height,
			LogoMaxDistance);
		var detailField = DistanceFieldBaker.BakeSignedDistance(
			detail.Select(ToTexels).ToArray(),
			width,
			height,
			LogoMaxDistance);
		var traceField = DistanceFieldBaker.BakeMirroredTraceParameter(tracedContour, width, height, LogoMaxDistance);

		var texels = PackHalves(width * height, silhouetteField, highlightField, detailField, traceField);
		var size = new Vector2(width, height) / TexelsPerUnit;
		var layer = new SplashScreenLayer
		{
			Center = Vector2.Zero,
			Size = size,
			Width = width,
			Height = height,
			Format = TextureFormat.Rgba16Float,
			Texels = texels
		};

		return new BakedLogo(layer, -LogoHeight * 0.5f, LogoHeight * 0.5f, silhouetteColor, highlightColor.Value);
	}

	private static BakedText BakeText(string fontPath, float logoBottom)
	{
		using var blob = Blob.FromFile(fontPath);
		using var face = new Face(blob, 0);
		if (face.GlyphCount == 0 || face.UnitsPerEm == 0)
		{
			throw new InvalidDataException($"The splash font '{fontPath}' has no usable glyphs.");
		}

		using var font = new Font(face);
		var unitsPerEm = (int)face.UnitsPerEm;
		font.SetFunctionsOpenType();
		font.SetScale(unitsPerEm, unitsPerEm);
		SetWeight(face, font, FontWeight);

		var poweredBy = LayOutLine(font, unitsPerEm, PoweredByText, PoweredBySize);
		var wordmark = LayOutLine(font, unitsPerEm, WordmarkText, WordmarkSize);

		// Place each line by its ink, centred horizontally: "Powered by" hangs below the logo, and the wordmark
		// below the descender of its "y".
		var poweredByOffset = new Vector2(
			-(poweredBy.InkMin.X + poweredBy.InkMax.X) * 0.5f,
			logoBottom + LogoToPoweredByGap - poweredBy.InkMin.Y);
		var poweredByBottom = poweredBy.InkMax.Y + poweredByOffset.Y;
		var wordmarkOffset = new Vector2(
			-(wordmark.InkMin.X + wordmark.InkMax.X) * 0.5f,
			poweredByBottom + PoweredByToWordmarkGap - wordmark.InkMin.Y);
		var poweredByPaths = Translate(poweredBy.Glyphs, poweredByOffset);
		var wordmarkPaths = Translate(wordmark.Glyphs, wordmarkOffset);

		var (inkMin, inkMax) = GetBounds(poweredByPaths.Concat(wordmarkPaths).SelectMany(path => path.Contours));
		var margin = TextMaxDistance / TexelsPerUnit;
		var origin = inkMin - new Vector2(margin);
		var width = (int)MathF.Ceiling((inkMax.X - inkMin.X + 2.0f * margin) * TexelsPerUnit);
		var height = (int)MathF.Ceiling((inkMax.Y - inkMin.Y + 2.0f * margin) * TexelsPerUnit);
		VectorPath ToTexels(VectorPath path) => new(
			path.Contours
				.Select(contour => contour.Select(point => (point - origin) * TexelsPerUnit).ToArray())
				.ToArray(),
			path.FillRule);

		var poweredByField = DistanceFieldBaker.BakeSignedDistance(
			poweredByPaths.Select(ToTexels).ToArray(),
			width,
			height,
			TextMaxDistance);
		var wordmarkField = DistanceFieldBaker.BakeSignedDistance(
			wordmarkPaths.Select(ToTexels).ToArray(),
			width,
			height,
			TextMaxDistance);

		var size = new Vector2(width, height) / TexelsPerUnit;
		var layer = new SplashScreenLayer
		{
			Center = origin + size * 0.5f,
			Size = size,
			Width = width,
			Height = height,
			Format = TextureFormat.Rg16Float,
			Texels = PackHalves(width * height, poweredByField, wordmarkField)
		};

		return new BakedText(layer, wordmarkOffset.Y + wordmark.InkMax.Y);
	}

	private sealed record LaidOutLine(IReadOnlyList<VectorPath> Glyphs, Vector2 InkMin, Vector2 InkMax);

	/// <summary>
	/// Shapes <paramref name="text"/> and returns its glyph outlines in reference units with the pen starting at
	/// the origin on the baseline, Y down.
	/// </summary>
	private static LaidOutLine LayOutLine(Font font, int unitsPerEm, string text, float fontSize)
	{
		using var buffer = new Buffer();
		buffer.AddUtf16(text);
		buffer.GuessSegmentProperties();
		font.Shape(buffer);

		var infos = buffer.GlyphInfos;
		var positions = buffer.GlyphPositions;
		var unitsToReference = fontSize / unitsPerEm;
		// The outline reader returns em units; the tolerance is a fraction of a texel at this size.
		var emTolerance = FlatteningTolerance / (fontSize * TexelsPerUnit);
		var glyphs = new List<VectorPath>();
		var penX = 0.0f;
		for (var i = 0; i < infos.Length; i++)
		{
			using var reader = new HarfBuzzOutlineReader(unitsPerEm);
			var shape = reader.Read(font, infos[i].Codepoint);
			var originX = penX + positions[i].XOffset * unitsToReference;
			var originY = -positions[i].YOffset * unitsToReference;
			var contours = new List<Vector2[]>();
			foreach (var contour in shape.Contours)
			{
				var points = FlattenContour(contour, emTolerance);
				if (points.Count < 3)
				{
					continue;
				}

				contours.Add(points
					.Select(point => new Vector2(originX + point.X * fontSize, originY - point.Y * fontSize))
					.ToArray());
			}

			if (contours.Count > 0)
			{
				// TrueType and CFF outlines fill by the nonzero rule; variable fonts keep overlapping contours.
				glyphs.Add(new VectorPath(contours, VectorFillRule.NonZero));
			}

			penX += positions[i].XAdvance * unitsToReference;
		}

		if (glyphs.Count == 0)
		{
			throw new InvalidDataException($"The splash font has no outlines for '{text}'.");
		}

		var (inkMin, inkMax) = GetBounds(glyphs.SelectMany(glyph => glyph.Contours));

		return new LaidOutLine(glyphs, inkMin, inkMax);
	}

	private static List<Vector2> FlattenContour(Contour contour, float tolerance)
	{
		var points = new List<Vector2>();
		foreach (var edge in contour.Edges)
		{
			var start = edge.GetPoint(0.0);
			if (points.Count == 0)
			{
				points.Add(start);
			}

			FlattenEdge(edge, 0.0, 1.0, start, edge.GetPoint(1.0), tolerance, 0, points);
		}

		if (points.Count > 1 &&
		    Vector2.DistanceSquared(points[0], points[^1]) <= 1e-12f)
		{
			points.RemoveAt(points.Count - 1);
		}

		return points;
	}

	private static void FlattenEdge(
		EdgeSegment edge,
		double t0,
		double t1,
		Vector2 a,
		Vector2 b,
		float tolerance,
		int depth,
		List<Vector2> points)
	{
		var middle = (t0 + t1) * 0.5;
		var m = edge.GetPoint(middle);
		var q1 = edge.GetPoint((t0 + middle) * 0.5);
		var q3 = edge.GetPoint((middle + t1) * 0.5);
		if (depth < 16 &&
		    (DistanceToSegment(m, a, b) > tolerance ||
		     DistanceToSegment(q1, a, b) > tolerance ||
		     DistanceToSegment(q3, a, b) > tolerance))
		{
			FlattenEdge(edge, t0, middle, a, m, tolerance, depth + 1, points);
			FlattenEdge(edge, middle, t1, m, b, tolerance, depth + 1, points);
			return;
		}

		points.Add(b);
	}

	private static float DistanceToSegment(Vector2 point, Vector2 a, Vector2 b)
	{
		var direction = b - a;
		var lengthSquared = direction.LengthSquared();
		var t = lengthSquared <= 0.0f
			? 0.0f
			: Math.Clamp(Vector2.Dot(point - a, direction) / lengthSquared, 0.0f, 1.0f);

		return Vector2.Distance(point, a + direction * t);
	}

	private static IReadOnlyList<VectorPath> Translate(IReadOnlyList<VectorPath> paths, Vector2 offset) =>
		paths
			.Select(path => new VectorPath(
				path.Contours.Select(contour => contour.Select(point => point + offset).ToArray()).ToArray(),
				path.FillRule))
			.ToArray();

	private static (Vector2 Min, Vector2 Max) GetBounds(IEnumerable<Vector2[]> contours)
	{
		var min = new Vector2(float.PositiveInfinity);
		var max = new Vector2(float.NegativeInfinity);
		foreach (var contour in contours)
		{
			foreach (var point in contour)
			{
				min = Vector2.Min(min, point);
				max = Vector2.Max(max, point);
			}
		}

		if (float.IsFinite(min.X) == false)
		{
			throw new InvalidDataException("The splash source has no outline points.");
		}

		return (min, max);
	}

	private static bool SameColor(ColorRGBA a, ColorRGBA b) =>
		MathF.Abs(a.R - b.R) < 1e-3f &&
		MathF.Abs(a.G - b.G) < 1e-3f &&
		MathF.Abs(a.B - b.B) < 1e-3f;

	/// <summary>Interleaves the channels as little-endian half floats, one texel after another.</summary>
	private static byte[] PackHalves(int texelCount, params float[][] channels)
	{
		var bytes = new byte[texelCount * channels.Length * 2];
		var span = bytes.AsSpan();
		for (var texel = 0; texel < texelCount; texel++)
		{
			for (var channel = 0; channel < channels.Length; channel++)
			{
				var offset = (texel * channels.Length + channel) * 2;
				System.Buffers.Binary.BinaryPrimitives.WriteHalfLittleEndian(
					span.Slice(offset, 2),
					(Half)channels[channel][texel]);
			}
		}

		return bytes;
	}

	/// <summary>Selects a weight on the font's <c>wght</c> axis, which shaping and outlines then follow.</summary>
	private static void SetWeight(Face face, Font font, float weight)
	{
		const uint tag = ('w' << 24) | ('g' << 16) | ('h' << 8) | 't';
		if (hb_ot_var_find_axis_info(face.Handle, tag, out var axis) == 0)
		{
			throw new InvalidDataException("The splash font is not a variable font with a weight axis.");
		}

		if (weight < axis.MinValue || weight > axis.MaxValue)
		{
			throw new InvalidDataException(
				$"The splash font's weight axis runs {axis.MinValue}-{axis.MaxValue}; it cannot be set to {weight}.");
		}

		var variation = new HarfBuzzVariation
		{
			Tag = tag,
			Value = weight
		};
		hb_font_set_variations(font.Handle, ref variation, 1);
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct HarfBuzzVariation
	{
		public uint Tag;
		public float Value;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct HarfBuzzAxisInfo
	{
		public uint AxisIndex;
		public uint Tag;
		public uint NameId;
		public int Flags;
		public float MinValue;
		public float DefaultValue;
		public float MaxValue;
		public uint Reserved;
	}

	private const string HarfBuzzLibrary = "libHarfBuzzSharp";

	[DllImport(HarfBuzzLibrary, CallingConvention = CallingConvention.Cdecl)]
	private static extern void hb_font_set_variations(nint font, ref HarfBuzzVariation variations, uint length);

	/// <returns>HarfBuzz's <c>hb_bool_t</c>: nonzero when the face has the axis.</returns>
	[DllImport(HarfBuzzLibrary, CallingConvention = CallingConvention.Cdecl)]
	private static extern int hb_ot_var_find_axis_info(nint face, uint tag, out HarfBuzzAxisInfo axisInfo);
}

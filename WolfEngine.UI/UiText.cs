using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using HarfBuzzSharp;
using WolfEngine.AssetPipeline;
using WolfEngine.Profiling;
using WolfEngine.Rendering;
using Buffer = HarfBuzzSharp.Buffer;

namespace WolfEngine.UI;

internal sealed class UiFont : IDisposable
{
	private readonly Blob _blob;
	private readonly Face _face;
	private readonly Font _font;
	private readonly Buffer _buffer = new();
	private readonly Dictionary<string, ShapedGlyph[]> _shapes = new(StringComparer.Ordinal);
	private readonly Queue<string> _order = new();
	private readonly HashSet<uint> _missing = [];
	private int _cachedGlyphs;
	public FontArtifact Data { get; }
	public Dictionary<uint, FontGlyph> Glyphs { get; }
	public Texture Atlas { get; }
	public UiFont(FontArtifact data)
	{
		Data = data;
		Glyphs = data.Glyphs.ToDictionary(g => g.Id);
		Atlas = new Texture(data.Metrics.Name + " UI font atlas", data.Metrics.Width, data.Metrics.Height, false,
			TextureFormat.Rgba8Unorm, [new TextureMipData(data.Metrics.Width, data.Metrics.Height, data.PixelsRgba)]);
		// FromStream in HarfBuzzSharp 14.2 wraps a temporarily pinned array as
		// ReadOnly. Copy while pinned instead, so native shaping survives GC moves.
		var pin = GCHandle.Alloc(data.FontData, GCHandleType.Pinned);
		try { _blob = new Blob(pin.AddrOfPinnedObject(), data.FontData.Length, MemoryMode.Duplicate); }
		finally { pin.Free(); }
		_face = new Face(_blob, 0);
		_font = new Font(_face);
		_font.SetFunctionsOpenType();
		_font.SetScale(data.Metrics.UnitsPerEm, data.Metrics.UnitsPerEm);
	}

	public ShapedGlyph[] Shape(string text)
	{
		lock (_shapes)
		{
			if (_shapes.TryGetValue(text, out var cached)) return cached;
			using var measure = FrameProfiler.Instance.Measure("Gameplay UI.Text Shape");
			_buffer.ClearContents();
			_buffer.AddUtf16(text);
			_buffer.GuessSegmentProperties();
			_font.Shape(_buffer);
			var infos = _buffer.GlyphInfos;
			var positions = _buffer.GlyphPositions;
			var result = new ShapedGlyph[infos.Length];
			var units = (float)Data.Metrics.UnitsPerEm;
			for (var i = 0; i < result.Length; i++)
			{
				var id = infos[i].Codepoint;
				if (id == 0 && _missing.Add(uint.MaxValue)) Console.Error.WriteLine($"[Gameplay UI] Font '{Data.Metrics.Name}' does not support a requested character; using .notdef.");
				var baked = Glyphs.TryGetValue(id, out var glyph);
				if (!baked)
				{
					if (_missing.Add(id)) Console.Error.WriteLine($"[Gameplay UI] Font '{Data.Metrics.Name}' has no baked glyph {id}; using .notdef. Expand import coverage.");
					glyph = Glyphs[0];
				}
				var p = positions[i];
				result[i] = new ShapedGlyph(glyph!, baked ? p.XAdvance / units : glyph!.Advance,
					new Vector2(p.XOffset / units, -p.YOffset / units));
			}
			// Both shape and layout caches are bounded; dynamic counters cannot grow them forever.
			while (_shapes.Count > 0 && (_shapes.Count >= 16384 || _cachedGlyphs + result.Length > 131072))
			{
				var remove = _order.Dequeue(); _cachedGlyphs -= _shapes[remove].Length; _shapes.Remove(remove);
			}
			if (result.Length <= 131072) { _shapes.Add(text, result); _order.Enqueue(text); _cachedGlyphs += result.Length; }
			return result;
		}
	}
	public void Dispose() { _buffer.Dispose(); _font.Dispose(); _face.Dispose(); _blob.Dispose(); }
}

internal readonly record struct ShapedGlyph(FontGlyph Glyph, float Advance, Vector2 Offset);
internal readonly record struct TextGlyph(FontGlyph Glyph, Vector2 Origin);
internal sealed record UiTextLayout(UiFont? Font, TextGlyph[] Glyphs, float Width, float Height);

internal sealed class UiFontCatalog(IFontContentProvider? provider) : IDisposable
{
	private readonly Dictionary<(Guid, string), UiFont> _fonts = [];
	public UiFont? Resolve(string source)
	{
		if (provider is null || !provider.TryResolve(source, out var id, out var revision)) return null;
		lock (_fonts)
		{
			if (_fonts.TryGetValue((id, revision), out var font)) return font;
			using var stream = provider.Open(id);
			font = new UiFont(FontArtifactSerializer.Read(stream));
			_fonts.Add((id, revision), font);
			return font;
		}
	}
	public void Dispose() { foreach (var font in _fonts.Values) font.Dispose(); _fonts.Clear(); }
}

internal sealed class UiTextService(UiFontCatalog? catalog = null)
{
	public int Revision { get; private set; }
	private readonly record struct Key(UiFont? Font, string Text, float Size, float LineHeight, float Width, bool NoWrap);
	private readonly Dictionary<string, UiFont?> _families = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, UiFont?> _resolvedFamilies = new(StringComparer.Ordinal);
	private readonly Dictionary<Key, UiTextLayout> _layouts = [];
	private readonly Queue<Key> _order = new();
	private readonly HashSet<string> _diagnostics = new(StringComparer.Ordinal);
	private int _cachedGlyphs;
	public bool SetFonts(IReadOnlyDictionary<string, string> sources)
	{
		var changed = false;
		foreach (var (family, source) in sources)
		{
			var font = catalog?.Resolve(source);
			changed |= !_families.TryGetValue(family, out var old) || !ReferenceEquals(old, font);
			_families[family] = font;
			if (font is null && _diagnostics.Add(source)) Console.Error.WriteLine($"[Gameplay UI] Missing cooked font '{source}'. Text is omitted until the source asset is imported.");
		}
		if (changed) { _resolvedFamilies.Clear(); Revision++; }
		return changed;
	}

	public UiTextLayout Layout(string text, ComputedStyle style, float width = float.PositiveInfinity)
	{
		if (!_resolvedFamilies.TryGetValue(style.FontFamily, out var font))
		{
			foreach (var family in style.FontFamily.Split(','))
				if (_families.TryGetValue(family.Trim().Trim('\'', '"'), out font) && font is not null) break;
			_resolvedFamilies[style.FontFamily] = font;
		}
		width = style.NoWrap || !float.IsFinite(width) ? float.PositiveInfinity : Math.Max(0, MathF.Round(width * 64) / 64);
		var resolvedLineHeight = style.LineHeightPixels ? style.LineHeight / style.FontSize : style.LineHeight;
		var key = new Key(font, text, style.FontSize, resolvedLineHeight, width, style.NoWrap);
		if (_layouts.TryGetValue(key, out var cached)) return cached;
		using var measure = FrameProfiler.Instance.Measure("Gameplay UI.Text Measure");
		var result = BuildLayout(font, text, style, width, resolvedLineHeight);
		while (_layouts.Count > 0 && (_layouts.Count >= 16384 || _cachedGlyphs + result.Glyphs.Length > 131072))
		{
			var remove = _order.Dequeue(); _cachedGlyphs -= _layouts[remove].Glyphs.Length; _layouts.Remove(remove);
		}
		if (result.Glyphs.Length <= 131072) { _layouts.Add(key, result); _order.Enqueue(key); _cachedGlyphs += result.Glyphs.Length; }
		return result;
	}

	private static UiTextLayout BuildLayout(UiFont? font, string text, ComputedStyle style, float width, float resolvedLineHeight)
	{
		var glyphs = new List<TextGlyph>();
		var size = style.FontSize;
		var lineHeight = (resolvedLineHeight > 0 ? resolvedLineHeight : font?.Data.Metrics.LineHeight ?? 1.2f) * size;
		var baseline = (lineHeight - (font?.Data.Metrics.Ascender - font?.Data.Metrics.Descender ?? 1) * size) / 2 +
			(font?.Data.Metrics.Ascender ?? 0.8f) * size;
		var pen = 0f; var line = 0; var maximum = 0f;
		foreach (Match part in Regex.Matches(text.Replace("\r\n", "\n"), @"[^\s]+|[^\S\n]+|\n"))
		{
			var token = part.Value;
			if (token == "\n") { NewLine(); continue; }
			var shaped = font?.Shape(token);
			var tokenWidth = shaped?.Sum(g => g.Advance * size) ?? token.Length * size * 0.6f;
			var space = char.IsWhiteSpace(token[0]);
			if (!style.NoWrap && pen > 0 && pen + tokenWidth > width) { NewLine(); if (space) continue; }
			if (shaped is null) { pen += tokenWidth; continue; }
			foreach (var glyph in shaped)
			{
				var advance = glyph.Advance * size;
				if (!style.NoWrap && pen > 0 && pen + advance > width && !space) NewLine();
				if (glyph.Glyph.AtlasWidth > 0) glyphs.Add(new TextGlyph(glyph.Glyph,
					new Vector2(pen, baseline + line * lineHeight) + glyph.Offset * size));
				pen += advance;
			}
		}
		maximum = Math.Max(maximum, pen);
		return new UiTextLayout(font, glyphs.ToArray(), maximum, text.Length == 0 ? 0 : (line + 1) * lineHeight);
		void NewLine() { maximum = Math.Max(maximum, pen); pen = 0; line++; }
	}
}

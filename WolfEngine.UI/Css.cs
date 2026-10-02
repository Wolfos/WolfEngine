using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using WolfEngine.Rendering;

namespace WolfEngine.UI;

internal enum UiLengthUnit { Auto, Pixels, Percent, ViewWidth, ViewHeight, Em, Rem }
internal enum UiTextAlign { Left, Center, Right }

internal readonly record struct UiLength(float Value, UiLengthUnit Unit)
{
	public static UiLength Auto => new(0, UiLengthUnit.Auto);
	public static UiLength Pixels(float value) => new(value, UiLengthUnit.Pixels);
}

internal sealed record ComputedStyle
{
	public static ComputedStyle Default { get; } = new();
	public bool Display { get; init; } = true;
	public bool Row { get; init; }
	public bool Wrap { get; init; }
	public bool Absolute { get; init; }
	public UiLength Width { get; init; } = UiLength.Auto;
	public UiLength Height { get; init; } = UiLength.Auto;
	public UiLength MinWidth { get; init; } = UiLength.Auto;
	public UiLength MinHeight { get; init; } = UiLength.Auto;
	public UiLength MaxWidth { get; init; } = UiLength.Auto;
	public UiLength MaxHeight { get; init; } = UiLength.Auto;
	public UiLength Left { get; init; } = UiLength.Auto;
	public UiLength Top { get; init; } = UiLength.Auto;
	public UiLength Right { get; init; } = UiLength.Auto;
	public UiLength Bottom { get; init; } = UiLength.Auto;
	public bool ClipOverflow { get; init; }
	public bool PointerEvents { get; init; } = true;
	public float FlexGrow { get; init; }
	public float FlexShrink { get; init; } = 1;
	public float Gap { get; init; }
	public UiEdges Padding { get; init; } = UiEdges.Zero;
	public UiEdges Margin { get; init; } = UiEdges.Zero;
	public string JustifyContent { get; init; } = "flex-start";
	public string AlignItems { get; init; } = "stretch";
	public ColorRGBA Background { get; init; } = new(0, 0, 0, 0);
	public ColorRGBA Color { get; init; } = ColorRGBA.White;
	public float Opacity { get; init; } = 1;
	public float FontSize { get; init; } = 16;
	public float RootFontSize { get; init; } = 16;
	public string FontFamily { get; init; } = "";
	public float LineHeight { get; init; }
	public bool LineHeightPixels { get; init; }
	public bool NoWrap { get; init; }
	public UiTextAlign TextAlign { get; init; }
	public float BorderRadius { get; init; }
}

internal sealed class CssStyleSheet
{
	private readonly record struct Declaration(string Name, string Value);
	private readonly record struct SimpleSelector(string? Tag, string? Id, string? Class, int States);
	// Only inherited values affect child declarations. Vector4 avoids ColorRGBA's
	// default boxed ValueType hash in the per-node, per-frame cache lookup.
	private readonly record struct InheritedStyleKey(float FontSize, float RootFontSize, string FontFamily,
		float LineHeight, bool LineHeightPixels, bool NoWrap, UiTextAlign TextAlign, bool PointerEvents, float Opacity, Vector4 Color);
	private readonly record struct StyleCacheKey(
		string Name,
		string? Id,
		string? Classes,
		string? InlineStyle,
		string? ParentName,
		string? ParentId,
		string? ParentClasses,
		bool ParentIsRoot,
		int State,
		int ParentState,
		InheritedStyleKey Inherited);
	private sealed record Rule(
		SimpleSelector Target,
		SimpleSelector? Parent,
		Declaration[] Declarations,
		int Specificity,
		int Order);

	private struct StyleAccumulator
	{
		public bool Display;
		public bool Row;
		public bool Wrap;
		public bool Absolute;
		public UiLength Width;
		public UiLength Height;
		public UiLength MinWidth;
		public UiLength MinHeight;
		public UiLength MaxWidth;
		public UiLength MaxHeight;
		public UiLength Left;
		public UiLength Top;
		public UiLength Right;
		public UiLength Bottom;
		public bool ClipOverflow;
		public bool PointerEvents;
		public float FlexGrow;
		public float FlexShrink;
		public float Gap;
		public UiEdges Padding;
		public UiEdges Margin;
		public string JustifyContent;
		public string AlignItems;
		public ColorRGBA Background;
		public ColorRGBA Color;
		public float Opacity;
		public float FontSize;
		public float RootFontSize;
		public UiLength? FontSizeValue;
		public string FontFamily;
		public float LineHeight;
		public bool LineHeightPixels;
		public bool NoWrap;
		public UiTextAlign TextAlign;
		private readonly UiTextAlign _inheritedTextAlign;
		public float BorderRadius;

		public StyleAccumulator(ComputedStyle inherited)
		{
			Display = true;
			Row = false;
			Wrap = false;
			Absolute = false;
			Width = UiLength.Auto;
			Height = UiLength.Auto;
			MinWidth = UiLength.Auto;
			MinHeight = UiLength.Auto;
			MaxWidth = UiLength.Auto;
			MaxHeight = UiLength.Auto;
			Left = UiLength.Auto;
			Top = UiLength.Auto;
			Right = UiLength.Auto;
			Bottom = UiLength.Auto;
			ClipOverflow = false;
			PointerEvents = inherited.PointerEvents;
			FlexGrow = 0;
			FlexShrink = 1;
			Gap = 0;
			Padding = UiEdges.Zero;
			Margin = UiEdges.Zero;
			JustifyContent = "flex-start";
			AlignItems = "stretch";
			Background = new ColorRGBA(0, 0, 0, 0);
			Color = inherited.Color;
			Opacity = inherited.Opacity;
			FontSize = inherited.FontSize;
			RootFontSize = inherited.RootFontSize;
			FontSizeValue = null;
			FontFamily = inherited.FontFamily;
			LineHeight = inherited.LineHeight;
			LineHeightPixels = inherited.LineHeightPixels;
			NoWrap = inherited.NoWrap;
			TextAlign = _inheritedTextAlign = inherited.TextAlign;
			BorderRadius = 0;
		}

		public void Apply(in Declaration declaration) => Apply(declaration.Name, declaration.Value);

		public void Apply(string name, string value)
		{
			switch (name)
			{
				case "display": Display = !value.Equals("none", StringComparison.OrdinalIgnoreCase); break;
				case "flex-direction": Row = value.StartsWith("row", StringComparison.OrdinalIgnoreCase); break;
				case "flex-wrap": Wrap = value.StartsWith("wrap", StringComparison.OrdinalIgnoreCase); break;
				case "position": Absolute = value.Equals("absolute", StringComparison.OrdinalIgnoreCase); break;
				case "width": Width = Length(value); break;
				case "height": Height = Length(value); break;
				case "min-width": MinWidth = Length(value); break;
				case "min-height": MinHeight = Length(value); break;
				case "max-width": MaxWidth = Length(value); break;
				case "max-height": MaxHeight = Length(value); break;
				case "left": Left = Length(value); break;
				case "top": Top = Length(value); break;
				case "right": Right = Length(value); break;
				case "bottom": Bottom = Length(value); break;
				case "inset": var inset = UiEdges.Parse(value); Top = inset.Top; Right = inset.Right; Bottom = inset.Bottom; Left = inset.Left; break;
				case "overflow": ClipOverflow = value == "hidden"; break;
				case "flex-grow": FlexGrow = Number(value, FlexGrow); break;
				case "flex-shrink": FlexShrink = Number(value, FlexShrink); break;
				case "gap": Gap = Number(value, Gap); break;
				case "padding": Padding = UiEdges.Parse(value); break;
				case "padding-top": Padding = Padding with { Top = Length(value) }; break;
				case "padding-right": Padding = Padding with { Right = Length(value) }; break;
				case "padding-bottom": Padding = Padding with { Bottom = Length(value) }; break;
				case "padding-left": Padding = Padding with { Left = Length(value) }; break;
				case "margin": Margin = UiEdges.Parse(value); break;
				case "margin-top": Margin = Margin with { Top = Length(value) }; break;
				case "margin-right": Margin = Margin with { Right = Length(value) }; break;
				case "margin-bottom": Margin = Margin with { Bottom = Length(value) }; break;
				case "margin-left": Margin = Margin with { Left = Length(value) }; break;
				case "justify-content": JustifyContent = value; break;
				case "align-items": AlignItems = value; break;
				case "background-color": Background = ParseColor(value, Background); break;
				case "color": Color = value.Equals("inherit", StringComparison.OrdinalIgnoreCase)
					? Color
					: ParseColor(value, Color); break;
				case "opacity": Opacity = Math.Clamp(Number(value, Opacity), 0, 1); break;
				case "font-size": FontSizeValue = value == "inherit" ? null : Length(value); break;
				case "font-family": if (!value.Equals("inherit", StringComparison.OrdinalIgnoreCase)) FontFamily = value; break;
				case "line-height":
					LineHeight = value == "normal" ? 0 : Math.Max(0, Number(value, LineHeight));
					LineHeightPixels = value.EndsWith("px", StringComparison.OrdinalIgnoreCase); break;
				case "white-space": NoWrap = value is "nowrap" or "pre"; break;
				case "pointer-events": PointerEvents = value == "auto"; break;
				case "text-align": TextAlign = value switch
				{
					"center" => UiTextAlign.Center,
					"right" => UiTextAlign.Right,
					"inherit" => _inheritedTextAlign,
					_ => UiTextAlign.Left
				}; break;
				case "border-radius": BorderRadius = Number(value, BorderRadius); break;
			}
		}

		public ComputedStyle Build(float vw, float vh, bool isRoot)
		{
			if (FontSizeValue is { } length) FontSize = UiCssValues.Resolve(length, vw, vh, FontSize, isRoot ? 16 : RootFontSize, FontSize);
			if (isRoot) RootFontSize = FontSize;
			return new()
		{
			Display = Display,
			Row = Row,
			Wrap = Wrap,
			Absolute = Absolute,
			Width = Width,
			Height = Height,
			MinWidth = MinWidth,
			MinHeight = MinHeight,
			MaxWidth = MaxWidth,
			MaxHeight = MaxHeight,
			Left = Left,
			Top = Top,
			Right = Right,
			Bottom = Bottom,
			ClipOverflow = ClipOverflow,
			PointerEvents = PointerEvents,
			FlexGrow = FlexGrow,
			FlexShrink = FlexShrink,
			Gap = Gap,
			Padding = Padding,
			Margin = Margin,
			JustifyContent = JustifyContent,
			AlignItems = AlignItems,
			Background = Background,
			Color = Color,
			Opacity = Opacity,
			FontSize = FontSize,
			RootFontSize = RootFontSize,
			FontFamily = FontFamily,
			LineHeight = LineHeight,
			LineHeightPixels = LineHeightPixels,
			NoWrap = NoWrap,
			TextAlign = TextAlign,
			BorderRadius = BorderRadius
		};
		}
	}

	private readonly Rule[] _rules;
	public IReadOnlyDictionary<string, string> FontSources { get; private init; } = new Dictionary<string, string>();
	private readonly Dictionary<StyleCacheKey, ComputedStyle> _styleCache = [];
	private readonly HashSet<(string Name, string Value)> _warned = [];
	private readonly List<string> _diagnostics = [];
	public IReadOnlyList<string> Diagnostics => _diagnostics;
	private float _viewportWidth = -1, _viewportHeight = -1;
	private ComputedStyle? _defaultStyle;

	private CssStyleSheet(List<Rule> rules)
	{
		rules.Sort(static (left, right) => left.Specificity != right.Specificity
			? left.Specificity.CompareTo(right.Specificity)
			: left.Order.CompareTo(right.Order));
		_rules = rules.ToArray();
	}

	public static CssStyleSheet Empty => new([]);

	public static CssStyleSheet Parse(string css)
	{
		if (string.IsNullOrWhiteSpace(css)) return Empty;
		css = Regex.Replace(css, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
		var rules = new List<Rule>();
		var order = 0;
		var fonts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var rejected = new List<Declaration>();
		foreach (Match match in Regex.Matches(css, @"(?<selector>[^{}]+)\{(?<body>[^{}]*)\}"))
		{
			var declarationList = new List<Declaration>();
			foreach (var source in match.Groups["body"].Value.Split(';', StringSplitOptions.RemoveEmptyEntries))
			{
				var separator = source.IndexOf(':');
				if (separator <= 0) continue;
				var name = source[..separator].Trim().ToLowerInvariant();
				var value = source[(separator + 1)..].Trim();
				declarationList.Add(new Declaration(name, name is "font-family" or "src" ? value : value.ToLowerInvariant()));
			}

			var declarations = declarationList.ToArray();
			if (match.Groups["selector"].Value.Trim().Equals("@font-face", StringComparison.OrdinalIgnoreCase))
			{
				var family = declarations.FirstOrDefault(d => d.Name == "font-family").Value?.Trim('\'', '"');
				var source = AssetPipeline.FontSourceReferences.Find("@font-face {" + match.Groups["body"].Value + "}").FirstOrDefault();
				if (string.IsNullOrWhiteSpace(family) || source is null) throw new FormatException("@font-face requires font-family and a project asset URL.");
				fonts[family] = source;
				foreach (var declaration in declarations) if (declaration.Name is not ("font-family" or "src")) rejected.Add(declaration);
				continue;
			}
			declarations = declarations.Where(d => { if (Valid(d.Name, d.Value)) return true; rejected.Add(d); return false; }).ToArray();
			foreach (var selectorSource in match.Groups["selector"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
			{
				var selector = selectorSource.Trim();
				var parts = selector.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
				if (parts.Length == 0) continue;
				var target = ParseSimpleSelector(parts[^1]);
				SimpleSelector? parent = parts.Length > 1 ? ParseSimpleSelector(parts[^2]) : null;
				rules.Add(new Rule(target, parent, declarations, Specificity(selector), order++));
			}
		}
		var sheet = new CssStyleSheet(rules) { FontSources = fonts };
		foreach (var declaration in rejected) sheet.Warn(declaration.Name, declaration.Value);
		return sheet;
	}

	public void Apply(UiNode root, float viewportWidth, float viewportHeight)
	{
		if (_viewportWidth != viewportWidth || _viewportHeight != viewportHeight)
		{
			_styleCache.Clear(); _viewportWidth = viewportWidth; _viewportHeight = viewportHeight;
		}
		_defaultStyle ??= ComputedStyle.Default with { FontFamily = FontSources.Keys.FirstOrDefault() ?? "" };
		ApplyNode(root, null, false, viewportWidth, viewportHeight);
	}

	private void ApplyNode(UiNode node, UiNode? parent, bool parentIsRoot, float vw, float vh)
	{
		var inherited = parent?.Style ?? _defaultStyle!;
		var inlineStyle = node.Attributes.TryGetValue("style", out var inlineValue) && inlineValue is not null
			? inlineValue.ToString()
			: null;
		var cacheKey = new StyleCacheKey(
			node.Name,
			node.Id,
			node.Classes,
			inlineStyle,
			parent?.Name,
			parent?.Id,
			parent?.Classes,
			parentIsRoot,
			node.InteractionState,
			parent?.InteractionState ?? 0,
			new InheritedStyleKey(inherited.FontSize, inherited.RootFontSize, inherited.FontFamily,
				inherited.LineHeight, inherited.LineHeightPixels, inherited.NoWrap, inherited.TextAlign, inherited.PointerEvents, inherited.Opacity,
				new Vector4(inherited.Color.R, inherited.Color.G, inherited.Color.B, inherited.Color.A)));
		var cacheable = inlineStyle is null;
		if (!cacheable || !_styleCache.TryGetValue(cacheKey, out var style))
		{
			var accumulator = new StyleAccumulator(inherited);
			for (var ruleIndex = 0; ruleIndex < _rules.Length; ruleIndex++)
			{
				var rule = _rules[ruleIndex];
				if (!Matches(node, parent, parentIsRoot, rule)) continue;
				for (var declarationIndex = 0; declarationIndex < rule.Declarations.Length; declarationIndex++)
					accumulator.Apply(rule.Declarations[declarationIndex]);
			}

			if (inlineStyle is not null) ApplyInline(inlineStyle, ref accumulator);
			style = accumulator.Build(vw, vh, parent is null);
			if (cacheable) _styleCache.Add(cacheKey, style);
		}

		node.Style = style;
		for (var i = 0; i < node.Children.Count; i++) ApplyNode(node.Children[i], node, parent is null, vw, vh);
	}

	private void ApplyInline(string inlineStyle, ref StyleAccumulator accumulator)
	{
		foreach (var source in inlineStyle.Split(';', StringSplitOptions.RemoveEmptyEntries))
		{
			var separator = source.IndexOf(':');
			if (separator <= 0) continue;
			var name = source[..separator].Trim().ToLowerInvariant();
			var value = source[(separator + 1)..].Trim();
			if (name != "font-family") value = value.ToLowerInvariant();
			if (Valid(name, value)) accumulator.Apply(name, value);
			else Warn(name, value);
		}
	}

	private void Warn(string name, string value)
	{
		if (!_warned.Add((name, value))) return;
		var message = $"[Gameplay UI CSS] Unsupported property or value '{name}: {value}'; declaration ignored.";
		_diagnostics.Add(message);
		Console.Error.WriteLine(message);
	}

	private static bool Valid(string name, string value)
	{
		bool LengthValue(bool auto, bool negative) => UiCssValues.TryLength(value, out var length) &&
			(length.Unit == UiLengthUnit.Auto ? auto : negative || length.Value >= 0);
		bool Edges(bool auto, bool negative)
		{
			var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
			return parts.Length is >= 1 and <= 4 && parts.All(p => UiCssValues.TryLength(p, out var length) &&
				(length.Unit == UiLengthUnit.Auto ? auto : negative || length.Value >= 0));
		}
		bool Scalar() => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && float.IsFinite(number) && number >= 0;
		switch (name)
		{
			case "display": return value is "flex" or "none";
			case "flex-direction": return value is "row" or "column";
			case "flex-wrap": return value is "wrap" or "nowrap";
			case "position": return value is "relative" or "absolute";
			case "overflow": return value is "hidden" or "visible";
			case "justify-content": return value is "flex-start" or "center" or "flex-end" or "space-between" or "space-around" or "space-evenly";
			case "align-items": return value is "stretch" or "flex-start" or "center" or "flex-end";
			case "width": case "height": case "min-width": case "min-height": case "max-width": case "max-height": return LengthValue(true, false);
			case "left": case "top": case "right": case "bottom": return LengthValue(true, true);
			case "inset": return Edges(true, true);
			case "padding": return Edges(false, false);
			case "margin": return Edges(true, true);
			case "padding-top": case "padding-right": case "padding-bottom": case "padding-left": return LengthValue(false, false);
			case "margin-top": case "margin-right": case "margin-bottom": case "margin-left": return LengthValue(true, true);
			case "font-size": return value == "inherit" || LengthValue(false, false);
			case "font-family": return value.Length > 0;
			case "flex-grow": case "flex-shrink": case "opacity": return Scalar();
			case "gap": case "border-radius": return LengthValue(false, false) && Length(value).Unit == UiLengthUnit.Pixels;
			case "line-height": return value == "normal" || Scalar() || value.EndsWith("px", StringComparison.Ordinal) && LengthValue(false, false);
			case "white-space": return value is "normal" or "nowrap" or "pre";
			case "pointer-events": return value is "auto" or "none";
			case "text-align": return value is "left" or "center" or "right" or "inherit";
			case "color": case "background-color":
				return value is "white" or "black" or "transparent" || name == "color" && value == "inherit" ||
					value.Length is 7 or 9 && value[0] == '#' && uint.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _);
			default: return false;
		}
	}

	private static float Number(string value, float fallback) =>
		float.TryParse(TrimUnit(value.AsSpan()), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
			? parsed
			: fallback;

	private static UiLength Length(string value) => UiCssValues.Length(value);

	private static ReadOnlySpan<char> TrimUnit(ReadOnlySpan<char> value)
	{
		value = value.Trim();
		var end = value.Length;
		while (end > 0 && value[end - 1] is 'p' or 'P' or 'x' or 'X' or '%' or 'v' or 'V' or 'w' or 'W' or
		       'h' or 'H' or 'e' or 'E' or 'm' or 'M' or 'r' or 'R') end--;
		return value[..end];
	}

	private static ColorRGBA ParseColor(string value, ColorRGBA fallback)
	{
		var span = value.AsSpan().Trim();
		if (span.Equals("transparent", StringComparison.OrdinalIgnoreCase)) return new ColorRGBA(0, 0, 0, 0);
		if (span.Equals("white", StringComparison.OrdinalIgnoreCase)) return ColorRGBA.White;
		if (span.Equals("black", StringComparison.OrdinalIgnoreCase)) return new ColorRGBA(0, 0, 0, 1);
		if (span.Length is 7 or 9 && span[0] == '#' &&
		    uint.TryParse(span[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgba))
		{
			if (span.Length == 7) rgba = (rgba << 8) | 0xff;
			return new ColorRGBA(((rgba >> 24) & 255) / 255f, ((rgba >> 16) & 255) / 255f,
				((rgba >> 8) & 255) / 255f, (rgba & 255) / 255f);
		}
		return fallback;
	}

	private static int Specificity(string selector)
	{
		var specificity = 1;
		for (var i = 0; i < selector.Length; i++)
			specificity += selector[i] == '#' ? 100 : selector[i] is '.' or ':' ? 10 : 0;
		return specificity;
	}

	private static bool Matches(UiNode node, UiNode? parent, bool parentIsRoot, Rule rule) =>
		(rule.Target.Tag != ":root" || parent is null) &&
		MatchesSimple(node, rule.Target) &&
		(rule.Parent is not { } parentSelector || parent is not null &&
			(parentSelector.Tag != ":root" || parentIsRoot) && MatchesSimple(parent, parentSelector));

	private static bool MatchesSimple(UiNode node, SimpleSelector selector)
	{
		if ((node.InteractionState & selector.States) != selector.States) return false;
		if (selector.Tag is { Length: > 0 } tag && tag is not ("*" or ":root") &&
		    !string.Equals(tag, node.Name, StringComparison.OrdinalIgnoreCase)) return false;
		if (selector.Id is not null && !string.Equals(selector.Id, node.Id, StringComparison.Ordinal)) return false;
		return selector.Class is null || ContainsClass(node.Classes, selector.Class);
	}

	private static SimpleSelector ParseSimpleSelector(string selector)
	{
		var states = 0;
		foreach (var (pseudo, bit) in new[] { (":hover", 1), (":active", 2), (":disabled", 4) })
		{
			if (selector.Contains(pseudo, StringComparison.Ordinal)) { states |= bit; selector = selector.Replace(pseudo, "", StringComparison.Ordinal); }
		}
		var idIndex = selector.IndexOf('#');
		var classIndex = selector.IndexOf('.');
		var tagEnd = selector.Length;
		if (idIndex >= 0) tagEnd = idIndex;
		if (classIndex >= 0 && classIndex < tagEnd) tagEnd = classIndex;
		var tag = tagEnd == 0 ? null : selector[..tagEnd];
		string? id = null;
		if (idIndex >= 0)
		{
			var end = classIndex > idIndex ? classIndex : selector.Length;
			id = selector[(idIndex + 1)..end];
		}
		var className = classIndex >= 0 ? selector[(classIndex + 1)..] : null;
		return new SimpleSelector(tag, id, className, states);
	}

	private static bool ContainsClass(string? classes, string required)
	{
		if (string.IsNullOrEmpty(classes)) return false;
		var span = classes.AsSpan();
		var position = 0;
		while (position < span.Length)
		{
			while (position < span.Length && char.IsWhiteSpace(span[position])) position++;
			var start = position;
			while (position < span.Length && !char.IsWhiteSpace(span[position])) position++;
			if (span[start..position].Equals(required, StringComparison.Ordinal)) return true;
		}
		return false;
	}
}

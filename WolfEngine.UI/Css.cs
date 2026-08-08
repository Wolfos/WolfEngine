using System.Globalization;
using System.Text.RegularExpressions;
using WolfEngine.Rendering;

namespace WolfEngine.UI;

internal enum UiLengthUnit { Auto, Pixels, Percent, ViewWidth, ViewHeight, Em, Rem }

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
	public float FlexGrow { get; init; }
	public float FlexShrink { get; init; } = 1;
	public float Gap { get; init; }
	public float Padding { get; init; }
	public float Margin { get; init; }
	public string JustifyContent { get; init; } = "flex-start";
	public string AlignItems { get; init; } = "stretch";
	public ColorRGBA Background { get; init; } = new(0, 0, 0, 0);
	public ColorRGBA Color { get; init; } = ColorRGBA.White;
	public float Opacity { get; init; } = 1;
	public float FontSize { get; init; } = 16;
	public float BorderRadius { get; init; }
}

internal sealed class CssStyleSheet
{
	private readonly record struct Declaration(string Name, string Value);
	private readonly record struct SimpleSelector(string? Tag, string? Id, string? Class);
	private readonly record struct StyleCacheKey(
		string Name,
		string? Id,
		string? Classes,
		string? InlineStyle,
		string? ParentName,
		string? ParentId,
		string? ParentClasses,
		ComputedStyle Inherited);
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
		public float FlexGrow;
		public float FlexShrink;
		public float Gap;
		public float Padding;
		public float Margin;
		public string JustifyContent;
		public string AlignItems;
		public ColorRGBA Background;
		public ColorRGBA Color;
		public float Opacity;
		public float FontSize;
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
			FlexGrow = 0;
			FlexShrink = 1;
			Gap = 0;
			Padding = 0;
			Margin = 0;
			JustifyContent = "flex-start";
			AlignItems = "stretch";
			Background = new ColorRGBA(0, 0, 0, 0);
			Color = inherited.Color;
			Opacity = inherited.Opacity;
			FontSize = inherited.FontSize;
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
				case "flex-grow": FlexGrow = Number(value, FlexGrow); break;
				case "flex-shrink": FlexShrink = Number(value, FlexShrink); break;
				case "gap": Gap = Number(value, Gap); break;
				case "padding": Padding = Number(value, Padding); break;
				case "margin": Margin = Number(value, Margin); break;
				case "justify-content": JustifyContent = value; break;
				case "align-items": AlignItems = value; break;
				case "background-color": Background = ParseColor(value, Background); break;
				case "color": Color = value.Equals("inherit", StringComparison.OrdinalIgnoreCase)
					? Color
					: ParseColor(value, Color); break;
				case "opacity": Opacity = Math.Clamp(Number(value, Opacity), 0, 1); break;
				case "font-size": FontSize = Number(value, FontSize); break;
				case "border-radius": BorderRadius = Number(value, BorderRadius); break;
			}
		}

		public readonly ComputedStyle Build() => new()
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
			BorderRadius = BorderRadius
		};
	}

	private readonly Rule[] _rules;
	private readonly Dictionary<StyleCacheKey, ComputedStyle> _styleCache = [];

	private CssStyleSheet(List<Rule> rules)
	{
		rules.Sort(static (left, right) => left.Specificity != right.Specificity
			? left.Specificity.CompareTo(right.Specificity)
			: left.Order.CompareTo(right.Order));
		_rules = rules.ToArray();
	}

	public static CssStyleSheet Empty { get; } = new([]);

	public static CssStyleSheet Parse(string css)
	{
		if (string.IsNullOrWhiteSpace(css)) return Empty;
		css = Regex.Replace(css, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
		var rules = new List<Rule>();
		var order = 0;
		foreach (Match match in Regex.Matches(css, @"(?<selector>[^{}]+)\{(?<body>[^{}]*)\}"))
		{
			var declarationList = new List<Declaration>();
			foreach (var source in match.Groups["body"].Value.Split(';', StringSplitOptions.RemoveEmptyEntries))
			{
				var separator = source.IndexOf(':');
				if (separator <= 0) continue;
				declarationList.Add(new Declaration(
					source[..separator].Trim().ToLowerInvariant(),
					source[(separator + 1)..].Trim()));
			}

			var declarations = declarationList.ToArray();
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
		return new CssStyleSheet(rules);
	}

	public void Apply(UiNode root, float viewportWidth, float viewportHeight) =>
		ApplyNode(root, null, viewportWidth, viewportHeight);

	private void ApplyNode(UiNode node, UiNode? parent, float vw, float vh)
	{
		var inherited = parent?.Style ?? ComputedStyle.Default;
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
			inherited);
		var cacheable = inlineStyle is null;
		if (!cacheable || !_styleCache.TryGetValue(cacheKey, out var style))
		{
			var accumulator = new StyleAccumulator(inherited);
			for (var ruleIndex = 0; ruleIndex < _rules.Length; ruleIndex++)
			{
				var rule = _rules[ruleIndex];
				if (!Matches(node, parent, rule)) continue;
				for (var declarationIndex = 0; declarationIndex < rule.Declarations.Length; declarationIndex++)
					accumulator.Apply(rule.Declarations[declarationIndex]);
			}

			if (inlineStyle is not null) ApplyInline(inlineStyle, ref accumulator);
			style = accumulator.Build();
			if (cacheable) _styleCache.Add(cacheKey, style);
		}

		node.Style = style;
		for (var i = 0; i < node.Children.Count; i++) ApplyNode(node.Children[i], node, vw, vh);
	}

	private static void ApplyInline(string inlineStyle, ref StyleAccumulator accumulator)
	{
		foreach (var source in inlineStyle.Split(';', StringSplitOptions.RemoveEmptyEntries))
		{
			var separator = source.IndexOf(':');
			if (separator <= 0) continue;
			accumulator.Apply(source[..separator].Trim().ToLowerInvariant(), source[(separator + 1)..].Trim());
		}
	}

	private static float Number(string value, float fallback) =>
		float.TryParse(TrimUnit(value.AsSpan()), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
			? parsed
			: fallback;

	private static UiLength Length(string value)
	{
		var span = value.AsSpan().Trim();
		if (span.Equals("auto", StringComparison.OrdinalIgnoreCase)) return UiLength.Auto;
		var unit = span.EndsWith("%", StringComparison.Ordinal) ? UiLengthUnit.Percent :
			span.EndsWith("vw", StringComparison.OrdinalIgnoreCase) ? UiLengthUnit.ViewWidth :
			span.EndsWith("vh", StringComparison.OrdinalIgnoreCase) ? UiLengthUnit.ViewHeight :
			span.EndsWith("rem", StringComparison.OrdinalIgnoreCase) ? UiLengthUnit.Rem :
			span.EndsWith("em", StringComparison.OrdinalIgnoreCase) ? UiLengthUnit.Em : UiLengthUnit.Pixels;
		return float.TryParse(TrimUnit(span), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
			? new UiLength(number, unit)
			: UiLength.Auto;
	}

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
			specificity += selector[i] == '#' ? 100 : selector[i] == '.' ? 10 : 0;
		return specificity;
	}

	private static bool Matches(UiNode node, UiNode? parent, Rule rule) =>
		MatchesSimple(node, rule.Target) &&
		(rule.Parent is not { } parentSelector || parent is not null && MatchesSimple(parent, parentSelector));

	private static bool MatchesSimple(UiNode node, SimpleSelector selector)
	{
		if (selector.Tag is { Length: > 0 } tag && tag != "*" &&
		    !string.Equals(tag, node.Name, StringComparison.OrdinalIgnoreCase)) return false;
		if (selector.Id is not null && !string.Equals(selector.Id, node.Id, StringComparison.Ordinal)) return false;
		return selector.Class is null || ContainsClass(node.Classes, selector.Class);
	}

	private static SimpleSelector ParseSimpleSelector(string selector)
	{
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
		return new SimpleSelector(tag, id, className);
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

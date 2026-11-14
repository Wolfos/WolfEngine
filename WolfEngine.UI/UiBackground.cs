using System.Numerics;

namespace WolfEngine.UI;

internal enum UiBackgroundSizeKind
{
	/// <summary>Each side is a length, a percentage of the box, or <c>auto</c>.</summary>
	Explicit,

	/// <summary>Scale to cover the whole box, cropping the overflow.</summary>
	Cover,

	/// <summary>Scale to fit entirely inside the box.</summary>
	Contain
}

/// <summary>A parsed <c>background-size</c>. The CSS initial value is <c>auto auto</c>: the texture's own size.</summary>
internal readonly record struct UiBackgroundSize(UiBackgroundSizeKind Kind, UiLength Width, UiLength Height)
{
	public static UiBackgroundSize Auto => new(UiBackgroundSizeKind.Explicit, UiLength.Auto, UiLength.Auto);
}

/// <summary>A parsed <c>background-position</c>. The CSS initial value is <c>0% 0%</c>: the top left.</summary>
internal readonly record struct UiBackgroundPosition(UiLength X, UiLength Y)
{
	public static UiBackgroundPosition TopLeft => new(new UiLength(0, UiLengthUnit.Percent), new UiLength(0, UiLengthUnit.Percent));
}

/// <summary>A parsed <c>background-repeat</c>. The CSS initial value is <c>repeat</c>: tile on both axes.</summary>
internal readonly record struct UiBackgroundRepeat(bool X, bool Y)
{
	public static UiBackgroundRepeat Both => new(true, true);
}

/// <summary>One quad of a background: where it lands and which part of the texture it shows.</summary>
internal readonly record struct UiBackgroundTile(Vector2 Min, Vector2 Max, Vector2 UvMin, Vector2 UvMax);

/// <summary>
/// The supported subset of CSS backgrounds: <c>background-image</c>, <c>-size</c>, <c>-position</c> and
/// <c>-repeat</c>, with the browser's initial values. There are no borders, so the box a background is positioned
/// in and painted over is the element's border box.
/// </summary>
internal static class UiBackgrounds
{
	/// <summary>
	/// A tiny tile on a large box would emit an unbounded number of quads. Past this many, the background is
	/// dropped rather than stalling the UI thread.
	/// </summary>
	public const int MaxTiles = 4096;

	/// <summary>
	/// <c>none</c>, or the reference inside <c>url(…)</c>: a project path such as <c>/Assets/UI/Panel.png</c> or an
	/// asset reference such as <c>asset:&lt;node id&gt;</c>.
	/// </summary>
	public static bool TryParseImage(string value, out string? reference)
	{
		reference = null;
		var text = value.Trim();

		if (text.Equals("none", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		if (text.StartsWith("url(", StringComparison.OrdinalIgnoreCase) == false || text.EndsWith(')') == false)
		{
			return false;
		}

		var inner = text[4..^1].Trim();
		if (inner.Length >= 2 &&
		    (inner[0] == '"' || inner[0] == '\'') &&
		    inner[^1] == inner[0])
		{
			inner = inner[1..^1];
		}

		if (inner.Length == 0)
		{
			return false;
		}

		reference = inner;
		return true;
	}

	public static bool TryParseSize(string value, out UiBackgroundSize size)
	{
		size = UiBackgroundSize.Auto;
		var parts = Split(value);

		if (parts.Length == 1 && parts[0] == "cover")
		{
			size = new UiBackgroundSize(UiBackgroundSizeKind.Cover, UiLength.Auto, UiLength.Auto);
			return true;
		}

		if (parts.Length == 1 && parts[0] == "contain")
		{
			size = new UiBackgroundSize(UiBackgroundSizeKind.Contain, UiLength.Auto, UiLength.Auto);
			return true;
		}

		if (parts.Length is < 1 or > 2)
		{
			return false;
		}

		if (TryParseSizeLength(parts[0], out var width) == false)
		{
			return false;
		}

		// One value sets the width; the height is auto, as in CSS.
		var height = UiLength.Auto;
		if (parts.Length == 2 && TryParseSizeLength(parts[1], out height) == false)
		{
			return false;
		}

		size = new UiBackgroundSize(UiBackgroundSizeKind.Explicit, width, height);
		return true;
	}

	/// <summary>
	/// One or two values: keywords (<c>left</c>, <c>center</c>, <c>right</c>, <c>top</c>, <c>bottom</c>),
	/// percentages or lengths. A single value positions the horizontal axis and centres the vertical one, except
	/// that <c>top</c> or <c>bottom</c> alone sets the vertical axis. Edge offsets such as <c>right 10px</c> are not
	/// supported.
	/// </summary>
	public static bool TryParsePosition(string value, out UiBackgroundPosition position)
	{
		position = UiBackgroundPosition.TopLeft;
		var parts = Split(value);
		var center = new UiLength(50, UiLengthUnit.Percent);

		if (parts.Length == 1)
		{
			if (parts[0] is "top" or "bottom")
			{
				position = new UiBackgroundPosition(center, Keyword(parts[0]));
				return true;
			}

			if (TryParseAxis(parts[0], horizontal: true, out var x) == false)
			{
				return false;
			}

			position = new UiBackgroundPosition(x, center);
			return true;
		}

		if (parts.Length != 2)
		{
			return false;
		}

		// Keyword pairs may come in either order, as in "top left".
		var first = parts[0];
		var second = parts[1];
		if (first is "top" or "bottom" || second is "left" or "right")
		{
			(first, second) = (second, first);
		}

		if (TryParseAxis(first, horizontal: true, out var horizontal) == false ||
		    TryParseAxis(second, horizontal: false, out var vertical) == false)
		{
			return false;
		}

		position = new UiBackgroundPosition(horizontal, vertical);
		return true;
	}

	public static bool TryParseRepeat(string value, out UiBackgroundRepeat repeat)
	{
		repeat = UiBackgroundRepeat.Both;
		var parts = Split(value);

		if (parts.Length == 1)
		{
			switch (parts[0])
			{
				case "repeat":
					repeat = new UiBackgroundRepeat(true, true);
					return true;

				case "no-repeat":
					repeat = new UiBackgroundRepeat(false, false);
					return true;

				case "repeat-x":
					repeat = new UiBackgroundRepeat(true, false);
					return true;

				case "repeat-y":
					repeat = new UiBackgroundRepeat(false, true);
					return true;

				default:
					return false;
			}
		}

		if (parts.Length == 2 &&
		    parts[0] is "repeat" or "no-repeat" &&
		    parts[1] is "repeat" or "no-repeat")
		{
			repeat = new UiBackgroundRepeat(parts[0] == "repeat", parts[1] == "repeat");
			return true;
		}

		return false;
	}

	/// <summary>
	/// Lays a <paramref name="naturalWidth"/> by <paramref name="naturalHeight"/> texture over the box from
	/// <paramref name="boxMin"/> to <paramref name="boxMax"/>, adding each visible tile, cut to the box, to
	/// <paramref name="tiles"/>. All values are logical pixels.
	/// </summary>
	/// <returns>False when the background would need more than <see cref="MaxTiles"/> tiles; nothing is added.</returns>
	public static bool Layout(
		Vector2 boxMin,
		Vector2 boxMax,
		float naturalWidth,
		float naturalHeight,
		UiBackgroundSize size,
		UiBackgroundPosition position,
		UiBackgroundRepeat repeat,
		float viewportWidth,
		float viewportHeight,
		float fontSize,
		float rootFontSize,
		List<UiBackgroundTile> tiles)
	{
		var box = boxMax - boxMin;
		if (box.X <= 0 || box.Y <= 0 || naturalWidth <= 0 || naturalHeight <= 0)
		{
			return true;
		}

		var tile = ResolveTileSize(box, naturalWidth, naturalHeight, size, viewportWidth, viewportHeight, fontSize, rootFontSize);
		if (tile.X <= 0 || tile.Y <= 0)
		{
			return true;
		}

		// A percentage aligns that point of the tile with the same point of the box; a length is an offset.
		var origin = boxMin + new Vector2(
			ResolveOffset(position.X, box.X - tile.X, viewportWidth, viewportHeight, fontSize, rootFontSize),
			ResolveOffset(position.Y, box.Y - tile.Y, viewportWidth, viewportHeight, fontSize, rootFontSize));

		// Repeating extends the tile grid backwards from the positioned tile until it covers the box.
		var firstX = repeat.X
			? origin.X - MathF.Ceiling((origin.X - boxMin.X) / tile.X) * tile.X
			: origin.X;
		var firstY = repeat.Y
			? origin.Y - MathF.Ceiling((origin.Y - boxMin.Y) / tile.Y) * tile.Y
			: origin.Y;
		var columns = repeat.X
			? (int)MathF.Ceiling((boxMax.X - firstX) / tile.X)
			: 1;
		var rows = repeat.Y
			? (int)MathF.Ceiling((boxMax.Y - firstY) / tile.Y)
			: 1;

		if ((long)columns * rows > MaxTiles)
		{
			return false;
		}

		for (var row = 0; row < rows; row++)
		{
			for (var column = 0; column < columns; column++)
			{
				var min = new Vector2(firstX + column * tile.X, firstY + row * tile.Y);
				AddClipped(min, min + tile, boxMin, boxMax, tiles);
			}
		}

		return true;
	}

	private static Vector2 ResolveTileSize(
		Vector2 box,
		float naturalWidth,
		float naturalHeight,
		UiBackgroundSize size,
		float vw,
		float vh,
		float em,
		float rem)
	{
		switch (size.Kind)
		{
			case UiBackgroundSizeKind.Cover:
			{
				var scale = MathF.Max(box.X / naturalWidth, box.Y / naturalHeight);
				return new Vector2(naturalWidth, naturalHeight) * scale;
			}

			case UiBackgroundSizeKind.Contain:
			{
				var scale = MathF.Min(box.X / naturalWidth, box.Y / naturalHeight);
				return new Vector2(naturalWidth, naturalHeight) * scale;
			}
		}

		var widthAuto = size.Width.Unit == UiLengthUnit.Auto;
		var heightAuto = size.Height.Unit == UiLengthUnit.Auto;
		var width = widthAuto ? 0.0f : ResolveAgainst(size.Width, box.X, vw, vh, em, rem);
		var height = heightAuto ? 0.0f : ResolveAgainst(size.Height, box.Y, vw, vh, em, rem);

		if (widthAuto && heightAuto)
		{
			return new Vector2(naturalWidth, naturalHeight);
		}

		// One auto side keeps the texture's aspect ratio.
		if (widthAuto)
		{
			return new Vector2(height * naturalWidth / naturalHeight, height);
		}

		if (heightAuto)
		{
			return new Vector2(width, width * naturalHeight / naturalWidth);
		}

		return new Vector2(width, height);
	}

	private static float ResolveOffset(UiLength length, float freeSpace, float vw, float vh, float em, float rem)
	{
		if (length.Unit == UiLengthUnit.Percent)
		{
			return freeSpace * length.Value / 100.0f;
		}

		return UiCssValues.Resolve(length, vw, vh, em, rem);
	}

	private static float ResolveAgainst(UiLength length, float reference, float vw, float vh, float em, float rem)
	{
		if (length.Unit == UiLengthUnit.Percent)
		{
			return reference * length.Value / 100.0f;
		}

		return UiCssValues.Resolve(length, vw, vh, em, rem);
	}

	private static void AddClipped(Vector2 min, Vector2 max, Vector2 boxMin, Vector2 boxMax, List<UiBackgroundTile> tiles)
	{
		var clippedMin = Vector2.Max(min, boxMin);
		var clippedMax = Vector2.Min(max, boxMax);
		if (clippedMax.X <= clippedMin.X || clippedMax.Y <= clippedMin.Y)
		{
			return;
		}

		// Cutting the quad cuts the texture by the same fraction, so the visible part keeps its scale.
		var extent = max - min;
		var uvMin = (clippedMin - min) / extent;
		var uvMax = (clippedMax - min) / extent;
		tiles.Add(new UiBackgroundTile(clippedMin, clippedMax, uvMin, uvMax));
	}

	private static bool TryParseSizeLength(string value, out UiLength length)
	{
		if (UiCssValues.TryLength(value, out length) == false)
		{
			return false;
		}

		return length.Unit == UiLengthUnit.Auto || length.Value >= 0;
	}

	private static bool TryParseAxis(string value, bool horizontal, out UiLength length)
	{
		if (horizontal && value is "left" or "center" or "right" ||
		    horizontal == false && value is "top" or "center" or "bottom")
		{
			length = Keyword(value);
			return true;
		}

		if (UiCssValues.TryLength(value, out length) && length.Unit != UiLengthUnit.Auto)
		{
			return true;
		}

		return false;
	}

	private static UiLength Keyword(string keyword)
	{
		var percent = keyword switch
		{
			"left" or "top" => 0.0f,
			"right" or "bottom" => 100.0f,
			_ => 50.0f
		};

		return new UiLength(percent, UiLengthUnit.Percent);
	}

	private static string[] Split(string value)
	{
		return value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
	}
}

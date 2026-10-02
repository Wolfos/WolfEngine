using System.Globalization;

namespace WolfEngine.UI;

internal readonly record struct UiInsets(float Top, float Right, float Bottom, float Left);

internal readonly record struct UiEdges(UiLength Top, UiLength Right, UiLength Bottom, UiLength Left)
{
	public static UiEdges Zero => new(UiLength.Pixels(0), UiLength.Pixels(0), UiLength.Pixels(0), UiLength.Pixels(0));
	public bool IsZero => this == Zero;
	public static implicit operator UiEdges(float pixels) => new(UiLength.Pixels(pixels), UiLength.Pixels(pixels), UiLength.Pixels(pixels), UiLength.Pixels(pixels));
	public static UiEdges Parse(string value)
	{
		var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
		var top = UiCssValues.Length(parts[0]);
		var right = parts.Length > 1 ? UiCssValues.Length(parts[1]) : top;
		var bottom = parts.Length > 2 ? UiCssValues.Length(parts[2]) : top;
		var left = parts.Length > 3 ? UiCssValues.Length(parts[3]) : right;
		return new(top, right, bottom, left);
	}
}

internal static class UiCssValues
{
	public static UiLength Length(string value) => TryLength(value, out var length) ? length : UiLength.Auto;
	public static bool TryLength(string value, out UiLength length)
	{
		var span = value.AsSpan().Trim();
		length = UiLength.Auto;
		if (span.Equals("auto", StringComparison.OrdinalIgnoreCase)) return true;
		var unit = UiLengthUnit.Pixels;
		var suffix = 0;
		if (span.EndsWith("rem", StringComparison.OrdinalIgnoreCase)) { unit = UiLengthUnit.Rem; suffix = 3; }
		else if (span.EndsWith("px", StringComparison.OrdinalIgnoreCase)) suffix = 2;
		else if (span.EndsWith("em", StringComparison.OrdinalIgnoreCase)) { unit = UiLengthUnit.Em; suffix = 2; }
		else if (span.EndsWith("vw", StringComparison.OrdinalIgnoreCase)) { unit = UiLengthUnit.ViewWidth; suffix = 2; }
		else if (span.EndsWith("vh", StringComparison.OrdinalIgnoreCase)) { unit = UiLengthUnit.ViewHeight; suffix = 2; }
		else if (span.EndsWith("%", StringComparison.Ordinal)) { unit = UiLengthUnit.Percent; suffix = 1; }
		if (!float.TryParse(span[..(span.Length - suffix)], NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !float.IsFinite(number)) return false;
		length = new(number, unit);
		return true;
	}
	public static float Resolve(UiLength length, float vw, float vh, float em, float rem, float percent = 0) => length.Unit switch
	{
		UiLengthUnit.ViewWidth => vw * length.Value / 100,
		UiLengthUnit.ViewHeight => vh * length.Value / 100,
		UiLengthUnit.Em => em * length.Value,
		UiLengthUnit.Rem => rem * length.Value,
		UiLengthUnit.Percent => percent * length.Value / 100,
		_ => length.Value
	};
}

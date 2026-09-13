using System.Globalization;
using System.Numerics;

namespace WolfEngine.Importing;

/// <summary>
/// A 3D colour lookup table parsed from an Adobe/Resolve .cube file.
/// </summary>
public sealed class CubeLut
{
	public CubeLut(string title, int size, Vector3 domainMin, Vector3 domainMax, Vector3[] values)
	{
		ArgumentNullException.ThrowIfNull(title);
		ArgumentNullException.ThrowIfNull(values);
		if (size < CubeLutParser.MinSize || size > CubeLutParser.MaxSize)
		{
			throw new ArgumentOutOfRangeException(nameof(size), size,
				$"LUT size must be between {CubeLutParser.MinSize} and {CubeLutParser.MaxSize}.");
		}

		if (values.Length != size * size * size)
		{
			throw new ArgumentException($"A {size}^3 LUT needs {size * size * size} entries, got {values.Length}.",
				nameof(values));
		}

		Title = title;
		Size = size;
		DomainMin = domainMin;
		DomainMax = domainMax;
		Values = values;
	}

	public string Title { get; }

	/// <summary>Entries per axis.</summary>
	public int Size { get; }

	public Vector3 DomainMin { get; }

	public Vector3 DomainMax { get; }

	/// <summary>
	/// Output colours in file order, which is red-fastest: the entry for input lattice point (r, g, b) is at
	/// <c>r + g * Size + b * Size * Size</c>. That is also x/y/z texel order for a volume texture.
	/// </summary>
	public Vector3[] Values { get; }
}

public static class CubeLutParser
{
	public const int MinSize = 2;

	/// <summary>256³ RGBA16F is ~128 MB; anything larger is not a grading LUT.</summary>
	public const int MaxSize = 256;

	public static CubeLut Parse(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		using var reader = new StreamReader(path);
		return Parse(reader, Path.GetFileName(path));
	}

	public static CubeLut Parse(TextReader reader, string sourceName)
	{
		ArgumentNullException.ThrowIfNull(reader);
		ArgumentNullException.ThrowIfNull(sourceName);

		var title = string.Empty;
		var size = 0;
		var domainMin = Vector3.Zero;
		var domainMax = Vector3.One;
		Vector3[]? values = null;
		var valueCount = 0;
		var lineNumber = 0;

		while (reader.ReadLine() is { } rawLine)
		{
			lineNumber++;
			var line = rawLine.Trim();
			if (line.Length == 0 || line[0] == '#')
			{
				continue;
			}

			if (IsDataLine(line))
			{
				if (values is null)
				{
					throw Error(sourceName, lineNumber, "LUT data appears before LUT_3D_SIZE.");
				}

				if (valueCount >= values.Length)
				{
					throw Error(sourceName, lineNumber, $"More than the {values.Length} entries a {size}^3 LUT holds.");
				}

				values[valueCount++] = ParseVector(line.AsSpan(), sourceName, lineNumber);
				continue;
			}

			var keywordEnd = line.IndexOfAny([' ', '\t']);
			var keyword = keywordEnd < 0 ? line : line[..keywordEnd];
			var arguments = keywordEnd < 0 ? string.Empty : line[(keywordEnd + 1)..].Trim();
			if (values is not null && valueCount > 0)
			{
				throw Error(sourceName, lineNumber, $"Keyword '{keyword}' appears after LUT data.");
			}

			switch (keyword)
			{
				case "TITLE":
					title = arguments.Trim('"');
					break;
				case "LUT_3D_SIZE":
					if (int.TryParse(arguments, NumberStyles.Integer, CultureInfo.InvariantCulture, out size) == false ||
					    size < MinSize || size > MaxSize)
					{
						throw Error(sourceName, lineNumber, $"LUT_3D_SIZE must be an integer between {MinSize} and {MaxSize}.");
					}

					values = new Vector3[size * size * size];
					break;
				case "LUT_1D_SIZE":
					throw Error(sourceName, lineNumber, "1D LUTs are not supported; export a 3D LUT instead.");
				case "DOMAIN_MIN":
					domainMin = ParseVector(arguments.AsSpan(), sourceName, lineNumber);
					break;
				case "DOMAIN_MAX":
					domainMax = ParseVector(arguments.AsSpan(), sourceName, lineNumber);
					break;
				case "LUT_3D_INPUT_RANGE":
				{
					// Resolve's scalar form of DOMAIN_MIN/DOMAIN_MAX.
					var range = ParseFloats(arguments.AsSpan(), 2, sourceName, lineNumber);
					domainMin = new Vector3(range[0]);
					domainMax = new Vector3(range[1]);
					break;
				}
				default:
					// Vendor keywords (e.g. LUT_1D_INPUT_RANGE in combined files) carry no data we use.
					break;
			}
		}

		if (values is null)
		{
			throw new InvalidDataException($"{sourceName}: missing LUT_3D_SIZE.");
		}

		if (valueCount != values.Length)
		{
			throw new InvalidDataException(
				$"{sourceName}: expected {values.Length} entries for a {size}^3 LUT, found {valueCount}.");
		}

		if (domainMax.X <= domainMin.X || domainMax.Y <= domainMin.Y || domainMax.Z <= domainMin.Z)
		{
			throw new InvalidDataException($"{sourceName}: DOMAIN_MAX must be greater than DOMAIN_MIN on every axis.");
		}

		return new CubeLut(title, size, domainMin, domainMax, values);
	}

	private static bool IsDataLine(string line)
	{
		var first = line[0];
		return char.IsAsciiDigit(first) || first is '-' or '+' or '.';
	}

	private static Vector3 ParseVector(ReadOnlySpan<char> text, string sourceName, int lineNumber)
	{
		var components = ParseFloats(text, 3, sourceName, lineNumber);
		return new Vector3(components[0], components[1], components[2]);
	}

	private static float[] ParseFloats(ReadOnlySpan<char> text, int expectedCount, string sourceName, int lineNumber)
	{
		var result = new float[expectedCount];
		var count = 0;
		foreach (var range in text.SplitAny(" \t"))
		{
			var token = text[range];
			if (token.IsEmpty)
			{
				continue;
			}

			if (count >= expectedCount ||
			    float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out result[count]) == false)
			{
				throw Error(sourceName, lineNumber, $"Expected {expectedCount} numbers.");
			}

			count++;
		}

		if (count != expectedCount)
		{
			throw Error(sourceName, lineNumber, $"Expected {expectedCount} numbers.");
		}

		return result;
	}

	private static InvalidDataException Error(string sourceName, int lineNumber, string message) =>
		new($"{sourceName} line {lineNumber}: {message}");
}

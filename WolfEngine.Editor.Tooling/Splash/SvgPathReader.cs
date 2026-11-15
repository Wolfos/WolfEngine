using System.Globalization;
using System.Numerics;
using System.Xml;
using System.Xml.Linq;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.Tooling.Splash;

internal enum VectorFillRule
{
	NonZero,
	EvenOdd
}

/// <summary>
/// A filled outline flattened to closed polylines. The region it covers is the union of its paths, each filled by
/// its own rule, which is how overlapping SVG paths of one colour paint.
/// </summary>
internal sealed record VectorPath(IReadOnlyList<Vector2[]> Contours, VectorFillRule FillRule);

/// <summary>An SVG path element with its paint.</summary>
internal sealed record SvgFilledPath(VectorPath Path, ColorRGBA Fill);

/// <summary>
/// Reads the filled paths of an SVG document in paint order, with every transform applied and every curve
/// flattened to lines.
/// </summary>
/// <remarks>
/// This covers what vector design tools export for flat logos: <c>g</c> and <c>path</c> elements, the
/// <c>matrix</c>, <c>translate</c> and <c>scale</c> transforms, solid fills and both fill rules. Anything that
/// would change how the logo looks if it were skipped, such as embedded images, other shapes, or arcs, is
/// rejected rather than dropped.
/// </remarks>
internal static class SvgPathReader
{
	private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

	/// <param name="flatteningTolerance">The furthest a line may stray from its curve, in document units.</param>
	public static IReadOnlyList<SvgFilledPath> Read(string svgPath, float flatteningTolerance)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(svgPath);
		if (flatteningTolerance <= 0.0f)
		{
			throw new ArgumentOutOfRangeException(nameof(flatteningTolerance), "The flattening tolerance must be positive.");
		}

		var settings = new XmlReaderSettings
		{
			DtdProcessing = DtdProcessing.Ignore,
			XmlResolver = null
		};
		XDocument document;
		using (var reader = XmlReader.Create(svgPath, settings))
		{
			document = XDocument.Load(reader);
		}

		var root = document.Root;
		if (root is null || root.Name != Svg + "svg")
		{
			throw new InvalidDataException($"'{svgPath}' is not an SVG document.");
		}

		var paths = new List<SvgFilledPath>();
		ReadChildren(root, Matrix3x2.Identity, ReadPaint(root, Paint.Default), flatteningTolerance, paths);
		if (paths.Count == 0)
		{
			throw new InvalidDataException($"'{svgPath}' contains no filled paths.");
		}

		return paths;
	}

	private static void ReadChildren(
		XElement parent,
		Matrix3x2 parentTransform,
		Paint parentPaint,
		float tolerance,
		List<SvgFilledPath> paths)
	{
		foreach (var element in parent.Elements())
		{
			var name = element.Name.LocalName;
			if (element.Name.Namespace != Svg ||
			    name is "defs" or "title" or "desc" or "metadata")
			{
				continue;
			}

			var transform = ParseTransform(element.Attribute("transform")?.Value) * parentTransform;
			var paint = ReadPaint(element, parentPaint);
			if (name == "g")
			{
				ReadChildren(element, transform, paint, tolerance, paths);
			}
			else if (name == "path")
			{
				if (paint.Fill is not { } fill)
				{
					continue;
				}

				var data = element.Attribute("d")?.Value ?? string.Empty;
				var contours = new PathDataParser(data, transform, tolerance).Parse();
				if (contours.Count > 0)
				{
					paths.Add(new SvgFilledPath(new VectorPath(contours, paint.FillRule), fill));
				}
			}
			else if (name is "image" or "use")
			{
				throw new InvalidDataException(
					$"The SVG contains an <{name}> element. The logo must be vector paths, not an embedded image.");
			}
			else
			{
				throw new InvalidDataException(
					$"The SVG contains an unsupported <{name}> element. Convert shapes to paths before exporting.");
			}
		}
	}

	private readonly record struct Paint(ColorRGBA? Fill, VectorFillRule FillRule)
	{
		// SVG paints black with the nonzero rule unless told otherwise.
		public static Paint Default => new(new ColorRGBA(0.0f, 0.0f, 0.0f, 1.0f), VectorFillRule.NonZero);
	}

	private static Paint ReadPaint(XElement element, Paint inherited)
	{
		var fill = inherited.Fill;
		var fillRule = inherited.FillRule;

		var declarations = new List<(string Name, string Value)>();
		foreach (var attribute in element.Attributes())
		{
			if (attribute.Name.LocalName is "fill" or "fill-rule")
			{
				declarations.Add((attribute.Name.LocalName, attribute.Value.Trim()));
			}
		}

		// The style attribute outranks presentation attributes.
		var style = element.Attribute("style")?.Value;
		if (style is not null)
		{
			foreach (var declaration in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
			{
				var separator = declaration.IndexOf(':');
				if (separator < 0)
				{
					continue;
				}

				declarations.Add((declaration[..separator].Trim(), declaration[(separator + 1)..].Trim()));
			}
		}

		foreach (var (name, value) in declarations)
		{
			if (name == "fill")
			{
				fill = value == "none" ? null : ParseColor(value);
			}
			else if (name == "fill-rule")
			{
				fillRule = value == "evenodd" ? VectorFillRule.EvenOdd : VectorFillRule.NonZero;
			}
		}

		return new Paint(fill, fillRule);
	}

	private static ColorRGBA ParseColor(string value)
	{
		if (value.StartsWith("rgb(", StringComparison.OrdinalIgnoreCase) &&
		    value.EndsWith(')'))
		{
			var parts = value[4..^1].Split(',');
			if (parts.Length != 3)
			{
				throw new InvalidDataException($"Unsupported SVG colour '{value}'.");
			}

			return new ColorRGBA(
				ParseChannel(parts[0]),
				ParseChannel(parts[1]),
				ParseChannel(parts[2]),
				1.0f);
		}

		if (value.StartsWith('#'))
		{
			var hex = value[1..];
			if (hex.Length == 3)
			{
				hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
			}

			if (hex.Length == 6 &&
			    int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
			{
				return new ColorRGBA(
					((rgb >> 16) & 0xFF) / 255.0f,
					((rgb >> 8) & 0xFF) / 255.0f,
					(rgb & 0xFF) / 255.0f,
					1.0f);
			}
		}

		throw new InvalidDataException($"Unsupported SVG colour '{value}'. Use rgb(r,g,b) or a hex colour.");
	}

	private static float ParseChannel(string value)
	{
		var trimmed = value.Trim();
		if (trimmed.EndsWith('%'))
		{
			return float.Parse(trimmed[..^1], CultureInfo.InvariantCulture) / 100.0f;
		}

		return float.Parse(trimmed, CultureInfo.InvariantCulture) / 255.0f;
	}

	/// <summary>
	/// SVG <c>matrix(a,b,c,d,e,f)</c> maps (x, y) to (ax + cy + e, bx + dy + f), which is a row-vector
	/// <see cref="Matrix3x2"/>. A list applies right to left, so the rightmost transform is applied first.
	/// </summary>
	internal static Matrix3x2 ParseTransform(string? value)
	{
		var result = Matrix3x2.Identity;
		if (string.IsNullOrWhiteSpace(value))
		{
			return result;
		}

		var position = 0;
		while (position < value.Length)
		{
			var open = value.IndexOf('(', position);
			if (open < 0)
			{
				break;
			}

			var close = value.IndexOf(')', open);
			if (close < 0)
			{
				throw new InvalidDataException($"Malformed SVG transform '{value}'.");
			}

			var name = value[position..open].Trim(' ', ',', '\t', '\n', '\r');
			var arguments = value[(open + 1)..close]
				.Split([',', ' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
				.Select(argument => float.Parse(argument, CultureInfo.InvariantCulture))
				.ToArray();
			var transform = name switch
			{
				"matrix" when arguments.Length == 6 => new Matrix3x2(
					arguments[0],
					arguments[1],
					arguments[2],
					arguments[3],
					arguments[4],
					arguments[5]),
				"translate" when arguments.Length is 1 or 2 => Matrix3x2.CreateTranslation(
					arguments[0],
					arguments.Length == 2 ? arguments[1] : 0.0f),
				"scale" when arguments.Length is 1 or 2 => Matrix3x2.CreateScale(
					arguments[0],
					arguments.Length == 2 ? arguments[1] : arguments[0]),
				_ => throw new InvalidDataException($"Unsupported SVG transform '{name}({value[(open + 1)..close]})'.")
			};

			result = transform * result;
			position = close + 1;
		}

		return result;
	}

	/// <summary>Parses SVG path data into closed polylines in document units.</summary>
	private ref struct PathDataParser
	{
		private readonly ReadOnlySpan<char> _data;
		private readonly Matrix3x2 _transform;
		private readonly float _tolerance;
		private readonly List<Vector2[]> _contours = new();
		private readonly List<Vector2> _current = new();
		private int _position;
		// In path coordinates, before the transform.
		private Vector2 _point;
		private Vector2 _subpathStart;
		private Vector2 _lastControl;
		private char _lastCommand;

		public PathDataParser(string data, Matrix3x2 transform, float tolerance)
		{
			_data = data;
			_transform = transform;
			_tolerance = tolerance;
		}

		public List<Vector2[]> Parse()
		{
			var command = '\0';
			while (true)
			{
				SkipSeparators();
				if (_position >= _data.Length)
				{
					break;
				}

				var next = _data[_position];
				if (char.IsLetter(next))
				{
					command = next;
					_position++;
				}
				else if (command == '\0')
				{
					throw new InvalidDataException("SVG path data must start with a command.");
				}

				ReadSegment(command);

				// Coordinates after a moveto continue as linetos.
				if (command == 'M')
				{
					command = 'L';
				}
				else if (command == 'm')
				{
					command = 'l';
				}
			}

			FinishContour();

			return _contours;
		}

		private void ReadSegment(char command)
		{
			var relative = char.IsLower(command);
			var origin = relative ? _point : Vector2.Zero;
			switch (char.ToUpperInvariant(command))
			{
				case 'M':
				{
					FinishContour();
					_point = origin + ReadPoint();
					_subpathStart = _point;
					_current.Add(Transform(_point));
					break;
				}

				case 'L':
				{
					LineTo(origin + ReadPoint());
					break;
				}

				case 'H':
				{
					var x = ReadNumber() + (relative ? _point.X : 0.0f);
					LineTo(new Vector2(x, _point.Y));
					break;
				}

				case 'V':
				{
					var y = ReadNumber() + (relative ? _point.Y : 0.0f);
					LineTo(new Vector2(_point.X, y));
					break;
				}

				case 'C':
				{
					var control1 = origin + ReadPoint();
					var control2 = origin + ReadPoint();
					var end = origin + ReadPoint();
					CubicTo(control1, control2, end);
					break;
				}

				case 'S':
				{
					var control1 = IsCubic(_lastCommand) ? 2.0f * _point - _lastControl : _point;
					var control2 = origin + ReadPoint();
					var end = origin + ReadPoint();
					CubicTo(control1, control2, end);
					break;
				}

				case 'Q':
				{
					var control = origin + ReadPoint();
					var end = origin + ReadPoint();
					QuadraticTo(control, end);
					break;
				}

				case 'T':
				{
					var control = IsQuadratic(_lastCommand) ? 2.0f * _point - _lastControl : _point;
					var end = origin + ReadPoint();
					QuadraticTo(control, end);
					break;
				}

				case 'Z':
				{
					_point = _subpathStart;
					FinishContour();
					// A command after Z without a moveto starts at the closed subpath's start.
					_current.Add(Transform(_point));
					break;
				}

				default:
				{
					throw new InvalidDataException(
						$"Unsupported SVG path command '{command}'. Convert arcs to curves before exporting.");
				}
			}

			_lastCommand = char.ToUpperInvariant(command);
		}

		private static bool IsCubic(char command) => command is 'C' or 'S';

		private static bool IsQuadratic(char command) => command is 'Q' or 'T';

		private void LineTo(Vector2 end)
		{
			_point = end;
			_lastControl = end;
			Append(Transform(end));
		}

		private void CubicTo(Vector2 control1, Vector2 control2, Vector2 end)
		{
			// An affine transform maps a Bezier curve onto the curve of its transformed control points, so the
			// curve is flattened where the tolerance is measured: in document units.
			FlattenCubic(
				Transform(_point),
				Transform(control1),
				Transform(control2),
				Transform(end),
				0);
			_point = end;
			_lastControl = control2;
		}

		private void QuadraticTo(Vector2 control, Vector2 end)
		{
			var control1 = _point + (2.0f / 3.0f) * (control - _point);
			var control2 = end + (2.0f / 3.0f) * (control - end);
			FlattenCubic(
				Transform(_point),
				Transform(control1),
				Transform(control2),
				Transform(end),
				0);
			_point = end;
			_lastControl = control;
		}

		private void FlattenCubic(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, int depth)
		{
			if (depth >= 16 ||
			    (DistanceToChord(p1, p0, p3) <= _tolerance &&
			     DistanceToChord(p2, p0, p3) <= _tolerance))
			{
				Append(p3);
				return;
			}

			var p01 = (p0 + p1) * 0.5f;
			var p12 = (p1 + p2) * 0.5f;
			var p23 = (p2 + p3) * 0.5f;
			var p012 = (p01 + p12) * 0.5f;
			var p123 = (p12 + p23) * 0.5f;
			var middle = (p012 + p123) * 0.5f;
			FlattenCubic(p0, p01, p012, middle, depth + 1);
			FlattenCubic(middle, p123, p23, p3, depth + 1);
		}

		private static float DistanceToChord(Vector2 point, Vector2 start, Vector2 end)
		{
			var chord = end - start;
			var lengthSquared = chord.LengthSquared();
			if (lengthSquared <= float.Epsilon)
			{
				return Vector2.Distance(point, start);
			}

			var t = Math.Clamp(Vector2.Dot(point - start, chord) / lengthSquared, 0.0f, 1.0f);

			return Vector2.Distance(point, start + chord * t);
		}

		private void Append(Vector2 point)
		{
			if (_current.Count > 0 &&
			    Vector2.DistanceSquared(_current[^1], point) <= 1e-12f)
			{
				return;
			}

			_current.Add(point);
		}

		private void FinishContour()
		{
			if (_current.Count > 1 &&
			    Vector2.DistanceSquared(_current[0], _current[^1]) <= 1e-12f)
			{
				_current.RemoveAt(_current.Count - 1);
			}

			if (_current.Count >= 3)
			{
				_contours.Add(_current.ToArray());
			}

			_current.Clear();
		}

		private Vector2 Transform(Vector2 point) => Vector2.Transform(point, _transform);

		private Vector2 ReadPoint()
		{
			var x = ReadNumber();
			var y = ReadNumber();

			return new Vector2(x, y);
		}

		private float ReadNumber()
		{
			SkipSeparators();
			var start = _position;
			if (_position < _data.Length &&
			    _data[_position] is '+' or '-')
			{
				_position++;
			}

			var seenDot = false;
			var seenDigit = false;
			while (_position < _data.Length)
			{
				var character = _data[_position];
				if (char.IsAsciiDigit(character))
				{
					seenDigit = true;
					_position++;
				}
				else if (character == '.' && seenDot == false)
				{
					// A second dot starts the next number, as in "0.5.5".
					seenDot = true;
					_position++;
				}
				else
				{
					break;
				}
			}

			if (seenDigit &&
			    _position < _data.Length &&
			    _data[_position] is 'e' or 'E')
			{
				var exponentStart = _position;
				_position++;
				if (_position < _data.Length &&
				    _data[_position] is '+' or '-')
				{
					_position++;
				}

				var exponentDigits = false;
				while (_position < _data.Length &&
				       char.IsAsciiDigit(_data[_position]))
				{
					exponentDigits = true;
					_position++;
				}

				if (exponentDigits == false)
				{
					_position = exponentStart;
				}
			}

			if (seenDigit == false)
			{
				throw new InvalidDataException($"Expected a number in SVG path data at offset {start}.");
			}

			return float.Parse(_data[start.._position], NumberStyles.Float, CultureInfo.InvariantCulture);
		}

		private void SkipSeparators()
		{
			while (_position < _data.Length &&
			       (char.IsWhiteSpace(_data[_position]) || _data[_position] == ','))
			{
				_position++;
			}
		}
	}
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace WolfEngine.UnityExporter
{
	/// <summary>
	/// Writes the export's JSON by hand, so floats round-trip exactly in the invariant culture. Commas between members and
	/// array elements are tracked per nesting level.
	/// </summary>
	internal sealed class ExportJsonWriter
	{
		private readonly StringBuilder _builder = new StringBuilder();
		private readonly Stack<bool> _hasMembers = new Stack<bool>();

		public void BeginObject()
		{
			WriteSeparator();
			_builder.Append('{');
			_hasMembers.Push(false);
		}

		public void BeginObject(string name)
		{
			WriteName(name);
			_builder.Append('{');
			_hasMembers.Push(false);
		}

		public void EndObject()
		{
			_hasMembers.Pop();
			WriteLineBreak();
			_builder.Append('}');
		}

		public void BeginArray(string name)
		{
			WriteName(name);
			_builder.Append('[');
			_hasMembers.Push(false);
		}

		public void EndArray()
		{
			_hasMembers.Pop();
			WriteLineBreak();
			_builder.Append(']');
		}

		public void Write(string name, string value)
		{
			WriteName(name);
			WriteString(value);
		}

		public void Write(string name, bool value)
		{
			WriteName(name);
			_builder.Append(value ? "true" : "false");
		}

		public void Write(string name, int value)
		{
			WriteName(name);
			_builder.Append(value.ToString(CultureInfo.InvariantCulture));
		}

		public void Write(string name, float value)
		{
			WriteName(name);
			WriteFloat(value);
		}

		public void Write(string name, Vector2 value)
		{
			WriteName(name);
			WriteFloats(value.x, value.y);
		}

		public void Write(string name, Vector3 value)
		{
			WriteName(name);
			WriteFloats(value.x, value.y, value.z);
		}

		public void Write(string name, Vector4 value)
		{
			WriteName(name);
			WriteFloats(value.x, value.y, value.z, value.w);
		}

		public void Write(string name, Quaternion value)
		{
			WriteName(name);
			WriteFloats(value.x, value.y, value.z, value.w);
		}

		public void Write(string name, Color value)
		{
			WriteName(name);
			WriteFloats(value.r, value.g, value.b, value.a);
		}

		public void WriteStrings(string name, IReadOnlyList<string> values)
		{
			WriteName(name);
			_builder.Append('[');
			for (var i = 0; i < values.Count; i++)
			{
				if (i > 0)
				{
					_builder.Append(", ");
				}

				WriteString(values[i]);
			}

			_builder.Append(']');
		}

		public void Save(string path)
		{
			File.WriteAllText(path, _builder.ToString() + "\n", new UTF8Encoding(false));
		}

		private void WriteName(string name)
		{
			WriteSeparator();
			WriteString(name);
			_builder.Append(": ");
		}

		private void WriteSeparator()
		{
			if (_hasMembers.Count == 0) return;

			if (_hasMembers.Peek())
			{
				_builder.Append(',');
			}
			else
			{
				_hasMembers.Pop();
				_hasMembers.Push(true);
			}

			WriteLineBreak();
		}

		private void WriteLineBreak()
		{
			_builder.Append('\n');
			_builder.Append('\t', _hasMembers.Count);
		}

		private void WriteFloats(params float[] values)
		{
			_builder.Append('[');
			for (var i = 0; i < values.Length; i++)
			{
				if (i > 0)
				{
					_builder.Append(", ");
				}

				WriteFloat(values[i]);
			}

			_builder.Append(']');
		}

		private void WriteFloat(float value)
		{
			if (float.IsNaN(value) || float.IsInfinity(value))
			{
				throw new InvalidOperationException($"Cannot export the non-finite value {value}.");
			}

			_builder.Append(value.ToString("R", CultureInfo.InvariantCulture));
		}

		private void WriteString(string value)
		{
			if (value == null)
			{
				_builder.Append("null");
				return;
			}

			_builder.Append('"');
			foreach (var character in value)
			{
				switch (character)
				{
					case '"':
						_builder.Append("\\\"");
						break;

					case '\\':
						_builder.Append("\\\\");
						break;

					case '\n':
						_builder.Append("\\n");
						break;

					case '\r':
						_builder.Append("\\r");
						break;

					case '\t':
						_builder.Append("\\t");
						break;

					default:
						if (character < ' ')
						{
							_builder.Append("\\u");
							_builder.Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
						}
						else
						{
							_builder.Append(character);
						}

						break;
				}
			}

			_builder.Append('"');
		}
	}
}

#nullable enable

using System;
using System.Collections.Generic;

namespace WolfEngine.Rendering;

public enum ShaderConstantFieldValueKind
{
	Unsupported = 0,
	UInt,
	Int,
	Float,
	Vector2,
	Vector3,
	Vector4,
	Matrix4x4
}

public readonly struct ShaderConstantFieldLayout
{
	public ShaderConstantFieldLayout(string path, int offset, int byteSize, ShaderConstantFieldValueKind valueKind)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			throw new ArgumentException("Shader field path cannot be null or empty.", nameof(path));
		}

		if (offset < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(offset), offset, "Shader field offset cannot be negative.");
		}

		if (byteSize < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(byteSize), byteSize, "Shader field byte size cannot be negative.");
		}

		Path = path;
		Offset = offset;
		ByteSize = byteSize;
		ValueKind = valueKind;
	}

	public string Path { get; }

	public int Offset { get; }

	public int ByteSize { get; }

	public ShaderConstantFieldValueKind ValueKind { get; }
}

public sealed class ShaderConstantBufferLayout
{
	private readonly Dictionary<string, ShaderConstantFieldLayout> _fields;

	public ShaderConstantBufferLayout(
		string name,
		uint registerIndex,
		int sizeInBytes,
		IReadOnlyDictionary<string, ShaderConstantFieldLayout> fields)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			throw new ArgumentException("Constant-buffer name cannot be null or empty.", nameof(name));
		}

		if (sizeInBytes <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(sizeInBytes), sizeInBytes, "Constant-buffer size must be positive.");
		}

		ArgumentNullException.ThrowIfNull(fields);

		Name = name;
		RegisterIndex = registerIndex;
		SizeInBytes = sizeInBytes;
		_fields = new Dictionary<string, ShaderConstantFieldLayout>(fields, StringComparer.Ordinal);
	}

	public string Name { get; }

	public uint RegisterIndex { get; }

	public int SizeInBytes { get; }

	public IReadOnlyDictionary<string, ShaderConstantFieldLayout> Fields => _fields;

	public bool TryGetField(string path, out ShaderConstantFieldLayout layout)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			layout = default;
			return false;
		}

		return _fields.TryGetValue(path, out layout);
	}

	public ShaderConstantFieldLayout GetFieldOrThrow(string path)
	{
		if (TryGetField(path, out var layout))
		{
			return layout;
		}

		throw new InvalidOperationException(
			$"Shader field '{path}' was not found in constant buffer '{Name}'.");
	}
}

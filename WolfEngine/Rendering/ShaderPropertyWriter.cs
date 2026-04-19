#nullable enable

using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace WolfEngine.Rendering;

internal sealed class ShaderPropertyWriter
{
	private readonly ShaderConstantBufferLayout _layout;
	private readonly byte[] _data;

	public ShaderPropertyWriter(ShaderConstantBufferLayout layout)
	{
		_layout = layout ?? throw new ArgumentNullException(nameof(layout));
		_data = new byte[_layout.SizeInBytes];
	}

	public uint RegisterIndex => _layout.RegisterIndex;

	public void Clear() => Array.Clear(_data);

	public ReadOnlySpan<byte> AsBytes() => _data;

	public void SetUInt(string path, uint value)
	{
		var field = GetFieldOrThrow(path, ShaderConstantFieldValueKind.UInt);
		Write(field, value);
	}

	public void SetUInt(in ShaderConstantFieldLayout field, uint value)
	{
		ValidateFieldKind(field, ShaderConstantFieldValueKind.UInt);
		Write(field, value);
	}

	public void SetFloat(string path, float value)
	{
		var field = GetFieldOrThrow(path, ShaderConstantFieldValueKind.Float);
		Write(field, value);
	}

	public void SetFloat(in ShaderConstantFieldLayout field, float value)
	{
		ValidateFieldKind(field, ShaderConstantFieldValueKind.Float);
		Write(field, value);
	}

	public void SetVector2(string path, Vector2 value)
	{
		var field = GetFieldOrThrow(path, ShaderConstantFieldValueKind.Vector2);
		Write(field, value);
	}

	public void SetVector3(string path, Vector3 value)
	{
		var field = GetFieldOrThrow(path, ShaderConstantFieldValueKind.Vector3);
		Write(field, value);
	}

	public void SetVector4(string path, Vector4 value)
	{
		var field = GetFieldOrThrow(path, ShaderConstantFieldValueKind.Vector4);
		Write(field, value);
	}

	public void SetColorRGBA(string path, ColorRGBA value)
	{
		var field = GetFieldOrThrow(path, ShaderConstantFieldValueKind.Vector4);
		Write(field, value);
	}

	public void SetMatrix4x4(string path, Matrix4x4 value)
	{
		var field = GetFieldOrThrow(path, ShaderConstantFieldValueKind.Matrix4x4);
		Write(field, value);
	}

	private ShaderConstantFieldLayout GetFieldOrThrow(string path, ShaderConstantFieldValueKind expectedKind)
	{
		var field = _layout.GetFieldOrThrow(path);
		ValidateFieldKind(field, expectedKind);
		return field;
	}

	private void ValidateFieldKind(in ShaderConstantFieldLayout field, ShaderConstantFieldValueKind expectedKind)
	{
		if (field.ValueKind != expectedKind)
		{
			throw new InvalidOperationException(
				$"Shader field '{field.Path}' in constant buffer '{_layout.Name}' has type '{field.ValueKind}', " +
				$"but '{expectedKind}' was requested.");
		}
	}

	private void Write<T>(in ShaderConstantFieldLayout field, in T value) where T : unmanaged
	{
		var byteCount = Marshal.SizeOf<T>();
		if (byteCount <= 0)
		{
			throw new InvalidOperationException("Shader write size must be positive.");
		}

		if (field.Offset < 0 || field.Offset + byteCount > _data.Length)
		{
			throw new InvalidOperationException(
				$"Shader field '{field.Path}' write ({byteCount} bytes at offset {field.Offset}) exceeds " +
				$"constant buffer '{_layout.Name}' size {_data.Length}.");
		}

		if (field.ByteSize > 0 && byteCount > field.ByteSize)
		{
			throw new InvalidOperationException(
				$"Shader field '{field.Path}' write size {byteCount} exceeds reflected field size {field.ByteSize}.");
		}

		MemoryMarshal.Write(_data.AsSpan(field.Offset, byteCount), in value);
	}
}

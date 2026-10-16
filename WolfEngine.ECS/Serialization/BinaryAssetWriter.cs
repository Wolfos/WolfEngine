using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using WolfEngine.ECS;

namespace WolfEngine.Serialization;

/// <summary>
/// Writes the binary cooked format read by <see cref="BinaryAssetReader"/>. Values are little-endian, counts and
/// string lengths are variable-length, and a null reference is a zero count so readers never need a separate flag.
/// </summary>
/// <remarks>
/// The member layout of components and data assets is not described anywhere in the data. Generated writers and
/// readers agree on it because they are generated together from the same type, and a layout hash recorded beside
/// the data catches content cooked by a different build.
/// </remarks>
public sealed class BinaryAssetWriter
{
	private readonly ArrayBufferWriter<byte> _buffer;

	public BinaryAssetWriter(int initialCapacity = 256)
	{
		_buffer = new ArrayBufferWriter<byte>(Math.Max(16, initialCapacity));
	}

	/// <summary>Maps entity references to stable ids. References are written as empty ids without one.</summary>
	public IEntityReferenceEncoder? Entities { get; set; }

	public int Length => _buffer.WrittenCount;

	public ReadOnlySpan<byte> WrittenSpan => _buffer.WrittenSpan;

	public byte[] ToArray()
	{
		return _buffer.WrittenSpan.ToArray();
	}

	public void Clear()
	{
		_buffer.Clear();
	}

	public void WriteBoolean(bool value)
	{
		WriteByte(value ? (byte)1 : (byte)0);
	}

	public void WriteByte(byte value)
	{
		var span = _buffer.GetSpan(1);
		span[0] = value;
		_buffer.Advance(1);
	}

	public void WriteSByte(sbyte value)
	{
		WriteByte(unchecked((byte)value));
	}

	public void WriteInt16(short value)
	{
		BinaryPrimitives.WriteInt16LittleEndian(_buffer.GetSpan(sizeof(short)), value);
		_buffer.Advance(sizeof(short));
	}

	public void WriteUInt16(ushort value)
	{
		BinaryPrimitives.WriteUInt16LittleEndian(_buffer.GetSpan(sizeof(ushort)), value);
		_buffer.Advance(sizeof(ushort));
	}

	public void WriteChar(char value)
	{
		WriteUInt16(value);
	}

	public void WriteInt32(int value)
	{
		BinaryPrimitives.WriteInt32LittleEndian(_buffer.GetSpan(sizeof(int)), value);
		_buffer.Advance(sizeof(int));
	}

	public void WriteUInt32(uint value)
	{
		BinaryPrimitives.WriteUInt32LittleEndian(_buffer.GetSpan(sizeof(uint)), value);
		_buffer.Advance(sizeof(uint));
	}

	public void WriteInt64(long value)
	{
		BinaryPrimitives.WriteInt64LittleEndian(_buffer.GetSpan(sizeof(long)), value);
		_buffer.Advance(sizeof(long));
	}

	public void WriteUInt64(ulong value)
	{
		BinaryPrimitives.WriteUInt64LittleEndian(_buffer.GetSpan(sizeof(ulong)), value);
		_buffer.Advance(sizeof(ulong));
	}

	public void WriteSingle(float value)
	{
		BinaryPrimitives.WriteSingleLittleEndian(_buffer.GetSpan(sizeof(float)), value);
		_buffer.Advance(sizeof(float));
	}

	public void WriteDouble(double value)
	{
		BinaryPrimitives.WriteDoubleLittleEndian(_buffer.GetSpan(sizeof(double)), value);
		_buffer.Advance(sizeof(double));
	}

	public void WriteDecimal(decimal value)
	{
		Span<int> bits = stackalloc int[4];
		decimal.GetBits(value, bits);
		for (var i = 0; i < bits.Length; i++)
		{
			WriteInt32(bits[i]);
		}
	}

	/// <summary>Writes an unsigned integer in 7-bit groups, least significant first.</summary>
	public void WriteVarUInt32(uint value)
	{
		while (value >= 0x80)
		{
			WriteByte((byte)(value | 0x80));
			value >>= 7;
		}

		WriteByte((byte)value);
	}

	/// <summary>Writes the element count of a collection, or null. Read back with <see cref="BinaryAssetReader.ReadCount"/>.</summary>
	public void WriteCount(int count)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(count);

		WriteVarUInt32((uint)count + 1);
	}

	public void WriteNull()
	{
		WriteVarUInt32(0);
	}

	/// <summary>Writes whether an optional value follows.</summary>
	public void WritePresence(bool present)
	{
		WriteBoolean(present);
	}

	public void WriteString(string? value)
	{
		if (value is null)
		{
			WriteNull();
			return;
		}

		var byteCount = Encoding.UTF8.GetByteCount(value);
		WriteCount(byteCount);
		Encoding.UTF8.GetBytes(value, _buffer.GetSpan(byteCount));
		_buffer.Advance(byteCount);
	}

	public void WriteBytes(ReadOnlySpan<byte> value)
	{
		value.CopyTo(_buffer.GetSpan(value.Length));
		_buffer.Advance(value.Length);
	}

	/// <summary>Writes a length-prefixed block, so a reader can keep or skip it without understanding it.</summary>
	public void WriteBlock(ReadOnlySpan<byte> value)
	{
		WriteCount(value.Length);
		WriteBytes(value);
	}

	public void WriteGuid(Guid value)
	{
		value.TryWriteBytes(_buffer.GetSpan(16));
		_buffer.Advance(16);
	}

	public void WriteDateTime(DateTime value)
	{
		WriteInt64(value.ToBinary());
	}

	public void WriteDateTimeOffset(DateTimeOffset value)
	{
		WriteInt64(value.Ticks);
		WriteInt64(value.Offset.Ticks);
	}

	public void WriteTimeSpan(TimeSpan value)
	{
		WriteInt64(value.Ticks);
	}

	public void WriteVector2(Vector2 value)
	{
		WriteSingle(value.X);
		WriteSingle(value.Y);
	}

	public void WriteVector3(Vector3 value)
	{
		WriteSingle(value.X);
		WriteSingle(value.Y);
		WriteSingle(value.Z);
	}

	public void WriteVector4(Vector4 value)
	{
		WriteSingle(value.X);
		WriteSingle(value.Y);
		WriteSingle(value.Z);
		WriteSingle(value.W);
	}

	public void WriteQuaternion(Quaternion value)
	{
		WriteSingle(value.X);
		WriteSingle(value.Y);
		WriteSingle(value.Z);
		WriteSingle(value.W);
	}

	public void WritePlane(Plane value)
	{
		WriteVector3(value.Normal);
		WriteSingle(value.D);
	}

	public void WriteMatrix3x2(Matrix3x2 value)
	{
		WriteSingle(value.M11);
		WriteSingle(value.M12);
		WriteSingle(value.M21);
		WriteSingle(value.M22);
		WriteSingle(value.M31);
		WriteSingle(value.M32);
	}

	public void WriteMatrix4x4(Matrix4x4 value)
	{
		WriteSingle(value.M11);
		WriteSingle(value.M12);
		WriteSingle(value.M13);
		WriteSingle(value.M14);
		WriteSingle(value.M21);
		WriteSingle(value.M22);
		WriteSingle(value.M23);
		WriteSingle(value.M24);
		WriteSingle(value.M31);
		WriteSingle(value.M32);
		WriteSingle(value.M33);
		WriteSingle(value.M34);
		WriteSingle(value.M41);
		WriteSingle(value.M42);
		WriteSingle(value.M43);
		WriteSingle(value.M44);
	}

	/// <summary>Writes an entity reference as the stable id <see cref="Entities"/> assigns it.</summary>
	public void WriteEntity(Entity value)
	{
		if (value.IsValid == false || Entities is null)
		{
			WriteGuid(Guid.Empty);
			return;
		}

		WriteGuid(Entities.Encode(value));
	}
}

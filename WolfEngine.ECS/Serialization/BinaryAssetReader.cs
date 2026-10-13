using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using WolfEngine.ECS;

namespace WolfEngine.Serialization;

/// <summary>Reads data written by <see cref="BinaryAssetWriter"/>. Reading past the end throws <see cref="InvalidDataException"/>.</summary>
public ref struct BinaryAssetReader
{
	private readonly ReadOnlySpan<byte> _data;
	private int _position;

	public BinaryAssetReader(ReadOnlySpan<byte> data, IEntityReferenceDecoder? entities = null)
	{
		_data = data;
		_position = 0;
		Entities = entities;
	}

	/// <summary>Resolves entity references. Without one, every reference reads as the invalid entity.</summary>
	public IEntityReferenceDecoder? Entities { get; set; }

	public int Position => _position;

	public int Remaining => _data.Length - _position;

	public bool IsAtEnd => _position == _data.Length;

	public bool ReadBoolean()
	{
		var value = ReadByte();
		if (value > 1)
		{
			throw new InvalidDataException($"Cooked data has an invalid boolean value {value} at offset {_position - 1}.");
		}

		return value == 1;
	}

	public byte ReadByte()
	{
		return Take(1)[0];
	}

	public sbyte ReadSByte()
	{
		return unchecked((sbyte)ReadByte());
	}

	public short ReadInt16()
	{
		return BinaryPrimitives.ReadInt16LittleEndian(Take(sizeof(short)));
	}

	public ushort ReadUInt16()
	{
		return BinaryPrimitives.ReadUInt16LittleEndian(Take(sizeof(ushort)));
	}

	public char ReadChar()
	{
		return (char)ReadUInt16();
	}

	public int ReadInt32()
	{
		return BinaryPrimitives.ReadInt32LittleEndian(Take(sizeof(int)));
	}

	public uint ReadUInt32()
	{
		return BinaryPrimitives.ReadUInt32LittleEndian(Take(sizeof(uint)));
	}

	public long ReadInt64()
	{
		return BinaryPrimitives.ReadInt64LittleEndian(Take(sizeof(long)));
	}

	public ulong ReadUInt64()
	{
		return BinaryPrimitives.ReadUInt64LittleEndian(Take(sizeof(ulong)));
	}

	public float ReadSingle()
	{
		return BinaryPrimitives.ReadSingleLittleEndian(Take(sizeof(float)));
	}

	public double ReadDouble()
	{
		return BinaryPrimitives.ReadDoubleLittleEndian(Take(sizeof(double)));
	}

	public decimal ReadDecimal()
	{
		Span<int> bits = stackalloc int[4];
		for (var i = 0; i < bits.Length; i++)
		{
			bits[i] = ReadInt32();
		}

		return new decimal(bits);
	}

	public uint ReadVarUInt32()
	{
		uint value = 0;
		for (var shift = 0; shift < 35; shift += 7)
		{
			var next = ReadByte();
			value |= (uint)(next & 0x7F) << shift;
			if ((next & 0x80) == 0)
			{
				return value;
			}
		}

		throw new InvalidDataException($"Cooked data has a malformed variable-length integer at offset {_position}.");
	}

	/// <summary>Reads a count written by <see cref="BinaryAssetWriter.WriteCount"/>; -1 means null.</summary>
	public int ReadCount()
	{
		var encoded = ReadVarUInt32();
		if (encoded == 0)
		{
			return -1;
		}

		var count = encoded - 1;
		if (count > int.MaxValue)
		{
			throw new InvalidDataException($"Cooked data has an out-of-range count {count} at offset {_position}.");
		}

		return (int)count;
	}

	/// <summary>
	/// Reads a count of elements that each take at least one byte. A count beyond the remaining data is corrupt,
	/// and rejecting it here stops it from becoming a huge allocation before the read fails.
	/// </summary>
	public int ReadBoundedCount()
	{
		var count = ReadCount();
		if (count > Remaining)
		{
			throw new InvalidDataException($"Cooked data has a count of {count} with only {Remaining} bytes left.");
		}

		return count;
	}

	public bool ReadPresence()
	{
		return ReadBoolean();
	}

	public string? ReadString()
	{
		var length = ReadBoundedCount();
		if (length < 0)
		{
			return null;
		}

		return Encoding.UTF8.GetString(Take(length));
	}

	public ReadOnlySpan<byte> ReadBytes(int length)
	{
		return Take(length);
	}

	/// <summary>Reads a block written by <see cref="BinaryAssetWriter.WriteBlock"/>.</summary>
	public ReadOnlySpan<byte> ReadBlock()
	{
		var length = ReadBoundedCount();
		if (length < 0)
		{
			throw new InvalidDataException($"Cooked data has a null block at offset {_position}.");
		}

		return Take(length);
	}

	public Guid ReadGuid()
	{
		return new Guid(Take(16));
	}

	public DateTime ReadDateTime()
	{
		return DateTime.FromBinary(ReadInt64());
	}

	public DateTimeOffset ReadDateTimeOffset()
	{
		var ticks = ReadInt64();
		var offset = ReadInt64();

		return new DateTimeOffset(ticks, new TimeSpan(offset));
	}

	public TimeSpan ReadTimeSpan()
	{
		return new TimeSpan(ReadInt64());
	}

	public Vector2 ReadVector2()
	{
		var x = ReadSingle();
		var y = ReadSingle();

		return new Vector2(x, y);
	}

	public Vector3 ReadVector3()
	{
		var x = ReadSingle();
		var y = ReadSingle();
		var z = ReadSingle();

		return new Vector3(x, y, z);
	}

	public Vector4 ReadVector4()
	{
		var x = ReadSingle();
		var y = ReadSingle();
		var z = ReadSingle();
		var w = ReadSingle();

		return new Vector4(x, y, z, w);
	}

	public Quaternion ReadQuaternion()
	{
		var x = ReadSingle();
		var y = ReadSingle();
		var z = ReadSingle();
		var w = ReadSingle();

		return new Quaternion(x, y, z, w);
	}

	public Plane ReadPlane()
	{
		var normal = ReadVector3();
		var d = ReadSingle();

		return new Plane(normal, d);
	}

	public Matrix3x2 ReadMatrix3x2()
	{
		var m11 = ReadSingle();
		var m12 = ReadSingle();
		var m21 = ReadSingle();
		var m22 = ReadSingle();
		var m31 = ReadSingle();
		var m32 = ReadSingle();

		return new Matrix3x2(m11, m12, m21, m22, m31, m32);
	}

	public Matrix4x4 ReadMatrix4x4()
	{
		var m11 = ReadSingle();
		var m12 = ReadSingle();
		var m13 = ReadSingle();
		var m14 = ReadSingle();
		var m21 = ReadSingle();
		var m22 = ReadSingle();
		var m23 = ReadSingle();
		var m24 = ReadSingle();
		var m31 = ReadSingle();
		var m32 = ReadSingle();
		var m33 = ReadSingle();
		var m34 = ReadSingle();
		var m41 = ReadSingle();
		var m42 = ReadSingle();
		var m43 = ReadSingle();
		var m44 = ReadSingle();

		return new Matrix4x4(
			m11,
			m12,
			m13,
			m14,
			m21,
			m22,
			m23,
			m24,
			m31,
			m32,
			m33,
			m34,
			m41,
			m42,
			m43,
			m44);
	}

	/// <summary>Reads an entity reference and resolves it through <see cref="Entities"/>.</summary>
	public Entity ReadEntity()
	{
		var id = ReadGuid();
		if (id == Guid.Empty || Entities is null)
		{
			return default;
		}

		return Entities.Decode(id);
	}

	private ReadOnlySpan<byte> Take(int length)
	{
		if (length < 0 || length > Remaining)
		{
			throw new InvalidDataException(
				$"Cooked data ended early: needed {length} bytes at offset {_position}, {Remaining} remain.");
		}

		var span = _data.Slice(_position, length);
		_position += length;

		return span;
	}
}

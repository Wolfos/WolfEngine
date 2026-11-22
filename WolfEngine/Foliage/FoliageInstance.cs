using System.Buffers.Binary;
using System.Numerics;

namespace WolfEngine;

/// <summary>One placed foliage instance, in the space of the terrain that owns it. See docs/foliage.md.</summary>
/// <param name="Position">Position in the terrain entity's local space.</param>
/// <param name="Rotation">Rotation in the terrain entity's local space.</param>
/// <param name="Scale">Uniform scale.</param>
/// <param name="TypeIndex">Index into <see cref="TerrainFoliage.Types"/>.</param>
public readonly record struct FoliageInstance(Vector3 Position, Quaternion Rotation, float Scale, int TypeIndex);

/// <summary>
/// The 16-byte form placed instances are stored and uploaded in. Mirrored by <c>DecodeFoliageInstance</c> in
/// foliage_cull.compute.slang; change them together.
/// </summary>
/// <remarks>
/// Four little-endian words:
/// <list type="number">
/// <item>Position X and Y, each a 16-bit unorm across the cell's bounds.</item>
/// <item>Position Z, the same way, and the scale as a 16-bit float.</item>
/// <item>
/// The rotation, packed as its three smallest components (10 bits each, in 1022 steps so zero is exact) and the index
/// of the largest (2 bits).
/// </item>
/// <item>The type index in the low 16 bits. The high 16 bits are reserved and zero.</item>
/// </list>
/// Positions resolve to about 1 mm across a 64 m cell, and rotations to about 0.1 degrees.
/// </remarks>
public static class FoliageInstancePacking
{
	public const int SizeInBytes = 16;

	/// <summary>Highest type index the packed form can hold.</summary>
	public const int MaxTypeIndex = ushort.MaxValue;

	private const float QuaternionComponentLimit = 0.70710678f;
	private const uint QuaternionComponentMask = 0x3FF;

	// An even number of steps, so zero falls exactly on step 511 and a rotation about one axis stays about that axis.
	private const float QuaternionComponentSteps = 1022.0f;

	public static void Pack(in FoliageInstance instance, Vector3 boundsMin, Vector3 boundsMax, Span<byte> destination)
	{
		if (destination.Length < SizeInBytes)
		{
			throw new ArgumentException($"A packed foliage instance needs {SizeInBytes} bytes.", nameof(destination));
		}

		if ((uint)instance.TypeIndex > MaxTypeIndex)
		{
			throw new ArgumentOutOfRangeException(nameof(instance), instance.TypeIndex, "Foliage type index is out of range.");
		}

		var x = QuantizeUnorm16(instance.Position.X, boundsMin.X, boundsMax.X);
		var y = QuantizeUnorm16(instance.Position.Y, boundsMin.Y, boundsMax.Y);
		var z = QuantizeUnorm16(instance.Position.Z, boundsMin.Z, boundsMax.Z);
		var scale = BitConverter.HalfToUInt16Bits((Half)instance.Scale);

		BinaryPrimitives.WriteUInt32LittleEndian(destination, x | (y << 16));
		BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], z | ((uint)scale << 16));
		BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], PackQuaternion(instance.Rotation));
		BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], (uint)instance.TypeIndex);
	}

	public static FoliageInstance Unpack(ReadOnlySpan<byte> source, Vector3 boundsMin, Vector3 boundsMax)
	{
		if (source.Length < SizeInBytes)
		{
			throw new ArgumentException($"A packed foliage instance needs {SizeInBytes} bytes.", nameof(source));
		}

		var word0 = BinaryPrimitives.ReadUInt32LittleEndian(source);
		var word1 = BinaryPrimitives.ReadUInt32LittleEndian(source[4..]);
		var word2 = BinaryPrimitives.ReadUInt32LittleEndian(source[8..]);
		var word3 = BinaryPrimitives.ReadUInt32LittleEndian(source[12..]);

		var position = new Vector3(
			DequantizeUnorm16(word0 & 0xFFFF, boundsMin.X, boundsMax.X),
			DequantizeUnorm16(word0 >> 16, boundsMin.Y, boundsMax.Y),
			DequantizeUnorm16(word1 & 0xFFFF, boundsMin.Z, boundsMax.Z));
		var scale = (float)BitConverter.UInt16BitsToHalf((ushort)(word1 >> 16));

		return new FoliageInstance(position, UnpackQuaternion(word2), scale, (int)(word3 & 0xFFFF));
	}

	public static uint PackQuaternion(Quaternion rotation)
	{
		var length = rotation.Length();
		rotation = length > 0.0f ? Quaternion.Normalize(rotation) : Quaternion.Identity;

		Span<float> components = [rotation.X, rotation.Y, rotation.Z, rotation.W];
		var largest = 0;
		for (var i = 1; i < 4; i++)
		{
			if (MathF.Abs(components[i]) > MathF.Abs(components[largest]))
			{
				largest = i;
			}
		}

		// q and -q are the same rotation, so the largest component is made positive and left out.
		var sign = components[largest] < 0.0f ? -1.0f : 1.0f;
		var packed = (uint)largest << 30;
		var shift = 20;
		for (var i = 0; i < 4; i++)
		{
			if (i == largest)
			{
				continue;
			}

			var normalized = (components[i] * sign / QuaternionComponentLimit) * 0.5f + 0.5f;
			var quantized = (uint)MathF.Round(Math.Clamp(normalized, 0.0f, 1.0f) * QuaternionComponentSteps);
			packed |= quantized << shift;
			shift -= 10;
		}

		return packed;
	}

	public static Quaternion UnpackQuaternion(uint packed)
	{
		var largest = (int)(packed >> 30);
		Span<float> components = stackalloc float[4];
		var shift = 20;
		var sumOfSquares = 0.0f;
		for (var i = 0; i < 4; i++)
		{
			if (i == largest)
			{
				continue;
			}

			var quantized = (packed >> shift) & QuaternionComponentMask;
			var value = ((quantized / QuaternionComponentSteps) * 2.0f - 1.0f) * QuaternionComponentLimit;
			components[i] = value;
			sumOfSquares += value * value;
			shift -= 10;
		}

		components[largest] = MathF.Sqrt(MathF.Max(0.0f, 1.0f - sumOfSquares));

		return Quaternion.Normalize(new Quaternion(components[0], components[1], components[2], components[3]));
	}

	private static uint QuantizeUnorm16(float value, float min, float max)
	{
		var extent = max - min;
		if (extent <= 0.0f)
		{
			return 0;
		}

		var normalized = Math.Clamp((value - min) / extent, 0.0f, 1.0f);

		return (uint)MathF.Round(normalized * ushort.MaxValue);
	}

	private static float DequantizeUnorm16(uint value, float min, float max)
	{
		return min + (value / (float)ushort.MaxValue) * (max - min);
	}
}

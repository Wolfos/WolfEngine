using System.Numerics;

namespace WolfEngine.AssetPipeline;

/// <summary>
/// Length-prefixed array primitives shared by the binary asset artifact serializers.
/// </summary>
internal static class AssetBinaryPrimitives
{
	internal static void WriteInt32Array(BinaryWriter writer, IReadOnlyList<int> values)
	{
		writer.Write(values.Count);
		for (var i = 0; i < values.Count; i++)
		{
			writer.Write(values[i]);
		}
	}

	internal static int[] ReadInt32Array(BinaryReader reader)
	{
		var count = reader.ReadInt32();
		var values = new int[count];
		for (var i = 0; i < count; i++)
		{
			values[i] = reader.ReadInt32();
		}

		return values;
	}

	internal static void WriteUInt32Array(BinaryWriter writer, IReadOnlyList<uint> values)
	{
		writer.Write(values.Count);
		for (var i = 0; i < values.Count; i++)
		{
			writer.Write(values[i]);
		}
	}

	internal static uint[] ReadUInt32Array(BinaryReader reader)
	{
		var count = reader.ReadInt32();
		var values = new uint[count];
		for (var i = 0; i < count; i++)
		{
			values[i] = reader.ReadUInt32();
		}

		return values;
	}

	internal static void WriteSingleArray(BinaryWriter writer, IReadOnlyList<float> values)
	{
		writer.Write(values.Count);
		for (var i = 0; i < values.Count; i++)
		{
			writer.Write(values[i]);
		}
	}

	internal static float[] ReadSingleArray(BinaryReader reader)
	{
		var count = reader.ReadInt32();
		var values = new float[count];
		for (var i = 0; i < count; i++)
		{
			values[i] = reader.ReadSingle();
		}

		return values;
	}

	internal static void WriteStringArray(BinaryWriter writer, IReadOnlyList<string> values)
	{
		writer.Write(values.Count);
		for (var i = 0; i < values.Count; i++)
		{
			writer.Write(values[i] ?? string.Empty);
		}
	}

	internal static string[] ReadStringArray(BinaryReader reader)
	{
		var count = reader.ReadInt32();
		var values = new string[count];
		for (var i = 0; i < count; i++)
		{
			values[i] = reader.ReadString();
		}

		return values;
	}

	internal static void WriteVector2Array(BinaryWriter writer, IReadOnlyList<Vector2> values)
	{
		writer.Write(values.Count);
		for (var i = 0; i < values.Count; i++)
		{
			writer.Write(values[i].X);
			writer.Write(values[i].Y);
		}
	}

	internal static Vector2[] ReadVector2Array(BinaryReader reader)
	{
		var count = reader.ReadInt32();
		var values = new Vector2[count];
		for (var i = 0; i < count; i++)
		{
			values[i] = new Vector2(reader.ReadSingle(), reader.ReadSingle());
		}

		return values;
	}

	internal static void WriteVector3Array(BinaryWriter writer, IReadOnlyList<Vector3> values)
	{
		writer.Write(values.Count);
		for (var i = 0; i < values.Count; i++)
		{
			writer.Write(values[i].X);
			writer.Write(values[i].Y);
			writer.Write(values[i].Z);
		}
	}

	internal static Vector3[] ReadVector3Array(BinaryReader reader)
	{
		var count = reader.ReadInt32();
		var values = new Vector3[count];
		for (var i = 0; i < count; i++)
		{
			values[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
		}

		return values;
	}

	internal static void WriteVector4Array(BinaryWriter writer, IReadOnlyList<Vector4> values)
	{
		writer.Write(values.Count);
		for (var i = 0; i < values.Count; i++)
		{
			writer.Write(values[i].X);
			writer.Write(values[i].Y);
			writer.Write(values[i].Z);
			writer.Write(values[i].W);
		}
	}

	internal static Vector4[] ReadVector4Array(BinaryReader reader)
	{
		var count = reader.ReadInt32();
		var values = new Vector4[count];
		for (var i = 0; i < count; i++)
		{
			values[i] = new Vector4(
				reader.ReadSingle(),
				reader.ReadSingle(),
				reader.ReadSingle(),
				reader.ReadSingle());
		}

		return values;
	}

	internal static void WriteQuaternionArray(BinaryWriter writer, IReadOnlyList<Quaternion> values)
	{
		writer.Write(values.Count);
		for (var i = 0; i < values.Count; i++)
		{
			writer.Write(values[i].X);
			writer.Write(values[i].Y);
			writer.Write(values[i].Z);
			writer.Write(values[i].W);
		}
	}

	internal static Quaternion[] ReadQuaternionArray(BinaryReader reader)
	{
		var count = reader.ReadInt32();
		var values = new Quaternion[count];
		for (var i = 0; i < count; i++)
		{
			values[i] = new Quaternion(
				reader.ReadSingle(),
				reader.ReadSingle(),
				reader.ReadSingle(),
				reader.ReadSingle());
		}

		return values;
	}

	internal static void WriteMatrix4x4(BinaryWriter writer, in Matrix4x4 value)
	{
		writer.Write(value.M11); writer.Write(value.M12); writer.Write(value.M13); writer.Write(value.M14);
		writer.Write(value.M21); writer.Write(value.M22); writer.Write(value.M23); writer.Write(value.M24);
		writer.Write(value.M31); writer.Write(value.M32); writer.Write(value.M33); writer.Write(value.M34);
		writer.Write(value.M41); writer.Write(value.M42); writer.Write(value.M43); writer.Write(value.M44);
	}

	internal static Matrix4x4 ReadMatrix4x4(BinaryReader reader) =>
		new(
			reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
			reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
			reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
			reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

	internal static void WriteMatrix4x4Array(BinaryWriter writer, IReadOnlyList<Matrix4x4> values)
	{
		writer.Write(values.Count);
		for (var i = 0; i < values.Count; i++)
		{
			WriteMatrix4x4(writer, values[i]);
		}
	}

	internal static Matrix4x4[] ReadMatrix4x4Array(BinaryReader reader)
	{
		var count = reader.ReadInt32();
		var values = new Matrix4x4[count];
		for (var i = 0; i < count; i++)
		{
			values[i] = ReadMatrix4x4(reader);
		}

		return values;
	}

	/// <summary>Writes to a temporary file and moves it into place, so a failed write cannot leave a torn artifact.</summary>
	internal static void WriteFileAtomically(string path, Action<BinaryWriter> write)
	{
		var directory = Path.GetDirectoryName(path);
		if (string.IsNullOrWhiteSpace(directory) == false)
		{
			Directory.CreateDirectory(directory);
		}

		var tempPath = path + ".tmp";
		using (var stream = File.Create(tempPath))
		using (var writer = new BinaryWriter(stream))
		{
			write(writer);
		}

		File.Move(tempPath, path, true);
	}
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace WolfEngine.AssetPipeline;

public static class ImportedMeshSerializer
{
	private static ReadOnlySpan<byte> Magic => "WEMH"u8;
	public const int CurrentVersion = 1;

	public static void Write(string path, ImportedMeshAssetFile mesh)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			throw new ArgumentException("Output path cannot be null or empty.", nameof(path));
		}

		ArgumentNullException.ThrowIfNull(mesh);

		var directory = Path.GetDirectoryName(path);
		if (string.IsNullOrWhiteSpace(directory) == false)
		{
			Directory.CreateDirectory(directory);
		}

		var tempPath = path + ".tmp";
		using (var stream = File.Create(tempPath))
		using (var writer = new BinaryWriter(stream))
		{
			writer.Write(Magic);
			writer.Write(CurrentVersion);
			WriteVector4Array(writer, mesh.Vertices);
			WriteUInt32Array(writer, mesh.Indices);
			WriteVector3Array(writer, mesh.Normals);
			WriteVector4Array(writer, mesh.Tangents);
			WriteVector2Array(writer, mesh.UVs);
		}

		File.Move(tempPath, path, true);
	}

	public static ImportedMeshAssetFile Read(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			throw new ArgumentException("Input path cannot be null or empty.", nameof(path));
		}

		if (File.Exists(path) == false)
		{
			throw new FileNotFoundException($"Imported mesh artifact '{path}' was not found.", path);
		}

		using var stream = File.OpenRead(path);
		using var reader = new BinaryReader(stream);
		var magic = reader.ReadBytes(Magic.Length);
		if (magic.AsSpan().SequenceEqual(Magic) == false)
		{
			throw new InvalidOperationException($"Imported mesh artifact '{path}' has an invalid header.");
		}

		var version = reader.ReadInt32();
		if (version != CurrentVersion)
		{
			throw new InvalidOperationException(
				$"Unsupported imported mesh artifact version {version}. Expected {CurrentVersion}.");
		}

		return new ImportedMeshAssetFile
		{
			Vertices = ReadVector4Array(reader),
			Indices = ReadUInt32Array(reader),
			Normals = ReadVector3Array(reader),
			Tangents = ReadVector4Array(reader),
			UVs = ReadVector2Array(reader)
		};
	}

	private static void WriteUInt32Array(BinaryWriter writer, IReadOnlyList<uint> values)
	{
		writer.Write(values.Count);
		for (var i = 0; i < values.Count; i++)
		{
			writer.Write(values[i]);
		}
	}

	private static uint[] ReadUInt32Array(BinaryReader reader)
	{
		var count = reader.ReadInt32();
		var values = new uint[count];
		for (var i = 0; i < count; i++)
		{
			values[i] = reader.ReadUInt32();
		}

		return values;
	}

	private static void WriteVector2Array(BinaryWriter writer, IReadOnlyList<Vector2> values)
	{
		writer.Write(values.Count);
		for (var i = 0; i < values.Count; i++)
		{
			writer.Write(values[i].X);
			writer.Write(values[i].Y);
		}
	}

	private static Vector2[] ReadVector2Array(BinaryReader reader)
	{
		var count = reader.ReadInt32();
		var values = new Vector2[count];
		for (var i = 0; i < count; i++)
		{
			values[i] = new Vector2(
				reader.ReadSingle(),
				reader.ReadSingle());
		}

		return values;
	}

	private static void WriteVector3Array(BinaryWriter writer, IReadOnlyList<Vector3> values)
	{
		writer.Write(values.Count);
		for (var i = 0; i < values.Count; i++)
		{
			writer.Write(values[i].X);
			writer.Write(values[i].Y);
			writer.Write(values[i].Z);
		}
	}

	private static Vector3[] ReadVector3Array(BinaryReader reader)
	{
		var count = reader.ReadInt32();
		var values = new Vector3[count];
		for (var i = 0; i < count; i++)
		{
			values[i] = new Vector3(
				reader.ReadSingle(),
				reader.ReadSingle(),
				reader.ReadSingle());
		}

		return values;
	}

	private static void WriteVector4Array(BinaryWriter writer, IReadOnlyList<Vector4> values)
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

	private static Vector4[] ReadVector4Array(BinaryReader reader)
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
}

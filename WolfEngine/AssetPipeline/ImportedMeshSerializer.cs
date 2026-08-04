using System;
using System.IO;

namespace WolfEngine.AssetPipeline;

public static class ImportedMeshSerializer
{
	private static ReadOnlySpan<byte> Magic => "WEMH"u8;

	/// <summary>Version 2 appends per-vertex skin influences. Version 1 artifacts read back as unskinned.</summary>
	public const int CurrentVersion = 2;

	private const int SkinnedVersion = 2;

	public static void Write(string path, ImportedMeshAssetFile mesh)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		ArgumentNullException.ThrowIfNull(mesh);

		AssetBinaryPrimitives.WriteFileAtomically(path, writer =>
		{
			writer.Write(Magic);
			writer.Write(CurrentVersion);
			AssetBinaryPrimitives.WriteVector4Array(writer, mesh.Vertices);
			AssetBinaryPrimitives.WriteUInt32Array(writer, mesh.Indices);
			AssetBinaryPrimitives.WriteVector3Array(writer, mesh.Normals);
			AssetBinaryPrimitives.WriteVector4Array(writer, mesh.Tangents);
			AssetBinaryPrimitives.WriteVector2Array(writer, mesh.UVs);
			AssetBinaryPrimitives.WriteUInt32Array(writer, mesh.BoneIndices ?? []);
			AssetBinaryPrimitives.WriteSingleArray(writer, mesh.BoneWeights ?? []);
		});
	}

	public static ImportedMeshAssetFile Read(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		if (File.Exists(path) == false)
		{
			throw new FileNotFoundException($"Imported mesh artifact '{path}' was not found.", path);
		}

		using var stream = File.OpenRead(path);
		return Read(stream);
	}

	public static ImportedMeshAssetFile Read(Stream stream)
	{
		ArgumentNullException.ThrowIfNull(stream);
		using var reader = new BinaryReader(stream);
		var magic = reader.ReadBytes(Magic.Length);
		if (magic.AsSpan().SequenceEqual(Magic) == false)
		{
			throw new InvalidOperationException("Imported mesh artifact has an invalid header.");
		}

		var version = reader.ReadInt32();
		if (version is < 1 or > CurrentVersion)
		{
			throw new InvalidOperationException(
				$"Unsupported imported mesh artifact version {version}. Expected 1 to {CurrentVersion}.");
		}

		var mesh = new ImportedMeshAssetFile
		{
			Version = version,
			Vertices = AssetBinaryPrimitives.ReadVector4Array(reader),
			Indices = AssetBinaryPrimitives.ReadUInt32Array(reader),
			Normals = AssetBinaryPrimitives.ReadVector3Array(reader),
			Tangents = AssetBinaryPrimitives.ReadVector4Array(reader),
			UVs = AssetBinaryPrimitives.ReadVector2Array(reader)
		};

		if (version >= SkinnedVersion)
		{
			mesh.BoneIndices = AssetBinaryPrimitives.ReadUInt32Array(reader);
			mesh.BoneWeights = AssetBinaryPrimitives.ReadSingleArray(reader);
		}

		return mesh;
	}
}

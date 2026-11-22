namespace WolfEngine.AssetPipeline;

public static class ImportedMeshSerializer
{
	private static ReadOnlySpan<byte> Magic => "WEMH"u8;

	/// <summary>
	/// Version 3 adds shared optional shadow index streams; versions 1 and 2 use main indices. Version 4 adds the
	/// mesh assets of coarser levels of detail, and version 5 their authored switch thresholds.
	/// </summary>
	public const int CurrentVersion = 5;

	private const int SkinnedVersion = 2;

	private const int ShadowStreamsVersion = 3;

	private const int LodLevelsVersion = 4;

	private const int LodScreenSizesVersion = 5;

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
			var opaque = mesh.ShadowOpaqueIndices;
			var alpha = mesh.ShadowAlphaTestIndices;
			AssetBinaryPrimitives.WriteUInt32Array(writer,
				opaque.AsSpan().SequenceEqual(mesh.Indices) ? [] : opaque);
			var alphaSharesOpaque = alpha.Length > 0 && alpha.AsSpan().SequenceEqual(opaque);
			writer.Write(alphaSharesOpaque);
			AssetBinaryPrimitives.WriteUInt32Array(writer,
				alphaSharesOpaque || alpha.AsSpan().SequenceEqual(mesh.Indices) ? [] : alpha);

			var lodLevelNodeIds = mesh.LodLevelNodeIds ?? [];
			writer.Write(lodLevelNodeIds.Length);
			foreach (var lodLevelNodeId in lodLevelNodeIds)
			{
				writer.Write(lodLevelNodeId.ToByteArray());
			}

			AssetBinaryPrimitives.WriteSingleArray(writer, mesh.LodScreenSizes ?? []);
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

		if (version >= ShadowStreamsVersion)
		{
			mesh.ShadowOpaqueIndices = AssetBinaryPrimitives.ReadUInt32Array(reader);
			var alphaSharesOpaque = reader.ReadBoolean();
			mesh.ShadowAlphaTestIndices = AssetBinaryPrimitives.ReadUInt32Array(reader);
			if (alphaSharesOpaque) mesh.ShadowAlphaTestIndices = mesh.ShadowOpaqueIndices;
		}

		if (version >= LodLevelsVersion)
		{
			var lodLevelCount = reader.ReadInt32();
			if (lodLevelCount < 0)
			{
				throw new InvalidOperationException($"Imported mesh artifact has an invalid level count {lodLevelCount}.");
			}

			mesh.LodLevelNodeIds = new Guid[lodLevelCount];
			for (var i = 0; i < lodLevelCount; i++)
			{
				mesh.LodLevelNodeIds[i] = new Guid(reader.ReadBytes(16));
			}
		}

		if (version >= LodScreenSizesVersion)
		{
			mesh.LodScreenSizes = AssetBinaryPrimitives.ReadSingleArray(reader);
		}

		return mesh;
	}
}

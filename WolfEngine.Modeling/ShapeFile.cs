using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace WolfEngine.Modeling;

/// <summary>
/// Reads and writes <c>.shape</c> source files.
/// <para>
/// Layout, little-endian: the magic <c>WSHP</c>, a format version, a chunk count, then the chunks. Each chunk is a
/// four-character tag, its own version, a byte length and its payload. Readers skip chunks whose tag they do not
/// know, so new kinds of data (hints, LODs, the UV carrier) arrive as new chunks rather than as a format break.
/// A chunk whose version is newer than the reader understands fails the read rather than being misread.
/// </para>
/// </summary>
public static class ShapeFile
{
	public const string Extension = ".shape";
	public const int FormatVersion = 1;

	private const uint Magic = 0x50485357; // "WSHP"
	private const int TreeChunkVersion = 1;
	private const int SettingsChunkVersion = 1;
	private const int MeshChunkVersion = 1;
	private const int MaxNodeDepth = 64;

	private static readonly uint TreeTag = Tag("TREE");
	private static readonly uint SettingsTag = Tag("SETT");
	private static readonly uint MeshTag = Tag("MESH");

	public static bool IsShapePath(string path)
	{
		return path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>The file name without its extension.</summary>
	public static string GetShapeName(string path)
	{
		var name = Path.GetFileName(path);
		return IsShapePath(name) ? name[..^Extension.Length] : Path.GetFileNameWithoutExtension(name);
	}

	public static ShapeAsset Read(string path)
	{
		using var stream = File.OpenRead(path);
		return Read(stream);
	}

	public static ShapeAsset Read(Stream stream)
	{
		using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
		if (reader.ReadUInt32() != Magic)
		{
			throw new InvalidDataException("Not a shape file.");
		}

		var version = reader.ReadInt32();
		if (version > FormatVersion)
		{
			throw new InvalidDataException($"Shape file version {version} is newer than this editor understands ({FormatVersion}).");
		}

		var asset = new ShapeAsset();
		var hasTree = false;
		var chunkCount = reader.ReadInt32();
		for (var i = 0; i < chunkCount; i++)
		{
			var tag = reader.ReadUInt32();
			var chunkVersion = reader.ReadInt32();
			var length = reader.ReadInt64();
			var end = stream.Position + length;

			if (tag == TreeTag)
			{
				RequireVersion("TREE", chunkVersion, TreeChunkVersion);
				asset.Tree = new ShapeTree(ReadNode(reader, 0));
				hasTree = true;
			}
			else if (tag == SettingsTag)
			{
				RequireVersion("SETT", chunkVersion, SettingsChunkVersion);
				asset.Settings = ReadSettings(reader);
			}
			else if (tag == MeshTag)
			{
				RequireVersion("MESH", chunkVersion, MeshChunkVersion);
				asset.MeshSourceHash = reader.ReadUInt64();
				asset.Mesh = ReadMesh(reader);
			}

			if (stream.Position > end)
			{
				throw new InvalidDataException($"Chunk {TagName(tag)} is longer than its header says.");
			}

			stream.Position = end;
		}

		if (hasTree == false)
		{
			throw new InvalidDataException("The shape file has no operation tree.");
		}

		return asset;
	}

	/// <summary>Writes the whole file next to the destination, then moves it into place, so a failed save never leaves half a file.</summary>
	public static void Write(string path, ShapeAsset asset)
	{
		ArgumentNullException.ThrowIfNull(asset);

		var directory = Path.GetDirectoryName(path);
		if (string.IsNullOrEmpty(directory) == false)
		{
			Directory.CreateDirectory(directory);
		}

		var temporaryPath = path + ".tmp";
		using (var stream = File.Create(temporaryPath))
		{
			Write(stream, asset);
		}

		File.Move(temporaryPath, path, overwrite: true);
	}

	public static void Write(Stream stream, ShapeAsset asset)
	{
		ArgumentNullException.ThrowIfNull(asset);

		var chunks = new List<(uint Tag, int Version, byte[] Payload)>
		{
			(TreeTag, TreeChunkVersion, WritePayload(writer => WriteNode(writer, asset.Tree.Root))),
			(SettingsTag, SettingsChunkVersion, WritePayload(writer => WriteSettings(writer, asset.Settings)))
		};

		if (asset.Mesh is { } mesh)
		{
			chunks.Add((MeshTag, MeshChunkVersion, WritePayload(writer =>
			{
				writer.Write(asset.MeshSourceHash);
				WriteMesh(writer, mesh);
			})));
		}

		using var output = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
		output.Write(Magic);
		output.Write(FormatVersion);
		output.Write(chunks.Count);
		foreach (var (tag, version, payload) in chunks)
		{
			output.Write(tag);
			output.Write(version);
			output.Write((long)payload.Length);
			output.Write(payload);
		}
	}

	/// <summary>
	/// A hash of everything that decides the compiled geometry: the tree and the compile settings, but not the
	/// material. A saved mesh whose hash differs was compiled from an older version of the shape.
	/// </summary>
	public static ulong ComputeSourceHash(ShapeTree tree, ShapeCompileSettings settings)
	{
		ArgumentNullException.ThrowIfNull(tree);
		ArgumentNullException.ThrowIfNull(settings);

		var geometrySettings = settings.Clone();
		geometrySettings.MaterialId = Guid.Empty;
		var bytes = WritePayload(writer =>
		{
			WriteNode(writer, tree.Root);
			WriteSettings(writer, geometrySettings);
		});

		// FNV-1a: stable across runs and processes, unlike string.GetHashCode.
		var hash = 14695981039346656037UL;
		foreach (var value in bytes)
		{
			hash ^= value;
			hash *= 1099511628211UL;
		}

		return hash;
	}

	private static byte[] WritePayload(Action<BinaryWriter> write)
	{
		using var memory = new MemoryStream();
		using (var writer = new BinaryWriter(memory, Encoding.UTF8, leaveOpen: true))
		{
			write(writer);
		}

		return memory.ToArray();
	}

	private static void WriteNode(BinaryWriter writer, ShapeNode node)
	{
		writer.Write((byte)node.Kind);
		writer.Write(node.Id.ToByteArray());
		writer.Write(node.Name);
		writer.Write((byte)node.Operation);
		writer.Write(node.Smoothness);
		WriteVector3(writer, node.Position);
		writer.Write(node.Rotation.X);
		writer.Write(node.Rotation.Y);
		writer.Write(node.Rotation.Z);
		writer.Write(node.Rotation.W);
		writer.Write(node.Scale);
		writer.Write((byte)node.Primitive);
		writer.Write(node.Dimensions.X);
		writer.Write(node.Dimensions.Y);
		writer.Write(node.Dimensions.Z);
		writer.Write(node.Dimensions.W);
		writer.Write(node.Children.Count);
		foreach (var child in node.Children)
		{
			WriteNode(writer, child);
		}
	}

	private static ShapeNode ReadNode(BinaryReader reader, int depth)
	{
		if (depth > MaxNodeDepth)
		{
			throw new InvalidDataException("The shape tree is nested too deeply.");
		}

		var node = new ShapeNode
		{
			Kind = ReadEnum<ShapeNodeKind>(reader.ReadByte()),
			Id = new Guid(reader.ReadBytes(16)),
			Name = reader.ReadString(),
			Operation = ReadEnum<ShapeOperation>(reader.ReadByte()),
			Smoothness = reader.ReadSingle(),
			Position = ReadVector3(reader),
			Rotation = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
			Scale = reader.ReadSingle(),
			Primitive = ReadEnum<ShapePrimitiveKind>(reader.ReadByte()),
			Dimensions = new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle())
		};

		var childCount = reader.ReadInt32();
		if (childCount < 0)
		{
			throw new InvalidDataException("A shape node has a negative child count.");
		}

		if (childCount > 0 && node.IsGroup == false)
		{
			throw new InvalidDataException($"Primitive '{node.Name}' has children; only groups may.");
		}

		for (var i = 0; i < childCount; i++)
		{
			node.Children.Add(ReadNode(reader, depth + 1));
		}

		return node;
	}

	private static void WriteSettings(BinaryWriter writer, ShapeCompileSettings settings)
	{
		writer.Write(settings.ReferenceResolution);
		writer.Write(settings.TriangleBudget);
		writer.Write(settings.ErrorTolerance);
		writer.Write(settings.CreaseAngleDegrees);
		writer.Write(settings.MaterialId.ToByteArray());
	}

	private static ShapeCompileSettings ReadSettings(BinaryReader reader)
	{
		return new ShapeCompileSettings
		{
			ReferenceResolution = reader.ReadInt32(),
			TriangleBudget = reader.ReadInt32(),
			ErrorTolerance = reader.ReadSingle(),
			CreaseAngleDegrees = reader.ReadSingle(),
			MaterialId = new Guid(reader.ReadBytes(16))
		};
	}

	private static void WriteMesh(BinaryWriter writer, ShapeMesh mesh)
	{
		writer.Write(mesh.VertexCount);
		foreach (var position in mesh.Positions)
		{
			WriteVector3(writer, position);
		}

		foreach (var normal in mesh.Normals)
		{
			WriteVector3(writer, normal);
		}

		writer.Write(mesh.Indices.Length);
		foreach (var index in mesh.Indices)
		{
			writer.Write(index);
		}
	}

	private static ShapeMesh ReadMesh(BinaryReader reader)
	{
		var vertexCount = reader.ReadInt32();
		if (vertexCount < 0)
		{
			throw new InvalidDataException("The compiled mesh has a negative vertex count.");
		}

		var positions = new Vector3[vertexCount];
		for (var i = 0; i < vertexCount; i++)
		{
			positions[i] = ReadVector3(reader);
		}

		var normals = new Vector3[vertexCount];
		for (var i = 0; i < vertexCount; i++)
		{
			normals[i] = ReadVector3(reader);
		}

		var indexCount = reader.ReadInt32();
		if (indexCount < 0 || indexCount % 3 != 0)
		{
			throw new InvalidDataException("The compiled mesh does not hold whole triangles.");
		}

		var indices = new uint[indexCount];
		for (var i = 0; i < indexCount; i++)
		{
			indices[i] = reader.ReadUInt32();
			if (indices[i] >= vertexCount)
			{
				throw new InvalidDataException("The compiled mesh refers to a vertex it does not have.");
			}
		}

		return new ShapeMesh(positions, normals, indices);
	}

	private static void WriteVector3(BinaryWriter writer, Vector3 value)
	{
		writer.Write(value.X);
		writer.Write(value.Y);
		writer.Write(value.Z);
	}

	private static Vector3 ReadVector3(BinaryReader reader)
	{
		return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
	}

	private static T ReadEnum<T>(byte value) where T : struct, Enum
	{
		var result = (T)Enum.ToObject(typeof(T), value);
		if (Enum.IsDefined(result) == false)
		{
			throw new InvalidDataException($"{value} is not a valid {typeof(T).Name}.");
		}

		return result;
	}

	private static void RequireVersion(string chunk, int version, int supported)
	{
		if (version > supported)
		{
			throw new InvalidDataException($"Chunk {chunk} version {version} is newer than this editor understands ({supported}).");
		}
	}

	private static uint Tag(string text)
	{
		return BinaryPrimitives.ReadUInt32LittleEndian(Encoding.ASCII.GetBytes(text));
	}

	private static string TagName(uint tag)
	{
		Span<byte> bytes = stackalloc byte[4];
		BinaryPrimitives.WriteUInt32LittleEndian(bytes, tag);
		return Encoding.ASCII.GetString(bytes);
	}
}

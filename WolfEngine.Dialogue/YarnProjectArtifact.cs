using System.Text;
using Google.Protobuf;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Dialogue;

/// <summary>
/// The cooked form of a <see cref="YarnProject"/>: the compiled program's protobuf bytes followed by the base-language
/// string table. The same file is read by the editor from the Library and by games from their content pack.
/// </summary>
public static class YarnProjectArtifact
{
	private static ReadOnlySpan<byte> Magic => "WYRN"u8;
	private const int Version = 1;

	public static void Write(string path, Yarn.Program program, IReadOnlyDictionary<string, string> lines)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
		using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
		Write(stream, program, lines);
	}

	public static void Write(Stream stream, Yarn.Program program, IReadOnlyDictionary<string, string> lines)
	{
		ArgumentNullException.ThrowIfNull(stream);
		ArgumentNullException.ThrowIfNull(program);
		ArgumentNullException.ThrowIfNull(lines);

		using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
		writer.Write(Magic);
		writer.Write(Version);
		var programBytes = program.ToByteArray();
		writer.Write(programBytes.Length);
		writer.Write(programBytes);

		// Sorted so reimporting an unchanged project writes the same bytes and the same content hash.
		var ordered = lines.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
		writer.Write(ordered.Length);
		foreach (var (id, text) in ordered)
		{
			writer.Write(id);
			writer.Write(text);
		}
	}

	public static YarnProject Read(string path, string name)
	{
		using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
		return Read(stream, name);
	}

	public static YarnProject Read(Stream stream, string name)
	{
		ArgumentNullException.ThrowIfNull(stream);
		using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
		if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic))
			throw new InvalidDataException("Not a cooked Yarn project.");
		var version = reader.ReadInt32();
		if (version != Version)
			throw new InvalidDataException($"Cooked Yarn project version {version} is not supported; reimport it.");

		var program = Yarn.Program.Parser.ParseFrom(reader.ReadBytes(reader.ReadInt32()));
		var count = reader.ReadInt32();
		var lines = new Dictionary<string, string>(count, StringComparer.Ordinal);
		for (var i = 0; i < count; i++)
		{
			var id = reader.ReadString();
			lines[id] = reader.ReadString();
		}

		return new YarnProject(name, program, lines);
	}
}

/// <summary>Loads a Yarn project from its imported artifact, for the editor and for games run from it.</summary>
public sealed class YarnProjectRuntimeResolver : IYarnProjectRuntimeResolver
{
	public object Resolve(RuntimeAssetResolveContext context)
	{
		var artifact = context.Asset.Artifacts.FirstOrDefault(item =>
			               string.Equals(item.Kind, YarnProjectConstants.RuntimeArtifactKind, StringComparison.Ordinal))
		               ?? throw new InvalidDataException($"Yarn project '{context.Asset.Name}' has no runtime artifact.");
		return YarnProjectArtifact.Read(context.GetAbsolutePath(artifact.RelativePath), context.Asset.Name);
	}
}

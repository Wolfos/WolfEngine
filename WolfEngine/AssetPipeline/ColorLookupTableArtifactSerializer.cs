using System.Numerics;
using WolfEngine.Importing;

namespace WolfEngine.AssetPipeline;

/// <summary>
/// Cooked form of a colour lookup table: the lattice as RGBA16F texels in x/y/z (red-fastest) order,
/// ready to upload as a volume texture without further conversion.
/// </summary>
public sealed record ColorLookupTableArtifact(
	string Title,
	int Size,
	Vector3 DomainMin,
	Vector3 DomainMax,
	byte[] Rgba16FloatTexels);

public static class ColorLookupTableArtifactSerializer
{
	private static ReadOnlySpan<byte> Magic => "WECL"u8;
	public const int CurrentVersion = 1;
	public const string ArtifactKind = "RuntimeColorLookupTable";
	public const int BytesPerTexel = 8;

	public static ColorLookupTableArtifact CreateArtifact(CubeLut lut)
	{
		ArgumentNullException.ThrowIfNull(lut);

		var texels = new byte[lut.Values.Length * BytesPerTexel];
		var span = texels.AsSpan();
		for (var i = 0; i < lut.Values.Length; i++)
		{
			var value = lut.Values[i];
			var texel = span.Slice(i * BytesPerTexel, BytesPerTexel);
			BitConverter.TryWriteBytes(texel[..2], (Half)value.X);
			BitConverter.TryWriteBytes(texel.Slice(2, 2), (Half)value.Y);
			BitConverter.TryWriteBytes(texel.Slice(4, 2), (Half)value.Z);
			BitConverter.TryWriteBytes(texel.Slice(6, 2), Half.One);
		}

		return new ColorLookupTableArtifact(lut.Title, lut.Size, lut.DomainMin, lut.DomainMax, texels);
	}

	public static void Write(string path, ColorLookupTableArtifact artifact)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		ArgumentNullException.ThrowIfNull(artifact);

		var directory = Path.GetDirectoryName(path);
		if (string.IsNullOrWhiteSpace(directory) == false)
		{
			Directory.CreateDirectory(directory);
		}

		var tempPath = path + ".tmp";
		using (var stream = File.Create(tempPath))
		{
			Write(stream, artifact);
		}

		File.Move(tempPath, path, true);
	}

	public static void Write(Stream stream, ColorLookupTableArtifact artifact)
	{
		ArgumentNullException.ThrowIfNull(stream);
		ArgumentNullException.ThrowIfNull(artifact);
		var expectedLength = (long)artifact.Size * artifact.Size * artifact.Size * BytesPerTexel;
		if (artifact.Rgba16FloatTexels.LongLength != expectedLength)
		{
			throw new ArgumentException(
				$"A {artifact.Size}^3 LUT needs {expectedLength} texel bytes, got {artifact.Rgba16FloatTexels.LongLength}.",
				nameof(artifact));
		}

		using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
		writer.Write(Magic);
		writer.Write(CurrentVersion);
		writer.Write(artifact.Title);
		writer.Write(artifact.Size);
		WriteVector(writer, artifact.DomainMin);
		WriteVector(writer, artifact.DomainMax);
		writer.Write(artifact.Rgba16FloatTexels.Length);
		writer.Write(artifact.Rgba16FloatTexels);
	}

	public static ColorLookupTableArtifact Read(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		if (File.Exists(path) == false)
		{
			throw new FileNotFoundException($"Colour lookup table artifact '{path}' was not found.", path);
		}

		using var stream = File.OpenRead(path);
		return Read(stream);
	}

	public static ColorLookupTableArtifact Read(Stream stream)
	{
		ArgumentNullException.ThrowIfNull(stream);
		using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
		var magic = reader.ReadBytes(Magic.Length);
		if (magic.AsSpan().SequenceEqual(Magic) == false)
		{
			throw new InvalidOperationException("Colour lookup table artifact has an invalid header.");
		}

		var version = reader.ReadInt32();
		if (version != CurrentVersion)
		{
			throw new InvalidOperationException(
				$"Unsupported colour lookup table artifact version {version}. Expected {CurrentVersion}.");
		}

		var title = reader.ReadString();
		var size = reader.ReadInt32();
		if (size < CubeLutParser.MinSize || size > CubeLutParser.MaxSize)
		{
			throw new InvalidOperationException($"Colour lookup table artifact has an invalid size {size}.");
		}

		var domainMin = ReadVector(reader);
		var domainMax = ReadVector(reader);
		var length = reader.ReadInt32();
		if (length != size * size * size * BytesPerTexel)
		{
			throw new InvalidOperationException("Colour lookup table artifact texel data does not match its size.");
		}

		var texels = reader.ReadBytes(length);
		if (texels.Length != length)
		{
			throw new EndOfStreamException("Colour lookup table artifact is truncated.");
		}

		return new ColorLookupTableArtifact(title, size, domainMin, domainMax, texels);
	}

	private static void WriteVector(BinaryWriter writer, Vector3 value)
	{
		writer.Write(value.X);
		writer.Write(value.Y);
		writer.Write(value.Z);
	}

	private static Vector3 ReadVector(BinaryReader reader) =>
		new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
}

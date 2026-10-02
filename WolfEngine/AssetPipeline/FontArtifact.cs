using System.Text.Json;

namespace WolfEngine.AssetPipeline;

/// <summary>MTSDF RGBA8 pixels are linear data, with rows stored top-down; glyph plane bounds are Y-up em units.</summary>
public sealed record FontArtifact
{
	public const int CurrentVersion = 1;
	public FontAssetSummary Metrics { get; init; } = new();
	public FontGlyph[] Glyphs { get; init; } = [];
	public Dictionary<int, uint> CharacterMap { get; init; } = [];
	public byte[] PixelsRgba { get; init; } = [];
	/// <summary>Original OpenType data preserves the glyph ID space and default variable-font instance for shaping.</summary>
	public byte[] FontData { get; init; } = [];
}

public sealed record FontAssetSummary
{
	public string Name { get; init; } = "";
	public string SourceHash { get; init; } = "";
	public string Compiler { get; init; } = "Remora.MSDFGen/1.0.0;HarfBuzzSharp/14.2.1.102;WolfFont/1";
	public int UnitsPerEm { get; init; }
	public float Ascender { get; init; }
	public float Descender { get; init; }
	public float LineHeight { get; init; }
	public int PixelsPerEm { get; init; }
	public int DistanceRange { get; init; }
	public int Width { get; init; }
	public int Height { get; init; }
	public int GlyphCount { get; init; }
	public string Coverage { get; init; } = "Basic Latin (U+0020–U+007E), .notdef, and preview shaping glyphs";
}

public sealed record FontGlyph(uint Id, float Advance, float Left, float Bottom, float Right, float Top,
	int AtlasX, int AtlasY, int AtlasWidth, int AtlasHeight);

public static class FontArtifactSerializer
{
	private const uint Magic = 0x544E4657; // WFNT
	private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

	public static void Write(string path, FontArtifact artifact)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
		using var stream = File.Create(path);
		using var writer = new BinaryWriter(stream);
		writer.Write(Magic);
		writer.Write(FontArtifact.CurrentVersion);
		var metadata = JsonSerializer.SerializeToUtf8Bytes(artifact with { PixelsRgba = [], FontData = [] }, Json);
		WriteBlock(writer, metadata);
		WriteBlock(writer, artifact.PixelsRgba);
		WriteBlock(writer, artifact.FontData);
	}

	public static FontArtifact Read(string path)
	{
		using var stream = File.OpenRead(path);
		return Read(stream);
	}

	public static FontArtifact Read(Stream stream)
	{
		using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
		if (reader.ReadUInt32() != Magic || reader.ReadInt32() != FontArtifact.CurrentVersion)
			throw new InvalidDataException("Unsupported font artifact header.");
		var artifact = JsonSerializer.Deserialize<FontArtifact>(ReadBlock(reader, 4 * 1024 * 1024), Json)
		               ?? throw new InvalidDataException("Missing font metadata.");
		var pixels = ReadBlock(reader, 4096 * 4096 * 4);
		var font = ReadBlock(reader, 64 * 1024 * 1024);
		if (artifact.Metrics.Width <= 0 || artifact.Metrics.Height <= 0 ||
		    pixels.LongLength != (long)artifact.Metrics.Width * artifact.Metrics.Height * 4 ||
		    artifact.Metrics.GlyphCount != artifact.Glyphs.Length || stream.Position != stream.Length)
			throw new InvalidDataException("Invalid font artifact dimensions or payload.");
		return artifact with { PixelsRgba = pixels, FontData = font };
	}

	private static void WriteBlock(BinaryWriter writer, byte[] bytes)
	{
		writer.Write(bytes.Length);
		writer.Write(bytes);
	}

	private static byte[] ReadBlock(BinaryReader reader, int limit)
	{
		var length = reader.ReadInt32();
		if (length < 0 || length > limit || length > reader.BaseStream.Length - reader.BaseStream.Position)
			throw new InvalidDataException("Invalid font artifact block length.");
		return reader.ReadBytes(length);
	}
}

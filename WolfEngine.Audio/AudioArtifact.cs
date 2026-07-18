using System.Text;
using System.Text.Json;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Audio;

public sealed class AudioArtifactHeader
{
	public const int CurrentVersion = 1;
	public int Version { get; set; } = CurrentVersion;
	public string Codec { get; set; } = string.Empty;
	public AudioStorageMode StorageMode { get; set; }
	public int Channels { get; set; }
	public int SampleRate { get; set; }
	public long FrameCount { get; set; }
	public double DurationSeconds { get; set; }
}

public sealed class AudioContentStream : IDisposable
{
	public required AudioArtifactHeader Header { get; init; }
	public required Stream Payload { get; init; }
	public void Dispose() => Payload.Dispose();
}

public static class AudioArtifact
{
	private static ReadOnlySpan<byte> Magic => "WOLFAUD\0"u8;

	public static void Write(string path, AudioArtifactHeader header, Stream payload)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		ArgumentNullException.ThrowIfNull(header);
		ArgumentNullException.ThrowIfNull(payload);
		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
		using var destination = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
		WriteTo(destination, header, payload);
	}

	public static void WriteTo(Stream destination, AudioArtifactHeader header, Stream payload)
	{
		ArgumentNullException.ThrowIfNull(destination);
		ArgumentNullException.ThrowIfNull(header);
		ArgumentNullException.ThrowIfNull(payload);
		header.Version = AudioArtifactHeader.CurrentVersion;
		var json = JsonSerializer.SerializeToUtf8Bytes(header, AssetJson.SerializerOptions);
		using var writer = new BinaryWriter(destination, Encoding.UTF8, leaveOpen: true);
		writer.Write(Magic);
		writer.Write(json.Length);
		writer.Write(json);
		payload.CopyTo(destination);
	}

	public static AudioContentStream Open(Stream stream)
	{
		ArgumentNullException.ThrowIfNull(stream);
		try
		{
			using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
			if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic))
				throw new InvalidDataException("Invalid WolfEngine audio artifact header.");
			var headerLength = reader.ReadInt32();
			if (headerLength is <= 0 or > 1024 * 1024)
				throw new InvalidDataException("Invalid WolfEngine audio artifact metadata length.");
			var header = JsonSerializer.Deserialize<AudioArtifactHeader>(reader.ReadBytes(headerLength), AssetJson.SerializerOptions)
			             ?? throw new InvalidDataException("WolfEngine audio artifact metadata is missing.");
			if (header.Version != AudioArtifactHeader.CurrentVersion)
				throw new InvalidDataException($"Unsupported WolfEngine audio artifact version {header.Version}.");
			return new AudioContentStream { Header = header, Payload = stream };
		}
		catch
		{
			stream.Dispose();
			throw;
		}
	}

	public static AudioContentStream Open(string path) => Open(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
}

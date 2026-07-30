using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WolfEngine.AssetPipeline;

public sealed class WolfEngineBuildConfig
{
	public const int CurrentVersion = 2;
	public int Version { get; set; } = CurrentVersion;
	/// <summary>Scenes included in the game build. The first scene is launched by default.</summary>
	public List<Guid> SceneIds { get; set; } = [];
	// Retained for reading version 1 project settings and for the runtime bootstrap manifest.
	public Guid InitialSceneId { get; set; }
	public CookedRuntimeSettings RuntimeSettings { get; set; } = new();
	public string Configuration { get; set; } = "Release";
	public bool SelfContained { get; set; } = true;
	public string Target { get; set; } = "current-host";

	public IReadOnlyList<Guid> GetSceneIds()
	{
		return SceneIds.Count > 0
			? SceneIds.Where(id => id != Guid.Empty).Distinct().ToArray()
			: InitialSceneId == Guid.Empty ? [] : [InitialSceneId];
	}

	public void SetSceneIds(IEnumerable<Guid> sceneIds)
	{
		SceneIds = sceneIds.Where(id => id != Guid.Empty).Distinct().ToList();
		InitialSceneId = SceneIds.FirstOrDefault();
		Version = CurrentVersion;
	}
}

public sealed class CookedRuntimeSettings
{
	public string Title { get; set; } = "WolfEngine Game";
	public int Width { get; set; } = 1280;
	public int Height { get; set; } = 720;
	public float FixedDeltaTime { get; set; } = 1.0f / 60.0f;
	public int MaxPhysicsStepsPerFrame { get; set; } = 4;
	public int FrameLimit { get; set; }
}

public sealed class WolfBootstrapManifest
{
	public const int CurrentVersion = 1;
	public int Version { get; set; } = CurrentVersion;
	public string Target { get; set; } = string.Empty;
	public string RuntimeVersion { get; set; } = string.Empty;
	public string BuildConfiguration { get; set; } = "Release";
	public Guid InitialSceneId { get; set; }
	public Guid GameplayAssemblyId { get; set; }
	public Guid GameplaySymbolsId { get; set; }
	public Guid RuntimeSettingsId { get; set; }
	public List<WolfManifestPack> Packs { get; set; } = [];
	public List<WolfManifestRoot> Roots { get; set; } = [];
}

public sealed class WolfManifestPack
{
	public string Name { get; set; } = string.Empty;
	public string FileName { get; set; } = string.Empty;
	public string Sha256 { get; set; } = string.Empty;
	public long ByteSize { get; set; }
}

public sealed class WolfManifestRoot
{
	public string Kind { get; set; } = string.Empty;
	public Guid Id { get; set; }
	public List<Guid> Dependencies { get; set; } = [];
}

public readonly record struct WolfPackHeader(int Version, int EntryCount, long TableByteSize);

public sealed class WolfPackEntry
{
	public Guid Id { get; set; }
	public string Kind { get; set; } = string.Empty;
	public long Offset { get; set; }
	public long Length { get; set; }
	public string Sha256 { get; set; } = string.Empty;
	public List<Guid> Dependencies { get; set; } = [];
}

public readonly record struct WolfPackSource
{
	public WolfPackSource(Guid id, string kind, ReadOnlyMemory<byte> payload, IReadOnlyCollection<Guid> dependencies)
	{
		Id = id;
		Kind = kind;
		Payload = payload;
		Dependencies = dependencies;
		FilePath = null;
	}

	private WolfPackSource(Guid id, string kind, string filePath, IReadOnlyCollection<Guid> dependencies)
	{
		Id = id;
		Kind = kind;
		Payload = default;
		Dependencies = dependencies;
		FilePath = Path.GetFullPath(filePath);
	}

	public Guid Id { get; }
	public string Kind { get; }
	public ReadOnlyMemory<byte> Payload { get; }
	public IReadOnlyCollection<Guid> Dependencies { get; }
	public string? FilePath { get; }
	public long Length => FilePath is null ? Payload.Length : new FileInfo(FilePath).Length;
	public static WolfPackSource FromFile(Guid id, string kind, string filePath, IReadOnlyCollection<Guid> dependencies)
		=> new(id, kind, filePath, dependencies);

	internal string ComputeSha256()
	{
		if (FilePath is null) return Convert.ToHexString(SHA256.HashData(Payload.Span));
		using var stream = File.OpenRead(FilePath);
		return Convert.ToHexString(SHA256.HashData(stream));
	}

	internal void CopyTo(Stream destination)
	{
		if (FilePath is null) destination.Write(Payload.Span);
		else { using var stream = File.OpenRead(FilePath); stream.CopyTo(destination); }
	}
}

public static class WolfPackFile
{
	private static ReadOnlySpan<byte> Magic => "WOLFPACK"u8;
	public const int CurrentVersion = 1;

	public static IReadOnlyList<WolfPackEntry> Write(string path, IEnumerable<WolfPackSource> sources)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		ArgumentNullException.ThrowIfNull(sources);
		var ordered = sources.OrderBy(source => source.Id).ThenBy(source => source.Kind, StringComparer.Ordinal).ToArray();
		if (ordered.GroupBy(source => source.Id).Any(group => group.Count() != 1))
			throw new InvalidOperationException("A wolfpack cannot contain duplicate stable IDs.");

		var entries = ordered.Select(source => new WolfPackEntry
		{
			Id = source.Id,
			Kind = source.Kind,
			Length = source.Length,
			Sha256 = source.ComputeSha256(),
			Dependencies = source.Dependencies.Where(id => id != Guid.Empty).Distinct().Order().ToList()
		}).ToList();

		byte[] table;
		while (true)
		{
			table = JsonSerializer.SerializeToUtf8Bytes(entries, AssetJson.SerializerOptions);
			long offset = Magic.Length + sizeof(int) * 2 + sizeof(long) + table.Length;
			var changed = false;
			foreach (var entry in entries)
			{
				if (entry.Offset != offset)
				{
					entry.Offset = offset;
					changed = true;
				}

				offset += entry.Length;
			}

			if (!changed)
				break;
		}

		table = JsonSerializer.SerializeToUtf8Bytes(entries, AssetJson.SerializerOptions);
		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
		using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
		using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
		writer.Write(Magic);
		writer.Write(CurrentVersion);
		writer.Write(entries.Count);
		writer.Write((long)table.Length);
		writer.Write(table);
		foreach (var source in ordered)
			source.CopyTo(stream);

		return entries;
	}

	public static (WolfPackHeader Header, IReadOnlyList<WolfPackEntry> Entries) ReadTable(Stream stream)
	{
		ArgumentNullException.ThrowIfNull(stream);
		using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
		if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic))
			throw new InvalidDataException("Invalid wolfpack header.");
		var version = reader.ReadInt32();
		if (version != CurrentVersion)
			throw new InvalidDataException($"Unsupported wolfpack version {version}. Expected {CurrentVersion}.");
		var count = reader.ReadInt32();
		var tableLength = reader.ReadInt64();
		if (count < 0 || tableLength < 2 || tableLength > int.MaxValue)
			throw new InvalidDataException("Invalid wolfpack table dimensions.");
		var entries = JsonSerializer.Deserialize<List<WolfPackEntry>>(reader.ReadBytes((int)tableLength), AssetJson.SerializerOptions)
			?? throw new InvalidDataException("Wolfpack table is missing.");
		if (entries.Count != count || entries.Select(entry => entry.Id).Distinct().Count() != count)
			throw new InvalidDataException("Wolfpack table count or stable IDs are invalid.");
		return (new WolfPackHeader(version, count, tableLength), entries);
	}
}

public sealed class WolfPackCatalog : IDisposable
{
	private readonly Dictionary<Guid, (string Path, FileStream Stream, WolfPackEntry Entry)> _entries = [];
	private readonly List<FileStream> _streams = [];

	public WolfPackCatalog(string manifestPath)
	{
		try
		{
			var manifestBytes = File.ReadAllBytes(manifestPath);
			Manifest = JsonSerializer.Deserialize<WolfBootstrapManifest>(manifestBytes, AssetJson.SerializerOptions)
				?? throw new InvalidDataException("Bootstrap manifest is invalid.");
			if (Manifest.Version != WolfBootstrapManifest.CurrentVersion)
				throw new InvalidDataException($"Unsupported bootstrap manifest version {Manifest.Version}.");
			var root = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
			foreach (var pack in Manifest.Packs.OrderBy(pack => pack.Name, StringComparer.Ordinal))
			{
				var path = Path.Combine(root, pack.FileName);
				using var hashStream = File.OpenRead(path);
				var actualHash = Convert.ToHexString(SHA256.HashData(hashStream));
				if (!string.Equals(actualHash, pack.Sha256, StringComparison.Ordinal))
					throw new InvalidDataException($"Pack '{pack.FileName}' failed SHA-256 validation.");
				var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
				_streams.Add(stream);
				var (_, entries) = WolfPackFile.ReadTable(stream);
				foreach (var entry in entries)
					if (!_entries.TryAdd(entry.Id, (path, stream, entry)))
						throw new InvalidDataException($"Duplicate asset ID '{entry.Id}' across packs.");
			}
			foreach (var pair in _entries)
				foreach (var dependency in pair.Value.Entry.Dependencies)
					if (!_entries.ContainsKey(dependency))
						throw new InvalidDataException($"Entry '{pair.Key}' has missing dependency '{dependency}'.");
		}
		catch
		{
			// A throwing constructor never yields an instance, so the caller can never dispose the
			// pack handles we already opened. Release them here or they pin the files until finalization.
			Dispose();
			throw;
		}
	}

	public WolfBootstrapManifest Manifest { get; }
	public IReadOnlyCollection<Guid> AssetIds => _entries.Keys;
	public WolfPackEntry GetEntry(Guid id) => _entries.TryGetValue(id, out var value)
		? value.Entry : throw new KeyNotFoundException($"Cooked asset '{id}' was not found.");
	public byte[] Read(Guid id)
	{
		if (!_entries.TryGetValue(id, out var value))
			throw new KeyNotFoundException($"Cooked asset '{id}' was not found.");

		var bytes = new byte[checked((int)value.Entry.Length)];
		lock (value.Stream)
		{
			value.Stream.Position = value.Entry.Offset;
			value.Stream.ReadExactly(bytes);
		}
		if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), value.Entry.Sha256, StringComparison.Ordinal))
			throw new InvalidDataException($"Cooked asset '{id}' failed SHA-256 validation.");
		return bytes;
	}

	public Stream OpenRead(Guid id)
	{
		if (!_entries.TryGetValue(id, out var value))
			throw new KeyNotFoundException($"Cooked asset '{id}' was not found.");
		var stream = new FileStream(value.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
		return new BoundedReadStream(stream, value.Entry.Offset, value.Entry.Length);
	}

	public void Dispose()
	{
		foreach (var stream in _streams)
			stream.Dispose();
	}
}

internal sealed class BoundedReadStream : Stream
{
	private readonly Stream _inner;
	private readonly long _start;
	private readonly long _length;
	private long _position;

	public BoundedReadStream(Stream inner, long start, long length)
	{
		_inner = inner ?? throw new ArgumentNullException(nameof(inner));
		if (!inner.CanRead || !inner.CanSeek) throw new ArgumentException("The source stream must be readable and seekable.", nameof(inner));
		if (start < 0 || length < 0 || start > inner.Length - length) throw new ArgumentOutOfRangeException(nameof(start));
		_start = start;
		_length = length;
		inner.Position = start;
	}

	public override bool CanRead => true;
	public override bool CanSeek => true;
	public override bool CanWrite => false;
	public override long Length => _length;
	public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }
	public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
	public override int Read(Span<byte> buffer)
	{
		var count = (int)Math.Min(buffer.Length, _length - _position);
		if (count <= 0) return 0;
		var read = _inner.Read(buffer[..count]);
		_position += read;
		return read;
	}
	public override long Seek(long offset, SeekOrigin origin)
	{
		var next = origin switch
		{
			SeekOrigin.Begin => offset,
			SeekOrigin.Current => _position + offset,
			SeekOrigin.End => _length + offset,
			_ => throw new ArgumentOutOfRangeException(nameof(origin))
		};
		if (next < 0 || next > _length) throw new IOException("Seek is outside the cooked entry.");
		_inner.Position = _start + next;
		return _position = next;
	}
	public override void Flush() { }
	public override void SetLength(long value) => throw new NotSupportedException();
	public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
}

using System.Security.Cryptography;
using System.Text.Json;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Tests;

public sealed class WolfPackTests
{
	private string _root = null!;

	[SetUp]
	public void SetUp()
	{
		_root = Path.Combine(Path.GetTempPath(), "WolfPackTests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_root);
	}

	[TearDown]
	public void TearDown()
	{
		if (Directory.Exists(_root))
			Directory.Delete(_root, true);
	}

	[Test]
	public void BuildConfig_SceneIdsAreDistinctAndFirstSceneIsInitial()
	{
		var first = Guid.NewGuid();
		var second = Guid.NewGuid();
		var config = new WolfEngineBuildConfig();

		config.SetSceneIds([first, second, first, Guid.Empty]);

		Assert.Multiple(() =>
		{
			Assert.That(config.Version, Is.EqualTo(WolfEngineBuildConfig.CurrentVersion));
			Assert.That(config.SceneIds, Is.EqualTo(new[] { first, second }));
			Assert.That(config.InitialSceneId, Is.EqualTo(first));
			Assert.That(config.GetSceneIds(), Is.EqualTo(new[] { first, second }));
		});
	}

	[Test]
	public void BuildConfig_UsesLegacyInitialSceneWhenSceneListIsAbsent()
	{
		var initial = Guid.NewGuid();
		var config = new WolfEngineBuildConfig { Version = 1, InitialSceneId = initial };

		Assert.That(config.GetSceneIds(), Is.EqualTo(new[] { initial }));
	}

	[Test]
	public void Writer_IsByteDeterministicAndCanonical()
	{
		var a = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
		var b = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
		var sources = new[]
		{
			new WolfPackSource(b, "B", new byte[] { 2 }, new[] { a }),
			new WolfPackSource(a, "A", new byte[] { 1 }, Array.Empty<Guid>())
		};
		var first = Path.Combine(_root, "first.wolfpack");
		var second = Path.Combine(_root, "second.wolfpack");
		WolfPackFile.Write(first, sources);
		WolfPackFile.Write(second, sources.Reverse());
		Assert.That(File.ReadAllBytes(second), Is.EqualTo(File.ReadAllBytes(first)));
		using var stream = File.OpenRead(first);
		var (_, entries) = WolfPackFile.ReadTable(stream);
		Assert.That(entries.Select(entry => entry.Id), Is.EqualTo(new[] { a, b }));
	}

	[Test]
	public void Writer_RejectsDuplicateStableIds()
	{
		var id = Guid.NewGuid();
		Assert.Throws<InvalidOperationException>(() => WolfPackFile.Write(Path.Combine(_root, "bad.wolfpack"),
			[new WolfPackSource(id, "A", Array.Empty<byte>(), []), new WolfPackSource(id, "B", Array.Empty<byte>(), [])]));
	}

	[Test]
	public void Reader_RejectsBadHeaderAndVersion()
	{
		using var badHeader = new MemoryStream("NOTPACK!"u8.ToArray());
		Assert.Throws<InvalidDataException>(() => WolfPackFile.ReadTable(badHeader));

		var path = Path.Combine(_root, "version.wolfpack");
		WolfPackFile.Write(path, []);
		var bytes = File.ReadAllBytes(path);
		BitConverter.GetBytes(99).CopyTo(bytes, 8);
		using var badVersion = new MemoryStream(bytes);
		Assert.Throws<InvalidDataException>(() => WolfPackFile.ReadTable(badVersion));
	}

	[Test]
	public void Catalog_RejectsMissingDependencyAndTampering()
	{
		var id = Guid.NewGuid();
		var missing = Guid.NewGuid();
		var pack = Path.Combine(_root, "content.wolfpack");
		WolfPackFile.Write(pack, [new WolfPackSource(id, "Data", new byte[] { 1, 2, 3 }, new[] { missing })]);
		var packBytes = File.ReadAllBytes(pack);
		var manifest = new WolfBootstrapManifest
		{
			Target = "test",
			Packs =
			[
				new WolfManifestPack
				{
					Name = "content",
					FileName = "content.wolfpack",
					ByteSize = packBytes.Length,
					Sha256 = Convert.ToHexString(SHA256.HashData(packBytes))
				}
			]
		};
		var manifestPath = Path.Combine(_root, "bootstrap.wolfmanifest");
		File.WriteAllBytes(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, AssetJson.SerializerOptions));
		Assert.Throws<InvalidDataException>(() => new WolfPackCatalog(manifestPath));

		packBytes[^1] ^= 0xff;
		File.WriteAllBytes(pack, packBytes);
		Assert.Throws<InvalidDataException>(() => new WolfPackCatalog(manifestPath));
	}

	[Test]
	public void Catalog_OpenReadIsBoundedSeekableAndSupportsFileBackedSources()
	{
		var id = Guid.NewGuid();
		var payloadPath = Path.Combine(_root, "large.bin");
		var payload = Enumerable.Range(0, 8192).Select(index => (byte)(index % 251)).ToArray();
		File.WriteAllBytes(payloadPath, payload);
		var packPath = Path.Combine(_root, "streaming.wolfpack");
		WolfPackFile.Write(packPath, [WolfPackSource.FromFile(id, "AudioClip", payloadPath, [])]);
		var packBytes = File.ReadAllBytes(packPath);
		var manifest = new WolfBootstrapManifest
		{
			Target = "test",
			Packs = [new WolfManifestPack
			{
				Name = "audio", FileName = "streaming.wolfpack", ByteSize = packBytes.Length,
				Sha256 = Convert.ToHexString(SHA256.HashData(packBytes))
			}]
		};
		var manifestPath = Path.Combine(_root, "streaming.wolfmanifest");
		File.WriteAllBytes(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, AssetJson.SerializerOptions));
		using var catalog = new WolfPackCatalog(manifestPath);
		using var first = catalog.OpenRead(id);
		using var second = catalog.OpenRead(id);
		Assert.That(first.Length, Is.EqualTo(payload.Length));
		first.Seek(4000, SeekOrigin.Begin);
		Assert.That(first.ReadByte(), Is.EqualTo(payload[4000]));
		Assert.That(second.ReadByte(), Is.EqualTo(payload[0]));
		Assert.Throws<IOException>(() => first.Seek(1, SeekOrigin.End));
	}
}

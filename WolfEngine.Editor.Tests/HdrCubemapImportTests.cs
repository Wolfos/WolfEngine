using System.Numerics;
using Microsoft.Data.Sqlite;
using NSubstitute;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;
using WolfEngine.Importing;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class HdrCubemapImportTests
{
	private string _root = string.Empty;

	[SetUp]
	public void SetUp()
	{
		_root = Path.Combine(Path.GetTempPath(), "WolfEngineHdrCubemapTests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_root);
	}

	[TearDown]
	public void TearDown()
	{
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}
	}

	[Test]
	public void StbImageLoader_DecodesRadianceHdrAsLinearHalfFloats()
	{
		var path = Path.Combine(_root, "sky.hdr");
		WriteRadianceHdr(path, 8, 4, (_, _) => new Vector3(4.0f, 0.5f, 100.0f));

		var texture = new StbImageLoader().Load(path, TextureSemantic.BaseColor);

		Assert.That(texture.Format, Is.EqualTo(TextureFormat.Rgba16Float));
		Assert.That(texture.IsHdr, Is.True);
		Assert.That(texture.IsSrgb, Is.False, "HDR content is linear whatever the semantic.");
		Assert.That(texture.MipLevels[0].Data.Length, Is.EqualTo(8 * 4 * HdrPixels.BytesPerPixel));
		var pixels = HdrPixels.UnpackHalfRgba(texture.MipLevels[0].Data);
		Assert.That(pixels[0], Is.EqualTo(4.0f).Within(0.05f));
		Assert.That(pixels[1], Is.EqualTo(0.5f).Within(0.01f));
		Assert.That(pixels[2], Is.EqualTo(100.0f).Within(1.0f));
		Assert.That(pixels[3], Is.EqualTo(1.0f));
	}

	[Test]
	public void HdrPixels_PackClampsNegativeAndNonFiniteValues()
	{
		var packed = HdrPixels.PackHalfRgba([-1.0f, float.NaN, float.PositiveInfinity, 2.5f]);

		var unpacked = HdrPixels.UnpackHalfRgba(packed);

		Assert.That(unpacked, Is.EqualTo(new[] { 0.0f, 0.0f, HdrPixels.MaxValue, 2.5f }));
	}

	[TestCase(0, 1.0f, 0.0f, 0.0f)]
	[TestCase(1, -1.0f, 0.0f, 0.0f)]
	[TestCase(2, 0.0f, 1.0f, 0.0f)]
	[TestCase(3, 0.0f, -1.0f, 0.0f)]
	[TestCase(4, 0.0f, 0.0f, 1.0f)]
	[TestCase(5, 0.0f, 0.0f, -1.0f)]
	public void CubemapConverter_FaceCentresFaceTheirAxis(int face, float x, float y, float z)
	{
		var direction = CubemapConverter.GetFaceDirection(face, 0.0f, 0.0f);

		Assert.That(Vector3.Distance(direction, new Vector3(x, y, z)), Is.LessThan(1e-5f));
	}

	[Test]
	public void CubemapConverter_FaceCornersFollowTheApiLayout()
	{
		// The top-left texel of +X looks up and towards +Z; of +Y, towards -Z and -X; of +Z, up and towards -X.
		var positiveX = CubemapConverter.GetFaceDirection(0, -1.0f, -1.0f);
		var positiveY = CubemapConverter.GetFaceDirection(2, -1.0f, -1.0f);
		var positiveZ = CubemapConverter.GetFaceDirection(4, -1.0f, -1.0f);

		Assert.That(Vector3.Distance(positiveX, Vector3.Normalize(new Vector3(1.0f, 1.0f, 1.0f))), Is.LessThan(1e-5f));
		Assert.That(Vector3.Distance(positiveY, Vector3.Normalize(new Vector3(-1.0f, 1.0f, -1.0f))), Is.LessThan(1e-5f));
		Assert.That(Vector3.Distance(positiveZ, Vector3.Normalize(new Vector3(-1.0f, 1.0f, 1.0f))), Is.LessThan(1e-5f));
	}

	[Test]
	public void CubemapConverter_EquirectangularFacesSampleTheSkyDirectionMapping()
	{
		// Red encodes the panorama's U and green its V, so each face centre must read back DirToEnvUV of its axis.
		const int width = 256;
		const int height = 128;
		var source = CreateHdrSource(width, height, (x, y) => new Vector3((x + 0.5f) / width, (y + 0.5f) / height, 3.0f));

		var cube = CubemapConverter.Convert(source, requestedFaceSize: 16, maxFaceSize: 0);

		Assert.That(cube.Dimension, Is.EqualTo(TextureDimension.Cube));
		Assert.That(cube.Width, Is.EqualTo(16));
		Assert.That(cube.MipLevels[0].Depth, Is.EqualTo(TextureDescriptor.CubeFaceCount));
		var faces = HdrPixels.UnpackHalfRgba(cube.MipLevels[0].Data);
		for (var face = 0; face < TextureDescriptor.CubeFaceCount; face++)
		{
			var expected = ExpectedFaceCentreUv(face, 16);
			var centre = ReadFaceCentre(faces, face, 16);
			Assert.That(centre.Z, Is.EqualTo(3.0f).Within(0.01f), $"face {face} keeps HDR values above one");
			Assert.That(centre.Y, Is.EqualTo(expected.Y).Within(0.01f), $"face {face} V");
			if (face is 0 or 4 or 5)
			{
				// -X sits on the panorama's seam and the poles have no defined U, so only these faces check U.
				Assert.That(centre.X, Is.EqualTo(expected.X).Within(0.01f), $"face {face} U");
			}
		}

		Assert.That(CubemapConverter.DirectionToEquirectangular(Vector3.UnitX).X, Is.EqualTo(0.5f).Within(1e-5f));
		Assert.That(CubemapConverter.DirectionToEquirectangular(Vector3.UnitZ).X, Is.EqualTo(0.25f).Within(1e-5f));
		Assert.That(CubemapConverter.DirectionToEquirectangular(Vector3.UnitY).Y, Is.EqualTo(0.0f).Within(1e-5f));
	}

	[Test]
	public void CubemapConverter_HorizontalStripKeepsFaceOrder()
	{
		const int faceSize = 8;
		var source = CreateHdrSource(faceSize * 6, faceSize, (x, _) => new Vector3(x / faceSize, 0.0f, 0.0f));

		var cube = CubemapConverter.Convert(source, requestedFaceSize: 0, maxFaceSize: 0);

		var faces = HdrPixels.UnpackHalfRgba(cube.MipLevels[0].Data);
		for (var face = 0; face < TextureDescriptor.CubeFaceCount; face++)
		{
			var faceStart = face * faceSize * faceSize * 4;
			for (var texel = 0; texel < faceSize * faceSize; texel++)
			{
				Assert.That(faces[faceStart + texel * 4], Is.EqualTo((float)face), $"face {face} texel {texel}");
			}
		}
	}

	[Test]
	public void CubemapConverter_RejectsSourcesWithoutACubeLayout()
	{
		var source = CreateHdrSource(30, 20, (_, _) => Vector3.One);

		Assert.Throws<InvalidOperationException>(() => CubemapConverter.Convert(source, 0, 0));
	}

	[TestCase(2048, 1024, 0, 0, 512)]
	[TestCase(3000, 1500, 0, 0, 512)]
	[TestCase(8192, 4096, 0, 1024, 1024)]
	[TestCase(2048, 1024, 256, 0, 256)]
	[TestCase(600, 100, 0, 0, 128)]
	[TestCase(2, 12, 0, 0, 4)]
	public void CubemapConverter_ResolvesPowerOfTwoFaceSizes(
		int width,
		int height,
		int requested,
		int max,
		int expected)
	{
		var layout = CubemapConverter.DetectLayout(width, height);

		Assert.That(layout, Is.Not.Null);
		Assert.That(CubemapConverter.ResolveFaceSize(layout!.Value, width, height, requested, max), Is.EqualTo(expected));
	}

	[Test]
	public void TextureMipGenerator_FiltersCubeFacesSeparatelyInHalfFloat()
	{
		const int size = 4;
		var faceValues = new float[size * size * 4 * TextureDescriptor.CubeFaceCount];
		for (var i = 0; i < faceValues.Length; i++)
		{
			var face = i / (size * size * 4);
			faceValues[i] = 10.0f * (face + 1);
		}

		var baseMip = new TextureMipData(size, size, HdrPixels.PackHalfRgba(faceValues), TextureDescriptor.CubeFaceCount);

		var mips = TextureMipGenerator.GenerateMipChain(baseMip, TextureFormat.Rgba16Float);

		Assert.That(mips.Select(mip => mip.Width), Is.EqualTo(new[] { 4, 2, 1 }));
		Assert.That(mips.All(mip => mip.Depth == TextureDescriptor.CubeFaceCount), Is.True);
		var smallest = HdrPixels.UnpackHalfRgba(mips[^1].Data);
		Assert.That(smallest.Length, Is.EqualTo(4 * TextureDescriptor.CubeFaceCount));
		for (var face = 0; face < TextureDescriptor.CubeFaceCount; face++)
		{
			Assert.That(smallest[face * 4], Is.EqualTo(10.0f * (face + 1)), $"face {face} does not blend with its neighbours");
		}
	}

	[TestCase(TextureSemantic.BaseColor, true, TextureFormat.Bc6hUfloat)]
	[TestCase(TextureSemantic.Normal, true, TextureFormat.Bc6hUfloat)]
	[TestCase(TextureSemantic.Unknown, false, TextureFormat.Unknown)]
	public void TextureCompressionCompiler_CompressesHdrSourcesToBc6h(TextureSemantic semantic, bool expectedResult, TextureFormat expectedFormat)
	{
		var texture = CreateHdrSource(4, 4, (_, _) => Vector3.One) with { Semantic = semantic };

		var result = TextureCompressionCompiler.TryGetBcRuntimeFormat(texture, out var format);

		Assert.That(result, Is.EqualTo(expectedResult));
		Assert.That(format, Is.EqualTo(expectedFormat));
	}

	[Test]
	public void ImportedTextureSerializer_RoundTripsHdrCube()
	{
		var path = Path.Combine(_root, "texture.bin");
		var cube = CubemapConverter.Convert(CreateHdrSource(32, 16, (x, y) => new Vector3(x, y, 2.0f)), 8, 0);

		ImportedTextureSerializer.Write(path, cube);
		var loaded = ImportedTextureSerializer.Read(path, "cube");

		Assert.That(loaded.Format, Is.EqualTo(TextureFormat.Rgba16Float));
		Assert.That(loaded.Dimension, Is.EqualTo(TextureDimension.Cube));
		Assert.That(loaded.Width, Is.EqualTo(8));
		Assert.That(loaded.MipLevels[0].Depth, Is.EqualTo(TextureDescriptor.CubeFaceCount));
		Assert.That(loaded.MipLevels[0].Data, Is.EqualTo(cube.MipLevels[0].Data));
	}

	[Test]
	public void ImportedTextureSerializer_TryReadMip_PreviewsHdrCubeFirstFaceAsSrgb()
	{
		var path = Path.Combine(_root, "texture.bin");
		var values = new float[4 * 4 * 4 * TextureDescriptor.CubeFaceCount];
		Array.Fill(values, 8.0f);
		values.AsSpan(0, 4 * 4 * 4).Fill(0.5f);
		ImportedTextureSerializer.Write(path, new ImportedTexture(
			"cube",
			4,
			4,
			false,
			TextureSemantic.BaseColor,
			[new TextureMipData(4, 4, HdrPixels.PackHalfRgba(values), TextureDescriptor.CubeFaceCount)],
			TextureFormat.Rgba16Float,
			TextureDimension.Cube));

		var read = ImportedTextureSerializer.TryReadMip(path, 64, out var preview);

		Assert.That(read, Is.True);
		Assert.That(preview.IsSrgb, Is.True);
		Assert.That(preview.Data.Length, Is.EqualTo(4 * 4 * 4), "a thumbnail is one face of 8-bit RGBA");
		Assert.That(preview.Data[0], Is.EqualTo(188), "linear 0.5 encodes to sRGB 188");
	}

	[Test]
	public void ImportedTextureSerializer_ReadsVersionOneRgba8Files()
	{
		var path = Path.Combine(_root, "legacy.bin");
		using (var writer = new BinaryWriter(File.Create(path)))
		{
			writer.Write("WEIT"u8);
			writer.Write(1);
			writer.Write(1);
			writer.Write(1);
			writer.Write((byte)1);
			writer.Write((int)TextureSemantic.BaseColor);
			writer.Write(1);
			writer.Write(1);
			writer.Write(1);
			writer.Write(4);
			writer.Write(new byte[] { 1, 2, 3, 4 });
		}

		var loaded = ImportedTextureSerializer.Read(path, "legacy");

		Assert.That(loaded.Format, Is.EqualTo(TextureFormat.Rgba8Unorm));
		Assert.That(loaded.Dimension, Is.EqualTo(TextureDimension.Texture2D));
		Assert.That(loaded.MipLevels[0].Data, Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
	}

	[Test]
	public void TextureArtifactSerializer_RoundTripsBc6hCube()
	{
		var path = Path.Combine(_root, "runtime.bin");
		var faceBytes = TextureFormatUtilities.GetMipDataSize(TextureFormat.Bc6hUfloat, 8, 8);
		var cube = Texture.CreateCube(
			"cube",
			8,
			false,
			TextureFormat.Bc6hUfloat,
			[
				new TextureMipData(8, 8, new byte[faceBytes * 6], 6),
				new TextureMipData(4, 4, new byte[16 * 6], 6)
			]);

		TextureArtifactSerializer.Write(path, cube, TextureSemantic.BaseColor, TextureCompressionFamily.Bc);
		var loaded = TextureArtifactSerializer.Read(path, "cube");

		Assert.That(loaded.Dimension, Is.EqualTo(TextureDimension.Cube));
		Assert.That(loaded.Depth, Is.EqualTo(TextureDescriptor.CubeFaceCount));
		Assert.That(loaded.Format, Is.EqualTo(TextureFormat.Bc6hUfloat));
		Assert.That(loaded.MipLevels.Select(mip => mip.Depth), Is.EqualTo(new[] { 6, 6 }));
		Assert.That(loaded.MipLevels[0].Data.Length, Is.EqualTo(faceBytes * 6));
	}

	[Test]
	public void ImportingEquirectangularHdrAsCube_CompressesFacesToBc6h()
	{
		var compression = Substitute.For<ITextureGpuCompressionService>();
		ImportedTexture? compressedSource = null;
		compression.CompileBcTexture(Arg.Any<ImportedTexture>()).Returns(call =>
		{
			var source = call.Arg<ImportedTexture>();
			compressedSource = source;
			var faceBytes = TextureFormatUtilities.GetMipDataSize(TextureFormat.Bc6hUfloat, source.Width, source.Height);
			return Texture.CreateCube(
				source.NameOrPath,
				source.Width,
				false,
				TextureFormat.Bc6hUfloat,
				[new TextureMipData(source.Width, source.Height, new byte[faceBytes * 6], 6)]);
		});
		var pipeline = CreatePipeline(compression);
		pipeline.InitializeProject(_root);
		var sourcePath = Path.Combine(AssetPipelinePaths.GetAssetsPath(_root), "Sky.hdr");
		WriteRadianceHdr(sourcePath, 64, 32, (x, _) => new Vector3(x / 8.0f, 1.0f, 1.0f));
		WriteTextureSettings(sourcePath, new TextureImportSettings { Shape = TextureShape.Cube });

		var database = pipeline.RebuildProject(_root);

		var asset = database.Assets.Single(entry => entry.Type == AssetType.Texture2D);
		Assert.That(asset.TryGetSummary<TextureAssetSummary>(out var summary), Is.True);
		Assert.That(summary!.Dimension, Is.EqualTo(TextureDimension.Cube));
		Assert.That(summary.IsHdr, Is.True);
		Assert.That(summary.Width, Is.EqualTo(16));
		Assert.That(compressedSource, Is.Not.Null);
		Assert.That(compressedSource!.Value.Dimension, Is.EqualTo(TextureDimension.Cube));
		Assert.That(compressedSource.Value.Format, Is.EqualTo(TextureFormat.Rgba16Float));
		var runtime = ReadRuntimeArtifact(asset);
		Assert.That(runtime.Dimension, Is.EqualTo(TextureDimension.Cube));
		Assert.That(runtime.Format, Is.EqualTo(TextureFormat.Bc6hUfloat));
	}

	[Test]
	public void ImportingHdrWithUnknownSemantic_KeepsUncompressedHalfFloatCube()
	{
		var compression = Substitute.For<ITextureGpuCompressionService>();
		var pipeline = CreatePipeline(compression);
		pipeline.InitializeProject(_root);
		var sourcePath = Path.Combine(AssetPipelinePaths.GetAssetsPath(_root), "Sky.hdr");
		WriteRadianceHdr(sourcePath, 64, 32, (_, _) => new Vector3(5.0f, 1.0f, 0.25f));
		WriteTextureSettings(sourcePath, new TextureImportSettings
		{
			Shape = TextureShape.Cube,
			TextureSemantic = TextureSemantic.Unknown
		});

		var database = pipeline.RebuildProject(_root);

		compression.DidNotReceiveWithAnyArgs().CompileBcTexture(default);
		var runtime = ReadRuntimeArtifact(database.Assets.Single(entry => entry.Type == AssetType.Texture2D));
		Assert.That(runtime.Dimension, Is.EqualTo(TextureDimension.Cube));
		Assert.That(runtime.Format, Is.EqualTo(TextureFormat.Rgba16Float));
		Assert.That(runtime.MipCount, Is.EqualTo(5), "16x16 faces mip down to 1x1");
		Assert.That(runtime.MipLevels.All(mip => mip.Depth == TextureDescriptor.CubeFaceCount), Is.True);
		var smallest = HdrPixels.UnpackHalfRgba(runtime.MipLevels[^1].Data);
		Assert.That(smallest[0], Is.EqualTo(5.0f).Within(0.05f));
	}

	private ProjectAssetPipelineService CreatePipeline(ITextureGpuCompressionService compression)
	{
		return new ProjectAssetPipelineService(
			new AssetPipelineIndex(),
			new AssetMetadataStore(),
			new StbImageLoader(),
			new DataAssetStore(),
			new MaterialAssetStore(),
			Substitute.For<IThreeDFileImporter>(),
			compression,
			new ProjectTypeCatalog(() => throw new InvalidOperationException("No gameplay types are needed.")));
	}

	private static void WriteTextureSettings(string sourcePath, TextureImportSettings settings)
	{
		var metadata = new AssetSourceMetaFile
		{
			SourceId = Guid.NewGuid(),
			ImporterId = AssetImporterIds.Texture
		};
		metadata.SetImportSettings(settings);
		new AssetMetadataStore().Save(sourcePath + ".meta", metadata);
	}

	private Texture ReadRuntimeArtifact(AssetDatabaseEntry asset)
	{
		var artifact = asset.Artifacts.Single(entry => entry.Kind == "RuntimeTexture");

		return TextureArtifactSerializer.Read(Path.Combine(_root, artifact.RelativePath), asset.Name);
	}

	private static ImportedTexture CreateHdrSource(int width, int height, Func<int, int, Vector3> color)
	{
		var values = new float[width * height * 4];
		for (var y = 0; y < height; y++)
		{
			for (var x = 0; x < width; x++)
			{
				var value = color(x, y);
				var offset = (y * width + x) * 4;
				values[offset + 0] = value.X;
				values[offset + 1] = value.Y;
				values[offset + 2] = value.Z;
				values[offset + 3] = 1.0f;
			}
		}

		return new ImportedTexture(
			"source",
			width,
			height,
			false,
			TextureSemantic.BaseColor,
			[new TextureMipData(width, height, HdrPixels.PackHalfRgba(values))],
			TextureFormat.Rgba16Float);
	}

	/// <summary>The panorama UV averaged over the same four texels <see cref="ReadFaceCentre"/> reads.</summary>
	private static Vector2 ExpectedFaceCentreUv(int face, int faceSize)
	{
		var sum = Vector2.Zero;
		for (var y = faceSize / 2 - 1; y <= faceSize / 2; y++)
		{
			for (var x = faceSize / 2 - 1; x <= faceSize / 2; x++)
			{
				var s = 2.0f * (x + 0.5f) / faceSize - 1.0f;
				var t = 2.0f * (y + 0.5f) / faceSize - 1.0f;
				sum += CubemapConverter.DirectionToEquirectangular(CubemapConverter.GetFaceDirection(face, s, t));
			}
		}

		return sum / 4.0f;
	}

	private static Vector3 ReadFaceCentre(float[] faces, int face, int faceSize)
	{
		// Average the four texels around the centre, which straddle the face's exact centre direction.
		var sum = Vector3.Zero;
		for (var y = faceSize / 2 - 1; y <= faceSize / 2; y++)
		{
			for (var x = faceSize / 2 - 1; x <= faceSize / 2; x++)
			{
				var offset = ((face * faceSize * faceSize) + y * faceSize + x) * 4;
				sum += new Vector3(faces[offset], faces[offset + 1], faces[offset + 2]);
			}
		}

		return sum / 4.0f;
	}

	/// <summary>Writes an uncompressed Radiance RGBE file, which stb_image reads one flat scanline at a time.</summary>
	private static void WriteRadianceHdr(string path, int width, int height, Func<int, int, Vector3> color)
	{
		using var stream = File.Create(path);
		using var writer = new BinaryWriter(stream);
		writer.Write(System.Text.Encoding.ASCII.GetBytes($"#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y {height} +X {width}\n"));
		for (var y = 0; y < height; y++)
		{
			for (var x = 0; x < width; x++)
			{
				var value = color(x, y);
				var max = MathF.Max(value.X, MathF.Max(value.Y, value.Z));
				if (max < 1e-32f)
				{
					writer.Write(0);
					continue;
				}

				var exponent = (int)MathF.Floor(MathF.Log2(max)) + 1;
				var scale = 256.0f / MathF.Pow(2.0f, exponent);
				writer.Write((byte)Math.Min(255, (int)(value.X * scale)));
				writer.Write((byte)Math.Min(255, (int)(value.Y * scale)));
				writer.Write((byte)Math.Min(255, (int)(value.Z * scale)));
				writer.Write((byte)(exponent + 128));
			}
		}
	}
}

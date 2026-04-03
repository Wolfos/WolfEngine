using AstcEncoder;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using WolfEngine.Importing;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.Projects;

internal static class TextureCompressionCompiler
{
	private const float EditorAstcQuality = 80.0f;

	public static Texture CompileD3D12(ImportedTexture importedTexture)
	{
		ArgumentNullException.ThrowIfNull(importedTexture.MipLevels);
		var rawMips = TextureMipGenerator.GenerateRgba32MipChain(importedTexture.MipLevels[0]);
		var format = importedTexture.Semantic == TextureSemantic.Normal
			? TextureFormat.Bc5Unorm
			: TextureFormat.Bc7Unorm;

		var encoder = new BcEncoder();
		encoder.Options.IsParallel = true;
		encoder.Options.TaskCount = Math.Max(1, Environment.ProcessorCount);
		encoder.OutputOptions.GenerateMipMaps = false;
		encoder.OutputOptions.Quality = CompressionQuality.Fast;
		encoder.OutputOptions.Format = format switch
		{
			TextureFormat.Bc5Unorm => CompressionFormat.Bc5,
			TextureFormat.Bc7Unorm => CompressionFormat.Bc7,
			_ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported BC runtime texture format.")
		};

		var compressedMips = new TextureMipData[rawMips.Length];
		for (var i = 0; i < rawMips.Length; i++)
		{
			var encoded = encoder.EncodeToRawBytes(rawMips[i].Data, rawMips[i].Width, rawMips[i].Height, PixelFormat.Rgba32);
			compressedMips[i] = new TextureMipData(rawMips[i].Width, rawMips[i].Height, encoded[0]);
		}

		return new Texture(importedTexture.NameOrPath, importedTexture.Width, importedTexture.Height, importedTexture.IsSrgb, format, compressedMips);
	}

	public static Texture CompileMetal(ImportedTexture importedTexture)
	{
		ArgumentNullException.ThrowIfNull(importedTexture.MipLevels);
		var rawMips = TextureMipGenerator.GenerateRgba32MipChain(importedTexture.MipLevels[0]);
		var compressedMips = new TextureMipData[rawMips.Length];
		for (var i = 0; i < rawMips.Length; i++)
		{
			compressedMips[i] = CompressAstc(rawMips[i], importedTexture.IsSrgb);
		}

		return new Texture(importedTexture.NameOrPath, importedTexture.Width, importedTexture.Height, importedTexture.IsSrgb, TextureFormat.Astc4x4Unorm, compressedMips);
	}

	private static TextureMipData CompressAstc(TextureMipData mip, bool isSrgb)
	{
		var flags = isSrgb ? AstcencFlags.UsePerceptual : 0;
		var profile = isSrgb ? AstcencProfile.AstcencPrfLdrSrgb : AstcencProfile.AstcencPrfLdr;
		var configResult = Astcenc.AstcencConfigInit(profile, 4, 4, 1, EditorAstcQuality, flags, out var config);
		EnsureSuccess(configResult, "initialize ASTC config");

		var threadCount = (uint)Math.Max(1, Environment.ProcessorCount);
		var allocResult = Astcenc.AstcencContextAlloc(ref config, threadCount, out var context);
		EnsureSuccess(allocResult, "allocate ASTC context");

		try
		{
			var output = new byte[TextureFormatUtilities.GetMipDataSize(TextureFormat.Astc4x4Unorm, mip.Width, mip.Height)];
			var image = new AstcencImage
			{
				dimX = (uint)mip.Width,
				dimY = (uint)mip.Height,
				dimZ = 1,
				dataType = AstcencType.AstcencTypeU8,
				data = mip.Data
			};
			var swizzle = new AstcencSwizzle
			{
				r = AstcencSwz.AstcencSwzR,
				g = AstcencSwz.AstcencSwzG,
				b = AstcencSwz.AstcencSwzB,
				a = AstcencSwz.AstcencSwzA
			};

			var compressResult = Astcenc.AstcencCompressImage(context, ref image, swizzle, output, 0);
			EnsureSuccess(compressResult, "compress ASTC image");
			return new TextureMipData(mip.Width, mip.Height, output);
		}
		finally
		{
			Astcenc.AstcencContextFree(context);
		}
	}

	private static void EnsureSuccess(AstcencError error, string operation)
	{
		if (error == 0)
		{
			return;
		}

		throw new InvalidOperationException($"Failed to {operation}: {Astcenc.GetErrorString(error)}");
	}
}

using AstcEncoder;
using JeremyAnsel.BcnSharp;
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

		var compressedMips = new TextureMipData[rawMips.Length];
		for (var i = 0; i < rawMips.Length; i++)
		{
			var mip = rawMips[i];
			var encoded = format switch
			{
				TextureFormat.Bc5Unorm => Bc5Sharp.Encode(SwizzleRgbaToBgra(mip.Data), mip.Width, mip.Height),
				TextureFormat.Bc7Unorm => Bc7Sharp.Encode(SwizzleRgbaToBgra(mip.Data), mip.Width, mip.Height),
				_ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported BC runtime texture format.")
			};

			compressedMips[i] = new TextureMipData(mip.Width, mip.Height, encoded);
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

	private static byte[] SwizzleRgbaToBgra(byte[] rgba)
	{
		var bgra = new byte[rgba.Length];
		for (var i = 0; i < rgba.Length; i += 4)
		{
			bgra[i + 0] = rgba[i + 2];
			bgra[i + 1] = rgba[i + 1];
			bgra[i + 2] = rgba[i + 0];
			bgra[i + 3] = rgba[i + 3];
		}

		return bgra;
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

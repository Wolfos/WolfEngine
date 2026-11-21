using WolfEngine.Importing;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.Projects;

internal static class TextureCompressionCompiler
{
	/// <summary>
	/// The block-compressed format an imported texture compiles to, or false when it stays uncompressed.
	/// HDR sources compress to BC6H, since none of the 8-bit BC formats can hold values above one.
	/// </summary>
	public static bool TryGetBcRuntimeFormat(ImportedTexture texture, out TextureFormat format)
	{
		if (texture.IsHdr)
		{
			return TryGetHdrBcRuntimeFormat(texture.Semantic, out format);
		}

		return TryGetBcRuntimeFormat(texture.Semantic, out format);
	}

	public static bool TryGetBcRuntimeFormat(TextureSemantic semantic, out TextureFormat format)
	{
		format = semantic switch
		{
			// Unknown textures are often data maps (terrain height/control, masks, lookup textures).
			// Leave them uncompressed by default to avoid destroying scalar fidelity.
			TextureSemantic.Unknown => TextureFormat.Unknown,
			TextureSemantic.BaseColor => TextureFormat.Bc1Unorm,
			TextureSemantic.Normal => TextureFormat.Bc5Unorm,
			TextureSemantic.MetallicRoughness => TextureFormat.Bc3Unorm,
			TextureSemantic.Occlusion => TextureFormat.Bc4Unorm,
			TextureSemantic.Emissive => TextureFormat.Bc1Unorm,
			TextureSemantic.BaseColorTransparent => TextureFormat.Bc3Unorm,
			_ => TextureFormat.Unknown
		};

		return format != TextureFormat.Unknown;
	}

	/// <summary>
	/// HDR content keeps one format whatever the semantic: BC6H, which has no alpha, or half floats for
	/// <see cref="TextureSemantic.Unknown"/>, which marks data that must not lose precision.
	/// </summary>
	public static bool TryGetHdrBcRuntimeFormat(TextureSemantic semantic, out TextureFormat format)
	{
		format = semantic == TextureSemantic.Unknown
			? TextureFormat.Unknown
			: TextureFormat.Bc6hUfloat;

		return format != TextureFormat.Unknown;
	}
}

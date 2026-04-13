using WolfEngine.Importing;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.Projects;

internal static class TextureCompressionCompiler
{
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
			_ => TextureFormat.Unknown
		};

		return format != TextureFormat.Unknown;
	}
}

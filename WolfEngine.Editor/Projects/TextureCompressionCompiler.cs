using WolfEngine.Importing;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.Projects;

internal static class TextureCompressionCompiler
{
	public static bool TryGetBcRuntimeFormat(TextureSemantic semantic, out TextureFormat format)
	{
		format = semantic switch
		{
			TextureSemantic.Unknown => TextureFormat.Bc1Unorm,
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

using System.Collections.Concurrent;
using WolfEngine.Importing;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine;

public interface ITextureFactory
{
	Texture GetTexture(ImportedTexture importedTexture);
	Texture GetWhiteTexture();
}

public sealed class TextureFactory : ITextureFactory
{
	private readonly RenderGraph _renderGraph;
	private readonly ConcurrentDictionary<string, Texture> _cache = new(StringComparer.OrdinalIgnoreCase);
	private Texture? _whiteTexture;

	public TextureFactory(RenderGraph renderGraph)
	{
		_renderGraph = renderGraph ?? throw new ArgumentNullException(nameof(renderGraph));
	}

	public Texture GetTexture(ImportedTexture importedTexture)
	{
		if (importedTexture.PixelData is null)
		{
			throw new ArgumentException("Imported texture must contain pixel data.", nameof(importedTexture));
		}

		return _cache.GetOrAdd(importedTexture.NameOrPath, _ =>
		{
			var texture = new Texture(
				importedTexture.NameOrPath,
				importedTexture.Width,
				importedTexture.Height,
				importedTexture.IsSrgb,
				importedTexture.PixelData);

			texture.Resources = _renderGraph.EnsureTextureResources(texture);
			return texture;
		});
	}

	public Texture GetWhiteTexture()
	{
		if (_whiteTexture is not null)
		{
			return _whiteTexture;
		}

		var pixels = new byte[] { 255, 255, 255, 255 };
		var texture = new Texture("white_fallback", 1, 1, true, pixels);
		texture.Resources = _renderGraph.EnsureTextureResources(texture);
		_whiteTexture = texture;
		return texture;
	}
}

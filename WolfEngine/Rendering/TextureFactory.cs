using System.Collections.Concurrent;
using WolfEngine.Importing;
using WolfEngine.Rendering;

namespace WolfEngine;

public interface ITextureFactory
{
	Texture GetTexture(ImportedTexture importedTexture);
	Texture GetWhiteTexture();
	Texture GetBlackTexture();
	Texture GetNeutralNormalTexture();
	Texture LoadFromFile(string path, bool isSrgb = false);
}

public sealed class TextureFactory : ITextureFactory
{
	private readonly RenderGraph _renderGraph;
	private readonly IImageLoader _imageLoader;
	private readonly ConcurrentDictionary<string, Texture> _cache = new(StringComparer.OrdinalIgnoreCase);
	private Texture _whiteTexture;
	private Texture _blackTexture;
	private Texture _neutralNormalTexture;

	public TextureFactory(RenderGraph renderGraph, IImageLoader imageLoader)
	{
		_renderGraph = renderGraph ?? throw new ArgumentNullException(nameof(renderGraph));
		_imageLoader = imageLoader ?? throw new ArgumentNullException(nameof(imageLoader));
	}

	public Texture GetTexture(ImportedTexture importedTexture)
	{
		if (importedTexture.PixelData is null)
		{
			throw new ArgumentException("Imported texture must contain pixel data.", nameof(importedTexture));
		}

		var texture = _cache.GetOrAdd(importedTexture.NameOrPath, _ =>
		{
			return new Texture(
				importedTexture.NameOrPath,
				importedTexture.Width,
				importedTexture.Height,
				importedTexture.IsSrgb,
				importedTexture.PixelData);
		});
		_renderGraph.EnsureTextureResources(texture);
		return texture;
	}

	public Texture GetWhiteTexture()
	{
		if (_whiteTexture is not null)
		{
			_renderGraph.EnsureTextureResources(_whiteTexture);
			return _whiteTexture;
		}

		var pixels = new byte[] { 255, 255, 255, 255 };
		var texture = new Texture("white_fallback", 1, 1, true, pixels);
		_renderGraph.EnsureTextureResources(texture);
		_whiteTexture = texture;
		return texture;
	}

	public Texture GetBlackTexture()
	{
		if (_blackTexture is not null)
		{
			_renderGraph.EnsureTextureResources(_blackTexture);
			return _blackTexture;
		}

		var pixels = new byte[] { 0, 0, 0, 255 };
		var texture = new Texture("black_fallback", 1, 1, true, pixels);
		_renderGraph.EnsureTextureResources(texture);
		_blackTexture = texture;
		return texture;
	}

	public Texture GetNeutralNormalTexture()
	{
		if (_neutralNormalTexture is not null)
		{
			_renderGraph.EnsureTextureResources(_neutralNormalTexture);
			return _neutralNormalTexture;
		}

		var pixels = new byte[] { 128, 128, 255, 255 };
		var texture = new Texture("neutral_normal_fallback", 1, 1, false, pixels);
		_renderGraph.EnsureTextureResources(texture);
		_neutralNormalTexture = texture;
		return texture;
	}

	public Texture LoadFromFile(string path, bool isSrgb = false)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			throw new ArgumentException("Path cannot be null or empty.", nameof(path));
		}

		var imported = _imageLoader.Load(path, isSrgb ? TextureSemantic.BaseColor : TextureSemantic.Unknown);
		return GetTexture(imported);
	}
}

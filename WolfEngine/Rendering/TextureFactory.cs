using System;
using System.Collections.Concurrent;
using WolfEngine.Importing;
using WolfEngine.Rendering;

namespace WolfEngine;

public interface ITextureFactory
{
	Texture GetTexture(ImportedTexture importedTexture);
	Texture GetTexture(Texture texture);
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
	private Texture? _whiteTexture;
	private Texture? _blackTexture;
	private Texture? _neutralNormalTexture;

	public TextureFactory(RenderGraph renderGraph, IImageLoader imageLoader)
	{
		_renderGraph = renderGraph ?? throw new ArgumentNullException(nameof(renderGraph));
		_imageLoader = imageLoader ?? throw new ArgumentNullException(nameof(imageLoader));
	}

	public Texture GetTexture(ImportedTexture importedTexture)
	{
		if (importedTexture.MipLevels is null || importedTexture.MipLevels.Length == 0)
		{
			throw new ArgumentException("Imported texture must contain mip data.", nameof(importedTexture));
		}

		return GetTexture(new Texture(
			importedTexture.NameOrPath,
			importedTexture.Width,
			importedTexture.Height,
			importedTexture.IsSrgb,
			TextureFormat.Rgba8Unorm,
			importedTexture.MipLevels));
	}

	public Texture GetTexture(Texture texture)
	{
		ArgumentNullException.ThrowIfNull(texture);

		var cached = _cache.GetOrAdd(texture.Name, _ => texture);
		if (ReferenceEquals(cached, texture) == false)
		{
			cached.ApplyTextureData(texture.Width, texture.Height, texture.IsSrgb, texture.Format, texture.MipLevels);
		}

		_renderGraph.EnsureTextureResources(cached);
		return cached;
	}

	public Texture GetWhiteTexture()
	{
		if (_whiteTexture is not null)
		{
			_renderGraph.EnsureTextureResources(_whiteTexture);
			return _whiteTexture;
		}

		var texture = new Texture("white_fallback", 1, 1, true, TextureFormat.Rgba8Unorm, [new TextureMipData(1, 1, [255, 255, 255, 255])]);
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

		var texture = new Texture("black_fallback", 1, 1, true, TextureFormat.Rgba8Unorm, [new TextureMipData(1, 1, [0, 0, 0, 255])]);
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

		var texture = new Texture("neutral_normal_fallback", 1, 1, false, TextureFormat.Rgba8Unorm, [new TextureMipData(1, 1, [128, 128, 255, 255])]);
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

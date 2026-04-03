using System;
using System.Collections.Concurrent;
using System.IO;
using WolfEngine.Importing;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Utility;
using ImportImageLoader = WolfEngine.Importing.IImageLoader;

namespace WolfEngine.Editor.UI;

public sealed class ImageLoader : IImageLoader
{
	private readonly ImportImageLoader _importImageLoader;
	private readonly IRenderer _renderer;
	private readonly IMainThreadDispatcher _mainThreadDispatcher;
	private readonly ConcurrentDictionary<string, CachedImage> _cache = new(StringComparer.OrdinalIgnoreCase);

	private sealed class CachedImage
	{
		public required Texture Texture { get; init; }
		public required ITextureResources TextureResources { get; init; }
		public required nint TextureId { get; init; }
	}

	public ImageLoader(
		ImportImageLoader importImageLoader,
		IRenderer renderer,
		IMainThreadDispatcher mainThreadDispatcher)
	{
		_importImageLoader = importImageLoader ?? throw new ArgumentNullException(nameof(importImageLoader));
		_renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
		_mainThreadDispatcher = mainThreadDispatcher ?? throw new ArgumentNullException(nameof(mainThreadDispatcher));
	}

	public bool TryGetImGuiTextureId(string path, out nint textureId, bool isSrgb = false)
	{
		textureId = 0;
		if (string.IsNullOrWhiteSpace(path))
		{
			return false;
		}

		var resolvedPath = ResolvePath(path);
		if (File.Exists(resolvedPath) == false)
		{
			return false;
		}

		var cacheKey = BuildCacheKey(resolvedPath, isSrgb);
		try
		{
			var image = _cache.GetOrAdd(cacheKey, _ => LoadImage(resolvedPath, isSrgb));
			textureId = image.TextureId;
			return textureId != 0;
		}
		catch
		{
			return false;
		}
	}

	public nint GetImGuiTextureId(string path, bool isSrgb = false)
	{
		if (TryGetImGuiTextureId(path, out var textureId, isSrgb))
		{
			return textureId;
		}

		throw new InvalidOperationException($"Failed to load image texture for ImGui: '{path}'.");
	}

	private CachedImage LoadImage(string path, bool isSrgb)
	{
		var semantic = isSrgb ? TextureSemantic.BaseColor : TextureSemantic.Unknown;
		var imported = _importImageLoader.Load(path, semantic);
		var texture = new Texture(
			Path.GetFileName(path),
			imported.Width,
			imported.Height,
			imported.IsSrgb,
			TextureFormat.Rgba8Unorm,
			imported.MipLevels);

		var resources = _mainThreadDispatcher.Invoke(() => _renderer.CreateTextureResources(texture));
		var textureId = resources.ShaderResourceView.IsValid
			? (nint)resources.ShaderResourceView.Value
			: 0;

		return new CachedImage
		{
			Texture = texture,
			TextureResources = resources,
			TextureId = textureId
		};
	}

	private static string ResolvePath(string path)
	{
		if (Path.IsPathRooted(path))
		{
			return path;
		}

		return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
	}

	private static string BuildCacheKey(string absolutePath, bool isSrgb)
	{
		return $"{(isSrgb ? "srgb" : "linear")}:{absolutePath}";
	}
}

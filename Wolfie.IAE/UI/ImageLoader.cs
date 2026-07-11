using System.Collections.Concurrent;
using WolfEngine;
using WolfEngine.Importing;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Utility;
using ImportImageLoader = WolfEngine.Importing.IImageLoader;

namespace Wolfie.IAE.UI;

public sealed class ImageLoader(
	ImportImageLoader importImageLoader,
	IRenderer renderer,
	IMainThreadDispatcher mainThreadDispatcher) : IImageLoader
{
	private readonly ConcurrentDictionary<string, CachedImage> _cache = new(StringComparer.OrdinalIgnoreCase);

	private sealed record CachedImage(Texture Texture, ITextureResources Resources, nint TextureId);

	public bool TryGetImGuiTextureId(string path, out nint textureId, bool isSrgb = false)
	{
		textureId = 0;
		if (string.IsNullOrWhiteSpace(path)) return false;
		var resolved = Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
		if (!File.Exists(resolved)) return false;
		try
		{
			var key = $"{(isSrgb ? "srgb" : "linear")}:{resolved}";
			var image = _cache.GetOrAdd(key, _ => Load(resolved, isSrgb));
			textureId = image.TextureId;
			return textureId != 0;
		}
		catch { return false; }
	}

	public nint GetImGuiTextureId(string path, bool isSrgb = false) =>
		TryGetImGuiTextureId(path, out var textureId, isSrgb)
			? textureId
			: throw new InvalidOperationException($"Failed to load image texture for ImGui: '{path}'.");

	private CachedImage Load(string path, bool isSrgb)
	{
		var imported = importImageLoader.Load(path, isSrgb ? TextureSemantic.BaseColor : TextureSemantic.Unknown);
		var texture = new Texture(Path.GetFileName(path), imported.Width, imported.Height, imported.IsSrgb,
			TextureFormat.Rgba8Unorm, imported.MipLevels);
		var resources = mainThreadDispatcher.Invoke(() => renderer.CreateTextureResources(texture));
		var textureId = resources.ShaderResourceView.IsValid ? (nint)resources.ShaderResourceView.Value : 0;
		return new CachedImage(texture, resources, textureId);
	}
}

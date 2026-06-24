using System;
using System.Collections.Concurrent;
using System.IO;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Utility;

namespace WolfEngine.Editor.UI;

public sealed class AssetThumbnailLoader : IAssetThumbnailLoader
{
	internal const int TextureThumbnailMaxDimension = 64;

	private readonly IEditorProjectService _projectService;
	private readonly IRenderer _renderer;
	private readonly IMainThreadDispatcher _mainThreadDispatcher;
	private readonly ConcurrentDictionary<string, CachedThumbnail> _cache = new(StringComparer.OrdinalIgnoreCase);

	private sealed class CachedThumbnail
	{
		public required Texture Texture { get; init; }
		public required ITextureResources TextureResources { get; init; }
		public required nint TextureId { get; init; }
	}

	public AssetThumbnailLoader(
		IEditorProjectService projectService,
		IRenderer renderer,
		IMainThreadDispatcher mainThreadDispatcher)
	{
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
		_renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
		_mainThreadDispatcher = mainThreadDispatcher ?? throw new ArgumentNullException(nameof(mainThreadDispatcher));
	}

	public bool TryGetTextureThumbnailId(AssetDatabaseEntry asset, out nint textureId)
	{
		textureId = 0;
		ArgumentNullException.ThrowIfNull(asset);
		if (asset.Type != AssetType.Texture2D ||
		    asset.TryGetSummary<TextureAssetSummary>(out var summary) == false ||
		    string.IsNullOrWhiteSpace(summary.RelativeImportedPath) ||
		    _projectService.HasOpenProject == false)
		{
			return false;
		}

		var absoluteImportedPath = _projectService.GetAbsolutePath(summary.RelativeImportedPath);
		if (File.Exists(absoluteImportedPath) == false)
		{
			return false;
		}

		var lastWriteTime = File.GetLastWriteTimeUtc(absoluteImportedPath).Ticks;
		var cacheKey = $"{TextureThumbnailMaxDimension}:{lastWriteTime}:{absoluteImportedPath}";
		try
		{
			var thumbnail = _cache.GetOrAdd(cacheKey, _ => LoadThumbnail(absoluteImportedPath, asset.Name));
			textureId = thumbnail.TextureId;
			return textureId != 0;
		}
		catch
		{
			textureId = 0;
			return false;
		}
	}

	private CachedThumbnail LoadThumbnail(string absoluteImportedPath, string assetName)
	{
		if (ImportedTextureSerializer.TryReadMip(
			    absoluteImportedPath,
			    TextureThumbnailMaxDimension,
			    out var preview) == false)
		{
			throw new InvalidOperationException($"Failed to read imported texture thumbnail: '{absoluteImportedPath}'.");
		}

		var texture = new Texture(
			$"thumbnail:{assetName}:{absoluteImportedPath}",
			preview.Width,
			preview.Height,
			preview.IsSrgb,
			TextureFormat.Rgba8Unorm,
			[new TextureMipData(preview.Width, preview.Height, preview.Data)]);
		var resources = _mainThreadDispatcher.Invoke(() => _renderer.CreateTextureResources(texture));
		var textureId = resources.ShaderResourceView.IsValid
			? (nint)resources.ShaderResourceView.Value
			: 0;

		return new CachedThumbnail
		{
			Texture = texture,
			TextureResources = resources,
			TextureId = textureId
		};
	}
}

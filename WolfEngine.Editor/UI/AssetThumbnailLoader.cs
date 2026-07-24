using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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
	private readonly ConcurrentDictionary<string, Lazy<Task<CachedThumbnail>>> _cache = new(StringComparer.OrdinalIgnoreCase);
	private readonly SemaphoreSlim _loadConcurrency = new(Math.Max(1, Math.Min(Environment.ProcessorCount, 4)));

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
		return GetTextureThumbnailState(asset, out textureId) == AssetThumbnailState.Ready;
	}

	public AssetThumbnailState GetTextureThumbnailState(AssetDatabaseEntry asset, out nint textureId)
	{
		textureId = 0;
		ArgumentNullException.ThrowIfNull(asset);
		if (asset.Type != AssetType.Texture2D ||
		    asset.TryGetSummary<TextureAssetSummary>(out var summary) == false ||
		    string.IsNullOrWhiteSpace(summary.RelativeImportedPath) ||
		    _projectService.HasOpenProject == false)
		{
			return AssetThumbnailState.Unavailable;
		}

		var absoluteImportedPath = _projectService.GetAbsolutePath(summary.RelativeImportedPath);
		if (File.Exists(absoluteImportedPath) == false)
		{
			return AssetThumbnailState.Unavailable;
		}

		var lastWriteTime = File.GetLastWriteTimeUtc(absoluteImportedPath).Ticks;
		var cacheKey = $"{TextureThumbnailMaxDimension}:{lastWriteTime}:{absoluteImportedPath}";
		var pendingThumbnail = _cache.GetOrAdd(
			cacheKey,
			_ => new Lazy<Task<CachedThumbnail>>(
				() => LoadThumbnailAsync(absoluteImportedPath, asset.Name),
				LazyThreadSafetyMode.ExecutionAndPublication)).Value;

		if (pendingThumbnail.IsCompletedSuccessfully)
		{
			var thumbnail = pendingThumbnail.Result;
			textureId = thumbnail.TextureId;
			return textureId != 0 ? AssetThumbnailState.Ready : AssetThumbnailState.Unavailable;
		}

		if (pendingThumbnail.IsFaulted || pendingThumbnail.IsCanceled)
		{
			_ = pendingThumbnail.Exception;
			return AssetThumbnailState.Unavailable;
		}

		return AssetThumbnailState.Loading;
	}

	private Task<CachedThumbnail> LoadThumbnailAsync(string absoluteImportedPath, string assetName)
	{
		return Task.Run(async () =>
		{
			await _loadConcurrency.WaitAsync().ConfigureAwait(false);
			try
			{
				return LoadThumbnail(absoluteImportedPath, assetName);
			}
			finally
			{
				_loadConcurrency.Release();
			}
		});
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

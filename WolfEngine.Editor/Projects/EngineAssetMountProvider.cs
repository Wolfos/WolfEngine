using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.Projects;

public interface IEngineAssetMountProvider
{
	IReadOnlyList<IAssetMount> GetMounts();
}

/// <summary>
/// Builds authoritative engine assets with the normal importer into a target-specific local cache,
/// then exposes the resulting database as a read-only mount.
/// </summary>
public sealed class EngineAssetMountProvider : IEngineAssetMountProvider
{
	private const string MountId = "engine";
	private const string MountDisplayName = "Engine";
	private readonly string _sourceRoot;
	private readonly string _cacheRoot;
	private readonly IProjectAssetPipelineService _assetPipelineService;
	private readonly IRuntimeArtifactTargetProvider _targetProvider;
	private readonly object _sync = new();
	private string? _loadedCacheKey;
	private IReadOnlyList<IAssetMount>? _loadedMounts;

	public EngineAssetMountProvider(
		string engineContentRoot,
		IProjectAssetPipelineService assetPipelineService,
		IRuntimeArtifactTargetProvider targetProvider,
		string? cacheRoot = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(engineContentRoot);
		_assetPipelineService = assetPipelineService ?? throw new ArgumentNullException(nameof(assetPipelineService));
		_targetProvider = targetProvider ?? throw new ArgumentNullException(nameof(targetProvider));
		_sourceRoot = Path.Combine(Path.GetFullPath(engineContentRoot), "BuiltInContent", "Assets");
		_cacheRoot = Path.GetFullPath(cacheRoot ?? GetDefaultCacheRoot());
	}

	public IReadOnlyList<IAssetMount> GetMounts()
	{
		lock (_sync)
		{
			if (!Directory.Exists(_sourceRoot))
			{
				throw new DirectoryNotFoundException(
					$"WolfEngine built-in asset sources are missing at '{_sourceRoot}'. Reinstall the engine or restore BuiltInContent/Assets.");
			}

			var cacheKey = ComputeCacheKey();
			if (_loadedMounts is not null && string.Equals(_loadedCacheKey, cacheKey, StringComparison.Ordinal))
				return _loadedMounts;

			var contentRoot = Path.Combine(_cacheRoot, cacheKey);
			try
			{
				if (!IsPrepared(contentRoot)) PrepareCache(contentRoot);
				_loadedMounts = [ReadOnlyAssetMountLoader.Load(contentRoot)];
				_loadedCacheKey = cacheKey;
				return _loadedMounts;
			}
			catch (Exception exception)
			{
				throw new InvalidOperationException(
					$"WolfEngine built-in content for target '{_targetProvider.CurrentTarget}' could not be prepared in '{contentRoot}'. " +
					$"Delete that generated cache directory and reopen the project. {exception.Message}", exception);
			}
		}
	}

	private string ComputeCacheKey()
	{
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		Append(hash, $"content-version:{ReadOnlyAssetMountLoader.CurrentContentVersion}\n");
		Append(hash, $"target:{_targetProvider.CurrentTarget}\n");
		Append(hash, $"importers:{_assetPipelineService.ImporterVersionFingerprint ?? string.Empty}\n");

		foreach (var sourcePath in Directory.EnumerateFiles(_sourceRoot, "*", SearchOption.AllDirectories)
			         .OrderBy(path => Path.GetRelativePath(_sourceRoot, path), StringComparer.Ordinal))
		{
			var relativePath = Path.GetRelativePath(_sourceRoot, sourcePath).Replace('\\', '/');
			Append(hash, relativePath);
			Append(hash, "\0");
			using var source = File.OpenRead(sourcePath);
			var buffer = new byte[64 * 1024];
			int count;
			while ((count = source.Read(buffer, 0, buffer.Length)) != 0) hash.AppendData(buffer, 0, count);
			Append(hash, "\0");
		}

		return $"v{ReadOnlyAssetMountLoader.CurrentContentVersion}-{_targetProvider.CurrentTarget}-{Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()[..20]}";
	}

	private void PrepareCache(string contentRoot)
	{
		Directory.CreateDirectory(_cacheRoot);
		var stagingRoot = Path.Combine(_cacheRoot, $".{Path.GetFileName(contentRoot)}.staging-{Guid.NewGuid():N}");
		try
		{
			CopyDirectory(_sourceRoot, Path.Combine(stagingRoot, "Assets"));
			_assetPipelineService.RebuildProject(stagingRoot);
			var manifest = new AssetMountManifest
			{
				Id = MountId,
				DisplayName = MountDisplayName
			};
			File.WriteAllText(
				Path.Combine(stagingRoot, ReadOnlyAssetMountLoader.ManifestFileName),
				JsonSerializer.Serialize(manifest, AssetJson.SerializerOptions));

			// Load before publishing so an incomplete database never becomes the active cache entry.
			ReadOnlyAssetMountLoader.Load(stagingRoot);
			if (Directory.Exists(contentRoot)) Directory.Delete(contentRoot, recursive: true);
			Directory.Move(stagingRoot, contentRoot);
		}
		finally
		{
			if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, recursive: true);
		}
	}

	private static bool IsPrepared(string contentRoot) =>
		File.Exists(Path.Combine(contentRoot, ReadOnlyAssetMountLoader.ManifestFileName)) &&
		File.Exists(Path.Combine(contentRoot, "Library", "AssetPipeline.sqlite"));

	private static void CopyDirectory(string sourceRoot, string destinationRoot)
	{
		foreach (var directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
		{
			Directory.CreateDirectory(Path.Combine(destinationRoot, Path.GetRelativePath(sourceRoot, directory)));
		}

		Directory.CreateDirectory(destinationRoot);
		foreach (var sourcePath in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
		{
			var destinationPath = Path.Combine(destinationRoot, Path.GetRelativePath(sourceRoot, sourcePath));
			Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
			File.Copy(sourcePath, destinationPath, overwrite: false);
		}
	}

	private static void Append(IncrementalHash hash, string value) => hash.AppendData(Encoding.UTF8.GetBytes(value));

	private static string GetDefaultCacheRoot()
	{
		var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		if (string.IsNullOrWhiteSpace(localData)) localData = Path.GetTempPath();
		return Path.Combine(localData, "WolfEngine", "AssetCache", "BuiltInContent");
	}
}

internal sealed class EmptyEngineAssetMountProvider : IEngineAssetMountProvider
{
	public static EmptyEngineAssetMountProvider Instance { get; } = new();
	public IReadOnlyList<IAssetMount> GetMounts() => [];
}

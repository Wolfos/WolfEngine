using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.Projects;

public sealed class EditorFontContentProvider(IEditorProjectService project) : IFontContentProvider
{
	private long _revision = -1;
	private readonly Dictionary<string, AssetDatabaseEntry> _sources = new(StringComparer.Ordinal);
	private readonly object _sync = new();
	public bool TryResolve(string sourcePath, out Guid assetId, out string revision)
	{
		var path = FontSourceReferences.Normalize(sourcePath);
		lock (_sync)
		{
			if (_revision != project.AssetDatabaseRevision)
			{
				_sources.Clear();
				foreach (var source in project.CurrentAssetDatabase.Assets.Where(a => a.Type == AssetType.Font)) _sources[source.RelativeSourcePath] = source;
				_revision = project.AssetDatabaseRevision;
			}
			_sources.TryGetValue(path, out var asset);
			assetId = asset?.Id ?? Guid.Empty;
			revision = asset?.Artifacts.FirstOrDefault(a => a.Kind == "RuntimeFont")?.ContentHash ?? "";
			return asset is not null && revision.Length != 0;
		}
	}
	public Stream Open(Guid assetId)
	{
		if (!project.TryGetAsset(assetId, out var asset) || asset.Type != AssetType.Font) throw new KeyNotFoundException($"Font {assetId} not found.");
		var artifact = asset.Artifacts.Single(a => a.Kind == "RuntimeFont");
		return File.OpenRead(project.GetAbsoluteAssetPath(assetId, artifact.RelativePath));
	}
}

using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.Projects;

/// <summary>Resolves texture source paths against the open project's asset database.</summary>
public sealed class EditorTextureSourceProvider(IEditorProjectService project) : ITextureSourceProvider
{
	private readonly Dictionary<string, Guid> _sources = new(StringComparer.Ordinal);
	private readonly object _sync = new();
	private long _revision = -1;

	public bool TryResolve(string sourcePath, out Guid nodeId)
	{
		nodeId = Guid.Empty;

		if (TextureSourceReferences.TryNormalize(sourcePath, out var path) == false)
		{
			return false;
		}

		lock (_sync)
		{
			if (_revision != project.AssetDatabaseRevision)
			{
				Rebuild();
			}

			return _sources.TryGetValue(path, out nodeId);
		}
	}

	private void Rebuild()
	{
		_sources.Clear();

		foreach (var asset in project.CurrentAssetDatabase.Assets)
		{
			if (asset.Type != AssetType.Texture2D)
			{
				continue;
			}

			// A model's embedded textures share its source path; only a standalone image is a texture source.
			if (string.Equals(asset.RelativeSourcePath, asset.RelativeAssetPath, StringComparison.Ordinal) == false)
			{
				continue;
			}

			_sources[asset.RelativeSourcePath] = asset.Id;
		}

		_revision = project.AssetDatabaseRevision;
	}
}

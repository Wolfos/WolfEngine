using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.Projects;

public interface ITerrainAssetCreator
{
	EditorAssetCreationResult CreateTerrainAsset(string targetRelativeFolderPath);
}

public sealed class TerrainAssetCreator : ITerrainAssetCreator
{
	private readonly IEditorProjectService _projectService;
	private readonly IAssetMetadataStore _metadataStore;
	private readonly IProjectAssetPipelineService _assetPipelineService;

	public TerrainAssetCreator(
		IEditorProjectService projectService,
		IAssetMetadataStore metadataStore,
		IProjectAssetPipelineService assetPipelineService)
	{
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
		_metadataStore = metadataStore ?? throw new ArgumentNullException(nameof(metadataStore));
		_assetPipelineService = assetPipelineService ?? throw new ArgumentNullException(nameof(assetPipelineService));
	}

	public EditorAssetCreationResult CreateTerrainAsset(string targetRelativeFolderPath)
	{
		if (_projectService.HasOpenProject == false)
		{
			return EditorAssetCreationResult.Failed("Open or create a project before creating terrain assets.");
		}

		var targetFolder = ProjectPathUtility.NormalizeAssetsFolderPath(targetRelativeFolderPath);
		var absoluteFolder = _projectService.GetAbsolutePath(targetFolder);
		Directory.CreateDirectory(absoluteFolder);
		var assetName = GetNextAssetName(absoluteFolder);
		var relativeAssetPath = $"{targetFolder}/{assetName}{TerrainAsset.FileExtension}";
		var absoluteAssetPath = _projectService.GetAbsolutePath(relativeAssetPath);

		try
		{
			TerrainAssetSerializer.Write(absoluteAssetPath, TerrainAsset.CreateDefault(assetName));
			var metadata = new AssetSourceMetaFile
			{
				SourceId = Guid.NewGuid(),
				ImporterId = AssetImporterIds.Terrain,
				ImporterVersion = 1,
				SubAssets =
				[
					new AssetSubAssetManifestEntry
					{
						Key = "main",
						NodeId = Guid.NewGuid(),
						Type = AssetType.Terrain,
						Name = assetName
					}
				]
			};
			_metadataStore.Save(_projectService.GetAbsolutePath(relativeAssetPath + ".meta"), metadata);
			_projectService.RefreshAssetSource(relativeAssetPath);

			return _assetPipelineService.TryGetPrimaryNodeIdForRelativeSourcePath(_projectService.ProjectRootPath!, relativeAssetPath, out var assetId)
				? EditorAssetCreationResult.Succeeded(assetId)
				: EditorAssetCreationResult.Failed("Terrain asset was created but could not be found in the asset database.");
		}
		catch (Exception ex)
		{
			return EditorAssetCreationResult.Failed($"Failed to create terrain asset: {ex.Message}");
		}
	}

	private static string GetNextAssetName(string absoluteFolder)
	{
		const string baseName = "New Terrain";
		var candidate = baseName;
		var index = 1;
		while (File.Exists(Path.Combine(absoluteFolder, candidate + TerrainAsset.FileExtension)))
		{
			index++;
			candidate = $"{baseName} {index}";
		}

		return candidate;
	}
}

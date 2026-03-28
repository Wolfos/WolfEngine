using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.Projects;

public interface IMaterialAssetCreator
{
	EditorAssetCreationResult CreateMaterial(string targetRelativeFolderPath);
}

public sealed class MaterialAssetCreator : IMaterialAssetCreator
{
	private readonly IEditorProjectService _projectService;
	private readonly IMaterialAssetStore _materialAssetStore;
	private readonly IAssetMetadataStore _metadataStore;
	private readonly IProjectAssetPipelineService _assetPipelineService;

	public MaterialAssetCreator(
		IEditorProjectService projectService,
		IMaterialAssetStore materialAssetStore,
		IAssetMetadataStore metadataStore,
		IProjectAssetPipelineService assetPipelineService)
	{
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
		_materialAssetStore = materialAssetStore ?? throw new ArgumentNullException(nameof(materialAssetStore));
		_metadataStore = metadataStore ?? throw new ArgumentNullException(nameof(metadataStore));
		_assetPipelineService = assetPipelineService ?? throw new ArgumentNullException(nameof(assetPipelineService));
	}

	public EditorAssetCreationResult CreateMaterial(string targetRelativeFolderPath)
	{
		if (_projectService.HasOpenProject == false)
		{
			return EditorAssetCreationResult.Failed("Open or create a project before creating materials.");
		}

		var targetFolderPath = NormalizeTargetFolderPath(targetRelativeFolderPath);
		var assetName = GetNextMaterialName(targetFolderPath);
		var relativeAssetPath = $"{targetFolderPath}/{assetName}{MaterialAsset.FileExtension}";
		var absoluteAssetPath = _projectService.GetAbsolutePath(relativeAssetPath);
		var absoluteMetaPath = absoluteAssetPath + ".meta";
		try
		{
			Directory.CreateDirectory(_projectService.GetAbsolutePath(targetFolderPath));
			_materialAssetStore.SaveAsset(absoluteAssetPath, _materialAssetStore.CreateDefault(MaterialAssetType.Opaque));
			_metadataStore.Save(absoluteMetaPath, new AssetSourceMetaFile
			{
				SourceId = Guid.NewGuid(),
				ImporterId = AssetImporterIds.Material,
				ImporterVersion = 1,
				SubAssets =
				[
					new AssetSubAssetManifestEntry
					{
						Key = "main",
						NodeId = Guid.NewGuid(),
						Type = AssetType.Material,
						Name = assetName
					}
				]
			});
			_projectService.RefreshAssetSource(relativeAssetPath);
			if (_assetPipelineService.TryGetPrimaryNodeIdForRelativeSourcePath(_projectService.ProjectRootPath!, relativeAssetPath, out var nodeId))
			{
				return EditorAssetCreationResult.Succeeded(nodeId);
			}

			return EditorAssetCreationResult.Failed("Material was created, but the pipeline did not produce a material node.");
		}
		catch (Exception ex)
		{
			return EditorAssetCreationResult.Failed($"Failed to create material: {ex.Message}");
		}
	}

	private string GetNextMaterialName(string targetFolderPath)
	{
		const string baseName = "New Material";
		var index = 0;
		while (true)
		{
			var candidateName = index == 0 ? baseName : $"{baseName} {index}";
			var relativeAssetPath = $"{targetFolderPath}/{candidateName}{MaterialAsset.FileExtension}";
			var absoluteAssetPath = _projectService.GetAbsolutePath(relativeAssetPath);
			if (File.Exists(absoluteAssetPath) == false)
			{
				return candidateName;
			}

			index++;
		}
	}

	private static string NormalizeTargetFolderPath(string targetRelativeFolderPath)
	{
		return ProjectPathUtility.NormalizeAssetsFolderPath(targetRelativeFolderPath);
	}
}

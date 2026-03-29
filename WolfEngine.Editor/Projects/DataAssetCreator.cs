using System;
using System.IO;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.Projects;

public interface IDataAssetCreator
{
	EditorAssetCreationResult CreateDataAsset(Type dataAssetType, string targetRelativeFolderPath);
}

public sealed class DataAssetCreator : IDataAssetCreator
{
	private readonly IEditorProjectService _projectService;
	private readonly IDataAssetStore _dataAssetStore;
	private readonly IAssetMetadataStore _metadataStore;
	private readonly IProjectAssetPipelineService _assetPipelineService;

	public DataAssetCreator(
		IEditorProjectService projectService,
		IDataAssetStore dataAssetStore,
		IAssetMetadataStore metadataStore,
		IProjectAssetPipelineService assetPipelineService)
	{
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
		_dataAssetStore = dataAssetStore ?? throw new ArgumentNullException(nameof(dataAssetStore));
		_metadataStore = metadataStore ?? throw new ArgumentNullException(nameof(metadataStore));
		_assetPipelineService = assetPipelineService ?? throw new ArgumentNullException(nameof(assetPipelineService));
	}

	public EditorAssetCreationResult CreateDataAsset(Type dataAssetType, string targetRelativeFolderPath)
	{
		if (_projectService.HasOpenProject == false)
		{
			return EditorAssetCreationResult.Failed("Open or create a project before creating data assets.");
		}

		ArgumentNullException.ThrowIfNull(dataAssetType);
		var targetFolderPath = NormalizeTargetFolderPath(targetRelativeFolderPath);
		var assetName = GetNextDataAssetName(dataAssetType.Name, targetFolderPath);
		var relativeAssetPath = $"{targetFolderPath}/{assetName}{DataAssetFile.FileExtension}";
		var absoluteAssetPath = _projectService.GetAbsolutePath(relativeAssetPath);
		var absoluteMetaPath = absoluteAssetPath + ".meta";

		try
		{
			Directory.CreateDirectory(_projectService.GetAbsolutePath(targetFolderPath));
			var asset = _dataAssetStore.CreateDefault(dataAssetType);
			_dataAssetStore.SaveAsset(absoluteAssetPath, dataAssetType, asset);
			_metadataStore.Save(absoluteMetaPath, new AssetSourceMetaFile
			{
				SourceId = Guid.NewGuid(),
				ImporterId = AssetImporterIds.DataAsset,
				ImporterVersion = 1,
				SubAssets =
				[
					new AssetSubAssetManifestEntry
					{
						Key = "main",
						NodeId = Guid.NewGuid(),
						Type = AssetType.DataAsset,
						Name = assetName
					}
				]
			});
			_projectService.RefreshAssetSource(relativeAssetPath);
			if (_assetPipelineService.TryGetPrimaryNodeIdForRelativeSourcePath(_projectService.ProjectRootPath!, relativeAssetPath, out var nodeId))
			{
				return EditorAssetCreationResult.Succeeded(nodeId);
			}

			return EditorAssetCreationResult.Failed("Data asset was created, but the pipeline did not produce a node.");
		}
		catch (Exception ex)
		{
			return EditorAssetCreationResult.Failed($"Failed to create data asset: {ex.Message}");
		}
	}

	private string GetNextDataAssetName(string typeName, string targetFolderPath)
	{
		var baseName = $"New {typeName}";
		var index = 0;
		while (true)
		{
			var candidateName = index == 0 ? baseName : $"{baseName} {index}";
			var relativeAssetPath = $"{targetFolderPath}/{candidateName}{DataAssetFile.FileExtension}";
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

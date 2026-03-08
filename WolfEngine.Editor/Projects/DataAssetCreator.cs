using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.Projects;

public interface IDataAssetCreator
{
	EditorAssetCreationResult CreateDataAsset(Type dataAssetType);
}

public sealed class DataAssetCreator : IDataAssetCreator
{
	private readonly IEditorProjectService _projectService;
	private readonly IDataAssetStore _dataAssetStore;

	public DataAssetCreator(IEditorProjectService projectService, IDataAssetStore dataAssetStore)
	{
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
		_dataAssetStore = dataAssetStore ?? throw new ArgumentNullException(nameof(dataAssetStore));
	}

	public EditorAssetCreationResult CreateDataAsset(Type dataAssetType)
	{
		if (_projectService.HasOpenProject == false)
		{
			return EditorAssetCreationResult.Failed("Open or create a project before creating data assets.");
		}

		ArgumentNullException.ThrowIfNull(dataAssetType);

		var assetId = Guid.NewGuid();
		var assetName = GetNextDataAssetName(dataAssetType.Name);
		var relativeAssetPath = $"Assets/{assetName}{DataAssetFile.FileExtension}";
		var relativeMetaPath = relativeAssetPath + ".meta.json";
		var absoluteAssetPath = _projectService.GetAbsolutePath(relativeAssetPath);
		var absoluteMetaPath = _projectService.GetAbsolutePath(relativeMetaPath);

		var asset = _dataAssetStore.CreateDefault(dataAssetType);
		var meta = _dataAssetStore.CreateMeta(assetId, dataAssetType);
		var updatedDatabase = _projectService.CloneCurrentAssetDatabase();
		updatedDatabase.Assets.Add(new AssetDatabaseEntry
		{
			Id = assetId,
			Type = AssetType.DataAsset,
			Name = assetName,
			RelativeAssetPath = relativeAssetPath,
			RelativeMetaPath = relativeMetaPath,
			DataAssetSummary = new DataAssetSummary
			{
				DataAssetType = meta.DataAssetType,
				DisplayName = dataAssetType.Name
			}
		});

		var createdFiles = new List<string>(2);
		try
		{
			_dataAssetStore.SaveAsset(absoluteAssetPath, dataAssetType, asset);
			createdFiles.Add(absoluteAssetPath);
			_dataAssetStore.SaveMeta(absoluteMetaPath, meta);
			createdFiles.Add(absoluteMetaPath);
			_projectService.SaveAssetDatabase(updatedDatabase);
			return EditorAssetCreationResult.Succeeded(assetId);
		}
		catch (Exception ex)
		{
			RollbackCreatedFiles(createdFiles);
			return EditorAssetCreationResult.Failed($"Failed to create data asset: {ex.Message}");
		}
	}

	private string GetNextDataAssetName(string typeName)
	{
		var baseName = $"New {typeName}";
		var index = 0;
		while (true)
		{
			var candidateName = index == 0 ? baseName : $"{baseName} {index}";
			var relativeAssetPath = $"Assets/{candidateName}{DataAssetFile.FileExtension}";
			var absoluteAssetPath = _projectService.GetAbsolutePath(relativeAssetPath);
			if (File.Exists(absoluteAssetPath) == false)
			{
				return candidateName;
			}

			index++;
		}
	}

	private static void RollbackCreatedFiles(IEnumerable<string> files)
	{
		foreach (var file in files.Reverse())
		{
			try
			{
				if (File.Exists(file))
				{
					File.Delete(file);
				}
			}
			catch
			{
			}
		}
	}
}

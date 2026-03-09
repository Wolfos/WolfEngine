using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.Projects;

public interface IMaterialAssetCreator
{
	EditorAssetCreationResult CreateMaterial();
}

public sealed class MaterialAssetCreator : IMaterialAssetCreator
{
	private readonly IEditorProjectService _projectService;
	private readonly IMaterialAssetStore _materialAssetStore;

	public MaterialAssetCreator(IEditorProjectService projectService, IMaterialAssetStore materialAssetStore)
	{
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
		_materialAssetStore = materialAssetStore ?? throw new ArgumentNullException(nameof(materialAssetStore));
	}

	public EditorAssetCreationResult CreateMaterial()
	{
		if (_projectService.HasOpenProject == false)
		{
			return EditorAssetCreationResult.Failed("Open or create a project before creating materials.");
		}

		var assetId = Guid.NewGuid();
		var assetName = GetNextMaterialName();
		var relativeAssetPath = $"Assets/{assetName}{MaterialAsset.FileExtension}";
		var relativeStatePath = _materialAssetStore.GetStateRelativePath(assetId);
		var absoluteAssetPath = _projectService.GetAbsolutePath(relativeAssetPath);
		var absoluteStatePath = _projectService.GetAbsolutePath(relativeStatePath);

		var materialAsset = _materialAssetStore.CreateDefault(MaterialAssetType.Opaque);
		var materialState = _materialAssetStore.CreateState(assetId, MaterialAssetType.Opaque);
		var updatedDatabase = _projectService.CloneCurrentAssetDatabase();
		updatedDatabase.Assets.Add(new AssetDatabaseEntry
		{
			Id = assetId,
			Type = AssetType.Material,
			Name = assetName,
			RelativeAssetPath = relativeAssetPath,
			RelativeStatePath = relativeStatePath,
			RelativeMetaPath = relativeStatePath,
			MaterialSummary = new MaterialAssetSummary
			{
				MaterialType = MaterialAssetType.Opaque
			}
		});

		var createdFiles = new List<string>(2);
		try
		{
			_materialAssetStore.SaveAsset(absoluteAssetPath, materialAsset);
			createdFiles.Add(absoluteAssetPath);
			_materialAssetStore.SaveState(absoluteStatePath, materialState);
			createdFiles.Add(absoluteStatePath);
			_projectService.SaveAssetDatabase(updatedDatabase);
			return EditorAssetCreationResult.Succeeded(assetId);
		}
		catch (Exception ex)
		{
			RollbackCreatedFiles(createdFiles);
			return EditorAssetCreationResult.Failed($"Failed to create material: {ex.Message}");
		}
	}

	private string GetNextMaterialName()
	{
		const string baseName = "New Material";
		var index = 0;
		while (true)
		{
			var candidateName = index == 0 ? baseName : $"{baseName} {index}";
			var relativeAssetPath = $"Assets/{candidateName}{MaterialAsset.FileExtension}";
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

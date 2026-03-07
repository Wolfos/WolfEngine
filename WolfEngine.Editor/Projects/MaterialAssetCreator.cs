using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.Projects;

public readonly record struct MaterialAssetCreationResult(bool Success, string? ErrorMessage, Guid? AssetId)
{
	public static MaterialAssetCreationResult Succeeded(Guid assetId) => new(true, null, assetId);
	public static MaterialAssetCreationResult Failed(string errorMessage) => new(false, errorMessage, null);
}

public interface IMaterialAssetCreator
{
	MaterialAssetCreationResult CreateMaterial();
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

	public MaterialAssetCreationResult CreateMaterial()
	{
		if (_projectService.HasOpenProject == false)
		{
			return MaterialAssetCreationResult.Failed("Open or create a project before creating materials.");
		}

		var assetId = Guid.NewGuid();
		var assetName = GetNextMaterialName();
		var relativeAssetPath = $"Assets/{assetName}{MaterialAssetFile.FileExtension}";
		var relativeMetaPath = relativeAssetPath + ".meta.json";
		var absoluteAssetPath = _projectService.GetAbsolutePath(relativeAssetPath);
		var absoluteMetaPath = _projectService.GetAbsolutePath(relativeMetaPath);

		var materialAsset = _materialAssetStore.CreateDefault(MaterialAssetType.Opaque);
		var materialMeta = _materialAssetStore.CreateMeta(assetId, MaterialAssetType.Opaque);
		var updatedDatabase = _projectService.CloneCurrentAssetDatabase();
		updatedDatabase.Assets.Add(new AssetDatabaseEntry
		{
			Id = assetId,
			Type = AssetType.Material,
			Name = assetName,
			RelativeAssetPath = relativeAssetPath,
			RelativeMetaPath = relativeMetaPath,
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
			_materialAssetStore.SaveMeta(absoluteMetaPath, materialMeta);
			createdFiles.Add(absoluteMetaPath);
			_projectService.SaveAssetDatabase(updatedDatabase);
			return MaterialAssetCreationResult.Succeeded(assetId);
		}
		catch (Exception ex)
		{
			RollbackCreatedFiles(createdFiles);
			return MaterialAssetCreationResult.Failed($"Failed to create material: {ex.Message}");
		}
	}

	private string GetNextMaterialName()
	{
		const string baseName = "New Material";
		var index = 0;
		while (true)
		{
			var candidateName = index == 0 ? baseName : $"{baseName} {index}";
			var relativeAssetPath = $"Assets/{candidateName}{MaterialAssetFile.FileExtension}";
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

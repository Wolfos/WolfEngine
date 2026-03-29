using System;
using WolfEngine.ECS;

namespace WolfEngine.Editor.Projects;

public interface IProjectSceneImporter
{
	void ImportScene(string absoluteSourcePath, World world);
}

public sealed class ProjectSceneImporter : IProjectSceneImporter
{
	private readonly IEditorProjectService _projectService;
	private readonly IProjectAssetPipelineService _assetPipelineService;

	public ProjectSceneImporter(IEditorProjectService projectService, IProjectAssetPipelineService assetPipelineService)
	{
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
		_assetPipelineService = assetPipelineService ?? throw new ArgumentNullException(nameof(assetPipelineService));
	}

	public void ImportScene(string absoluteSourcePath, World world)
	{
		if (_projectService.HasOpenProject == false)
		{
			throw new InvalidOperationException("Open or create a project before importing 3D models.");
		}

		var importResult = _assetPipelineService.ImportExternalSource(_projectService.ProjectRootPath!, absoluteSourcePath);
		_projectService.ReloadAssetDatabaseFromIndex();
		if (importResult.PrimaryNodeId is not { } modelNodeId)
		{
			throw new InvalidOperationException("The imported 3D source did not produce a 3D model node.");
		}

		_assetPipelineService.InstantiateImportedModel(_projectService.ProjectRootPath!, modelNodeId, world);
	}
}

using WolfEngine.Build;

namespace WolfEngine.Editor.Projects;

public interface IGameBuildService
{
	GameBuildResult Build(string outputPath, bool debug = false);
}

public sealed class GameBuildService : IGameBuildService
{
	private readonly IEditorProjectService _projectService;

	public GameBuildService(IEditorProjectService projectService)
	{
		_projectService = projectService;
	}

	public GameBuildResult Build(string outputPath, bool debug = false)
	{
		if (_projectService.HasOpenProject == false || string.IsNullOrWhiteSpace(_projectService.ProjectRootPath))
			throw new InvalidOperationException("Open a project before building a game.");
		if (string.IsNullOrWhiteSpace(_projectService.GameplayProjectPath))
			throw new InvalidOperationException("Gameplay project is not configured.");

		_projectService.ReloadAssetDatabase();
		return GameBuilder.Build(
			new GameBuildContext
			{
				ProjectRootPath = _projectService.ProjectRootPath,
				GameplayProjectPath = _projectService.GameplayProjectPath,
				AssetDatabase = _projectService.CloneCurrentAssetDatabase()
			},
			new GameBuildOptions(outputPath, debug));
	}
}

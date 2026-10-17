using WolfEngine.Build;

namespace WolfEngine.Editor.Projects;

public interface IGameBuildService
{
	/// <param name="target">The runtime identifier to build for, or null for this machine. See <see cref="GameBuildOptions.Target"/>.</param>
	GameBuildResult Build(string outputPath, bool debug = false, string? target = null);
}

public sealed class GameBuildService : IGameBuildService
{
	private readonly IEditorProjectService _projectService;

	public GameBuildService(IEditorProjectService projectService)
	{
		_projectService = projectService;
	}

	public GameBuildResult Build(string outputPath, bool debug = false, string? target = null)
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
				AssetDatabase = _projectService.CloneCurrentAssetDatabase(),
				AssetCatalog = _projectService.CurrentAssetCatalog
			},
			new GameBuildOptions(outputPath, debug, target));
	}
}

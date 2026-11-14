using System.Text.Json;
using ImGuiNET;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;

namespace WolfEngine.Editor.UI;

public sealed class ProjectSettingsWindow : EditorWindow
{
	private readonly IEditorProjectService _projectService;
	private readonly IEditorNotificationService _notificationService;
	private static readonly string[] BuildConfigurations = ["Release", "Debug"];
	private const int MinimumWindowSize = 64;
	private const int MaximumWindowSize = 16384;
	private const int MinimumFixedUpdateRate = 10;
	private const int MaximumFixedUpdateRate = 1000;
	private const int MaximumFixedStepsPerFrame = 32;

	private WolfEngineBuildConfig? _config;
	private string? _loadedProjectPath;

	public ProjectSettingsWindow(IEditorProjectService projectService, IEditorNotificationService notificationService)
	{
		_projectService = projectService;
		_notificationService = notificationService;
	}

	public override string Name => "Project Settings";

	public override bool CanOpen(out string? reason)
	{
		if (_projectService.HasOpenProject == false)
		{
			reason = "Open a project before editing project settings.";
			return false;
		}
		reason = null;
		return true;
	}

	public override void OnOpened() => Load();

	public override void Draw(EditorScene scene)
	{
		if (_projectService.HasOpenProject == false)
		{
			CloseCurrentWorkspaceWindow();
			return;
		}
		if (_config is null || !string.Equals(_loadedProjectPath, _projectService.ProjectRootPath, StringComparison.Ordinal))
			Load();
		if (_config is null)
			return;

		ImGui.SetNextWindowSize(new System.Numerics.Vector2(560, 640), ImGuiCond.FirstUseEver);
		Begin();
		ImGui.TextUnformatted("Project Scenes");
		DrawHint("The first scene is launched when the game starts. All listed scenes are included in builds.");
		ImGui.Separator();

		var scenes = GetScenes();
		for (var index = 0; index < _config.SceneIds.Count; index++)
		{
			var sceneId = _config.SceneIds[index];
			var projectScene = scenes.FirstOrDefault(asset => asset.Id == sceneId);
			var label = projectScene is null ? $"Missing scene ({sceneId:D})" : projectScene.Name;
			ImGui.TextUnformatted(index == 0 ? $"Start: {label}" : label);
			ImGui.SameLine();
			if (index > 0 && ImGui.SmallButton($"Make Start##{sceneId:D}"))
			{
				_config.SceneIds.RemoveAt(index);
				_config.SceneIds.Insert(0, sceneId);
			}
			ImGui.SameLine();
			if (ImGui.SmallButton($"Remove##{sceneId:D}"))
			{
				_config.SceneIds.RemoveAt(index);
				index--;
			}
		}

		ImGui.Spacing();
		if (ImGui.BeginCombo("Add Scene", "Select a scene..."))
		{
			foreach (var projectScene in scenes.Where(candidate => !_config.SceneIds.Contains(candidate.Id)))
			{
				if (ImGui.Selectable($"{projectScene.Name}##{projectScene.Id:D}"))
					_config.SceneIds.Add(projectScene.Id);
			}
			ImGui.EndCombo();
		}

		DrawBuildSettings(_config);
		DrawWindowSettings(_config.RuntimeSettings);
		DrawSimulationSettings(_config.RuntimeSettings);
		DrawStartupSettings(_config);

		ImGui.Spacing();
		if (ImGui.Button("Save"))
		{
			Save();
		}

		ImGui.SameLine();
		if (ImGui.Button("Reload"))
		{
			Load();
		}

		ImGui.End();
	}

	private void DrawBuildSettings(WolfEngineBuildConfig config)
	{
		BeginSection("Build");

		var executableName = config.ExecutableName;
		var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(_projectService.ProjectRootPath ?? string.Empty));
		if (ImGui.InputTextWithHint("Executable name", folderName, ref executableName, 128))
		{
			config.ExecutableName = executableName;
		}

		DrawHint("Leave empty to use the project folder's name.");

		var configurationIndex = Array.IndexOf(BuildConfigurations, config.Configuration);
		if (configurationIndex < 0)
		{
			configurationIndex = 0;
		}

		if (ImGui.Combo("Configuration", ref configurationIndex, BuildConfigurations, BuildConfigurations.Length))
		{
			config.Configuration = BuildConfigurations[configurationIndex];
		}

		var selfContained = config.SelfContained;
		if (ImGui.Checkbox("Self-contained", ref selfContained))
		{
			config.SelfContained = selfContained;
		}

		DrawHint("Bundles the .NET runtime, so players do not need it installed.");
	}

	private static void DrawWindowSettings(CookedRuntimeSettings settings)
	{
		BeginSection("Window");

		var title = settings.Title;
		if (ImGui.InputText("Title", ref title, 256))
		{
			settings.Title = title;
		}

		var width = settings.Width;
		if (ImGui.InputInt("Width", ref width))
		{
			settings.Width = Math.Clamp(width, MinimumWindowSize, MaximumWindowSize);
		}

		var height = settings.Height;
		if (ImGui.InputInt("Height", ref height))
		{
			settings.Height = Math.Clamp(height, MinimumWindowSize, MaximumWindowSize);
		}

		DrawHint("The window's size when the game starts.");
	}

	private static void DrawSimulationSettings(CookedRuntimeSettings settings)
	{
		BeginSection("Simulation");

		var fixedUpdateRate = (int)MathF.Round(1.0f / settings.FixedDeltaTime);
		if (ImGui.InputInt("Fixed update rate (Hz)", ref fixedUpdateRate))
		{
			settings.FixedDeltaTime = 1.0f / Math.Clamp(fixedUpdateRate, MinimumFixedUpdateRate, MaximumFixedUpdateRate);
		}

		var maxSteps = settings.MaxPhysicsStepsPerFrame;
		if (ImGui.InputInt("Max fixed steps per frame", ref maxSteps))
		{
			settings.MaxPhysicsStepsPerFrame = Math.Clamp(maxSteps, 1, MaximumFixedStepsPerFrame);
		}

		DrawHint("Physics steps at the fixed rate. A slow frame runs at most this many steps, then lets time slip.");
	}

	private static void DrawStartupSettings(WolfEngineBuildConfig config)
	{
		BeginSection("Startup");

		var showEngineSplashScreen = config.ShowEngineSplashScreen;
		if (ImGui.Checkbox("Show engine splash screen", ref showEngineSplashScreen))
		{
			config.ShowEngineSplashScreen = showEngineSplashScreen;
		}

		DrawHint("Shows \"Powered by WolfEngine\" while the first scene loads. Without it, the window stays blank until the scene is ready.");
	}

	private static void DrawHint(string text)
	{
		ImGui.PushTextWrapPos(0.0f);
		ImGui.TextDisabled(text);
		ImGui.PopTextWrapPos();
	}

	private static void BeginSection(string name)
	{
		ImGui.Spacing();
		ImGui.Spacing();
		ImGui.TextUnformatted(name);
		ImGui.Separator();
	}

	private IReadOnlyList<AssetDatabaseEntry> GetScenes() => _projectService.CurrentAssetDatabase.Assets
		.Where(asset => asset.Type == AssetType.Scene)
		.OrderBy(asset => asset.Name, StringComparer.OrdinalIgnoreCase)
		.ToArray();

	private void Load()
	{
		try
		{
			var projectPath = _projectService.ProjectRootPath ?? throw new InvalidOperationException("No project is open.");
			var configPath = Path.Combine(projectPath, "WolfEngineBuild.json");
			_config = File.Exists(configPath)
				? JsonSerializer.Deserialize<WolfEngineBuildConfig>(File.ReadAllBytes(configPath), AssetJson.SerializerOptions)
				: new WolfEngineBuildConfig();
			if (_config is null)
				throw new InvalidDataException("WolfEngineBuild.json is invalid.");
			_config.SetSceneIds(_config.GetSceneIds());
			_loadedProjectPath = projectPath;
		}
		catch (Exception exception)
		{
			_config = null;
			_notificationService.ReportError($"Could not load project settings: {exception.Message}");
		}
	}

	private void Save()
	{
		try
		{
			if (_config!.SceneIds.Count == 0)
				throw new InvalidOperationException("Add at least one project scene.");
			_config.SetSceneIds(_config.SceneIds);
			var configPath = Path.Combine(_projectService.ProjectRootPath!, "WolfEngineBuild.json");
			File.WriteAllBytes(configPath, JsonSerializer.SerializeToUtf8Bytes(_config, AssetJson.SerializerOptions));
		}
		catch (Exception exception)
		{
			_notificationService.ReportError($"Could not save project settings: {exception.Message}");
		}
	}
}

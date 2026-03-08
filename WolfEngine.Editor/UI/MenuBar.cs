using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using WolfEngine.Editor.Projects;
using WolfEngine.Utility;

namespace WolfEngine.Editor.UI;

public interface IMenuBar
{
	public void Draw(EditorScene scene);
}

public sealed class MenuBar : IMenuBar
{
	private const string NewProjectPopupId = "New Project";
	private const string ErrorPopupId = "Asset Pipeline Error";

	private readonly IFileDialogService _fileDialogService;
	private readonly ISceneBuilder _sceneBuilder;
	private readonly FramerateTool _framerateTool;
	private readonly IEditorProjectService _projectService;
	private readonly ITextureAssetImporter _textureAssetImporter;

	private string _newProjectName = string.Empty;
	private string _newProjectParentFolder = string.Empty;
	private bool _openNewProjectPopup;
	private bool _openErrorPopup;
	private string _errorMessage = string.Empty;

	public MenuBar(
		IFileDialogService fileDialogService,
		ISceneBuilder sceneBuilder,
		FramerateTool framerateTool,
		IEditorProjectService projectService,
		ITextureAssetImporter textureAssetImporter)
	{
		_fileDialogService = fileDialogService;
		_sceneBuilder = sceneBuilder;
		_framerateTool = framerateTool;
		_projectService = projectService;
		_textureAssetImporter = textureAssetImporter;
	}

	public void Draw(EditorScene scene)
	{
		if (ImGui.BeginMainMenuBar() == false)
		{
			DrawPopups();
			return;
		}

		if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
		{
			const float macTitlebarButtonInset = 70.0f;
			if (ImGui.GetCursorPosX() < macTitlebarButtonInset)
			{
				ImGui.SetCursorPosX(macTitlebarButtonInset);
			}
		}

		DrawFileMenu();
		DrawEditMenu();
		DrawImportMenu(scene);

		if (_projectService.HasOpenProject)
		{
			var projectLabel = Path.GetFileName(_projectService.ProjectRootPath);
			ImGui.SameLine();
			ImGui.TextDisabled($"Project: {projectLabel}");
		}

		_framerateTool.DrawRightAlignedInMenuBar();
		ImGui.EndMainMenuBar();

		DrawPopups();
	}

	private void DrawFileMenu()
	{
		if (ImGui.BeginMenu("File") == false)
		{
			return;
		}

		if (ImGui.MenuItem("New Project..."))
		{
			_newProjectName = string.Empty;
			_newProjectParentFolder = string.Empty;
			_openNewProjectPopup = true;
		}

		if (ImGui.MenuItem("Open Project..."))
		{
			var selectedFolder = _fileDialogService.OpenFolder(new FileDialogOptions
			{
				Title = "Open Project"
			});
			if (string.IsNullOrWhiteSpace(selectedFolder) == false)
			{
				if (_projectService.OpenProject(selectedFolder, out var errorMessage))
				{
					PersistLastOpenedProject();
				}
				else
				{
					ShowError(errorMessage);
				}
			}
		}

		if (ImGui.MenuItem("Preferences"))
		{
			EditorPreferencesMenu.Open();
		}

		ImGui.EndMenu();
	}

	private static void DrawEditMenu()
	{
		if (ImGui.BeginMenu("Edit"))
		{
			ImGui.EndMenu();
		}
	}

	private void DrawImportMenu(EditorScene scene)
	{
		if (ImGui.BeginMenu("Import") == false)
		{
			return;
		}

		if (ImGui.MenuItem("Import 3D file"))
		{
			var path = _fileDialogService.OpenFile(new FileDialogOptions
			{
				Title = "Import 3D file",
				AllowedExtensions = ["gltf", "glb", "fbx"]
			});
			if (string.IsNullOrEmpty(path) == false)
			{
				_sceneBuilder.Import3DScene(path, scene.World);
			}
		}

		var hasOpenProject = _projectService.HasOpenProject;
		if (hasOpenProject == false)
		{
			ImGui.BeginDisabled();
		}

		if (ImGui.MenuItem("Import Texture..."))
		{
			var result = _textureAssetImporter.ImportTexture();
			if (result.Success == false && result.Cancelled == false)
			{
				ShowError(result.ErrorMessage ?? "Texture import failed.");
			}
		}

		if (hasOpenProject == false)
		{
			ImGui.EndDisabled();
		}

		ImGui.EndMenu();
	}

	private void DrawPopups()
	{
		if (_openNewProjectPopup)
		{
			ImGui.OpenPopup(NewProjectPopupId);
			_openNewProjectPopup = false;
		}

		if (_openErrorPopup)
		{
			ImGui.OpenPopup(ErrorPopupId);
			_openErrorPopup = false;
		}

		DrawNewProjectPopup();
		DrawErrorPopup();
	}

	private void DrawNewProjectPopup()
	{
		var isOpen = true;
		ImGui.SetNextWindowSizeConstraints(new Vector2(520.0f, 0.0f), new Vector2(520.0f, float.MaxValue));
		if (ImGui.BeginPopupModal(NewProjectPopupId, ref isOpen, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoResize) == false)
		{
			return;
		}

		ImGui.TextUnformatted("Project Name");
		ImGui.SetNextItemWidth(-1.0f);
		ImGui.InputText("##ProjectName", ref _newProjectName, 256);

		ImGui.Spacing();
		ImGui.TextUnformatted("Parent Folder");
		ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 90.0f);
		ImGui.InputText("##ParentFolder", ref _newProjectParentFolder, 1024, ImGuiInputTextFlags.ReadOnly);
		ImGui.SameLine();
		if (ImGui.Button("Browse"))
		{
			var selectedFolder = _fileDialogService.OpenFolder(new FileDialogOptions
			{
				Title = "Choose Project Parent Folder"
			});
			if (string.IsNullOrWhiteSpace(selectedFolder) == false)
			{
				_newProjectParentFolder = selectedFolder;
			}
		}

		ImGui.Spacing();
		if (ImGui.Button("Create", new Vector2(100.0f, 0.0f)))
		{
			if (_projectService.CreateProject(_newProjectParentFolder, _newProjectName, out var errorMessage))
			{
				PersistLastOpenedProject();
				ImGui.CloseCurrentPopup();
			}
			else
			{
				ShowError(errorMessage);
			}
		}

		ImGui.SameLine();
		if (ImGui.Button("Cancel", new Vector2(100.0f, 0.0f)))
		{
			ImGui.CloseCurrentPopup();
		}

		ImGui.EndPopup();
	}

	private void DrawErrorPopup()
	{
		var isOpen = true;
		ImGui.SetNextWindowSize(new Vector2(480.0f, 0.0f), ImGuiCond.Appearing);
		if (ImGui.BeginPopupModal(ErrorPopupId, ref isOpen, ImGuiWindowFlags.AlwaysAutoResize) == false)
		{
			return;
		}

		ImGui.TextWrapped(_errorMessage);
		ImGui.Spacing();
		if (ImGui.Button("OK", new Vector2(100.0f, 0.0f)))
		{
			_errorMessage = string.Empty;
			ImGui.CloseCurrentPopup();
		}

		ImGui.EndPopup();
	}

	private void ShowError(string errorMessage)
	{
		_errorMessage = errorMessage;
		_openErrorPopup = true;
	}

	private void PersistLastOpenedProject()
	{
		EditorPreferences.SetLastProjectPath(_projectService.ProjectRootPath);
		EditorPreferences.Save();
	}
}

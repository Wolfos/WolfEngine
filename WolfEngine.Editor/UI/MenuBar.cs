using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using WolfEngine.Editor.Projects;
using WolfEngine.Platform;
using WolfEngine.Rendering.UI;
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
	private const float MacTitlebarButtonInset = 70.0f;
	private const float WindowsCaptionButtonWidth = 45.0f;
	private const float WindowsCaptionButtonSpacing = 0.0f;
	private const float WindowsMaximizedTopInset = 8.0f;
	private const float WindowsMaximizedContentInset = 4.0f;

	private readonly IFileDialogService _fileDialogService;
	private readonly IProjectSceneImporter _sceneImporter;
	private readonly FramerateTool _framerateTool;
	private readonly IEditorProjectService _projectService;
	private readonly ITextureAssetImporter _textureAssetImporter;
	private readonly IIconManager _icons;
	private readonly IWindowChromeController _windowChromeController;
	private readonly IEditorModeState _editorModeState;

	private string _newProjectName = string.Empty;
	private string _newProjectParentFolder = string.Empty;
	private bool _openNewProjectPopup;
	private bool _openErrorPopup;
	private string _errorMessage = string.Empty;

	public MenuBar(
		IFileDialogService fileDialogService,
		IProjectSceneImporter sceneImporter,
		FramerateTool framerateTool,
		IEditorProjectService projectService,
		ITextureAssetImporter textureAssetImporter,
		IIconManager icons,
		IWindowChromeController windowChromeController,
		IEditorModeState editorModeState)
	{
		_fileDialogService = fileDialogService;
		_sceneImporter = sceneImporter;
		_framerateTool = framerateTool;
		_projectService = projectService;
		_textureAssetImporter = textureAssetImporter;
		_icons = icons;
		_windowChromeController = windowChromeController;
		_editorModeState = editorModeState;
	}

	public void Draw(EditorScene scene)
	{
		var style = ImGui.GetStyle();
		var pushedMaximizedMenuPadding = false;
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
		    && _windowChromeController.IsCustomChromeSupported
		    && _windowChromeController.IsMaximized)
		{
			ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(style.FramePadding.X, style.FramePadding.Y + (WindowsMaximizedTopInset * 0.5f)));
			pushedMaximizedMenuPadding = true;
		}

		if (ImGui.BeginMainMenuBar() == false)
		{
			_windowChromeController.SetTitleBarMetrics(WindowTitleBarMetrics.Empty);
			if (pushedMaximizedMenuPadding)
			{
				ImGui.PopStyleVar();
			}
			DrawPopups();
			return;
		}

		var exclusionRects = new List<WindowChromeRect>(8);
		var titleBarMin = ImGui.GetWindowPos();
		WindowChromeRect minimizeButtonRect = WindowChromeRect.Empty;
		WindowChromeRect maximizeButtonRect = WindowChromeRect.Empty;
		WindowChromeRect closeButtonRect = WindowChromeRect.Empty;
		var isWindowsChrome = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && _windowChromeController.IsCustomChromeSupported;
		var contentInset = isWindowsChrome && _windowChromeController.IsMaximized
			? WindowsMaximizedContentInset
			: 0.0f;

		if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
		{
			if (ImGui.GetCursorPosX() < MacTitlebarButtonInset)
			{
				ImGui.SetCursorPosX(MacTitlebarButtonInset);
			}
		}
		else if (contentInset > 0.0f)
		{
			ImGui.SetCursorPosY(ImGui.GetCursorPosY() + contentInset);
		}

		AddRect(exclusionRects, DrawFileMenu());
		AddRect(exclusionRects, DrawEditMenu());
		AddRect(exclusionRects, DrawImportMenu(scene));

		var rightInset = isWindowsChrome
			? (WindowsCaptionButtonWidth * 3.0f) + WindowsCaptionButtonSpacing + ImGui.GetStyle().ItemSpacing.X
			: 0.0f;
		DrawCenteredEditorModeButtons(exclusionRects, rightInset);

		// if (_projectService.HasOpenProject)
		// {
		// 	var projectLabel = Path.GetFileName(_projectService.ProjectRootPath);
		// 	ImGui.SameLine();
		// 	ImGui.TextDisabled($"Project: {projectLabel}");
		// 	AddLastItemRect(exclusionRects);
		// }

		_framerateTool.DrawRightAlignedInMenuBar(rightInset);
		AddLastItemRect(exclusionRects);

		if (isWindowsChrome)
		{
			DrawWindowsCaptionButtons(ref minimizeButtonRect, ref maximizeButtonRect, ref closeButtonRect, exclusionRects);
		}

		var titleBarMax = titleBarMin + ImGui.GetWindowSize();
		ImGui.EndMainMenuBar();
		if (pushedMaximizedMenuPadding)
		{
			ImGui.PopStyleVar();
		}
		_windowChromeController.SetTitleBarMetrics(new WindowTitleBarMetrics(
			new WindowChromeRect(titleBarMin.X, titleBarMin.Y, titleBarMax.X, titleBarMax.Y),
			minimizeButtonRect,
			maximizeButtonRect,
			closeButtonRect,
			exclusionRects.ToArray()));

		DrawPopups();
	}

	private void DrawCenteredEditorModeButtons(List<WindowChromeRect> exclusionRects, float rightInset)
	{
		const string SceneLabel = "Scene";
		const string AssetsLabel = "Assets";
		const string AnimationLabel = "Animation";
		const string FramerateTemplateLabel = "Frame 0000.00 ms avg | 0000.00 ms max";

		var style = ImGui.GetStyle();
		var leftBoundary = ImGui.GetCursorPosX() + style.ItemSpacing.X;
		var buttonHeight = ImGui.GetFrameHeight();
		var sceneWidth = ImGui.CalcTextSize(SceneLabel).X + (style.FramePadding.X * 2.0f);
		var assetsWidth = ImGui.CalcTextSize(AssetsLabel).X + (style.FramePadding.X * 2.0f);
		var animationWidth = ImGui.CalcTextSize(AnimationLabel).X + (style.FramePadding.X * 2.0f);
		var groupWidth = sceneWidth + assetsWidth + animationWidth + (style.ItemSpacing.X * 2.0f);
		var framerateWidth = ImGui.CalcTextSize(FramerateTemplateLabel).X + (style.FramePadding.X * 2.0f);
		var rightBoundary = ImGui.GetWindowWidth() - style.WindowPadding.X - rightInset - framerateWidth - style.ItemSpacing.X;
		var centeredX = (ImGui.GetWindowWidth() - groupWidth) * 0.5f;
		var startX = MathF.Max(leftBoundary, centeredX);
		if (startX + groupWidth > rightBoundary)
		{
			startX = MathF.Max(leftBoundary, rightBoundary - groupWidth);
		}
		
		var notSelectedColor = ImGui.GetStyle().Colors[(int)ImGuiCol.TitleBg];
		var currentMode = _editorModeState.CurrentMode;

		void DrawEditorModeButton(string label, float width, EditorMode mode)
		{
			var selected = currentMode == mode;
			if(selected == false) ImGui.PushStyleColor(ImGuiCol.Button, notSelectedColor);
			
			if (ImGui.Button(label, new Vector2(width, buttonHeight)))
			{
				_editorModeState.SetMode(mode);
			}
			AddLastItemRect(exclusionRects);
			
			if(selected == false) ImGui.PopStyleColor();
		}

		ImGui.SameLine();
		ImGui.SetCursorPosX(startX);
		DrawEditorModeButton(SceneLabel, sceneWidth, EditorMode.Scene);

		
		ImGui.SameLine();
		DrawEditorModeButton(AssetsLabel, assetsWidth, EditorMode.Assets);

		ImGui.SameLine();
		DrawEditorModeButton(AnimationLabel, animationWidth, EditorMode.Animation);

	}

	private void DrawWindowsCaptionButtons(
		ref WindowChromeRect minimizeButtonRect,
		ref WindowChromeRect maximizeButtonRect,
		ref WindowChromeRect closeButtonRect,
		List<WindowChromeRect> exclusionRects)
	{
		var buttonHeight = MathF.Max(ImGui.GetWindowHeight() - 2.0f, 20.0f);
		var buttonSize = new Vector2(WindowsCaptionButtonWidth, buttonHeight);
		var style = ImGui.GetStyle();
		var contentInset = _windowChromeController.IsMaximized ? WindowsMaximizedContentInset : 0.0f;
		var topOffset = MathF.Max((ImGui.GetWindowHeight() - buttonHeight) * 0.5f, 0.0f) + contentInset;
		var rightEdge = ImGui.GetWindowWidth();
		var closeX = rightEdge - buttonSize.X;
		var maximizeX = closeX - WindowsCaptionButtonSpacing - buttonSize.X;
		var minimizeX = maximizeX - WindowsCaptionButtonSpacing - buttonSize.X;

		minimizeButtonRect = DrawWindowsCaptionButton("TitleMinimize", "minimize", minimizeX, topOffset, buttonSize, false, () => _windowChromeController.Minimize());
		exclusionRects.Add(minimizeButtonRect);

		var maximizeIcon = _windowChromeController.IsMaximized ? "window" : "square";
		maximizeButtonRect = DrawWindowsCaptionButton("TitleMaximize", maximizeIcon, maximizeX, topOffset, buttonSize, false, () => _windowChromeController.ToggleMaximize());
		exclusionRects.Add(maximizeButtonRect);

		closeButtonRect = DrawWindowsCaptionButton("TitleClose", "close", closeX, topOffset, buttonSize, true, () => _windowChromeController.Close());
		exclusionRects.Add(closeButtonRect);
	}

	private WindowChromeRect DrawWindowsCaptionButton(
		string id,
		string iconName,
		float x,
		float y,
		Vector2 size,
		bool isCloseButton,
		Action onClick)
	{
		ImGui.SetCursorPos(new Vector2(x, y));

		var baseColor = new Vector4(0.0f, 0.0f, 0.0f, 0.0f);
		var hoveredColor = isCloseButton ? new Vector4(0.741f, 0.184f, 0.224f, 1.0f) : new Vector4(1.0f, 1.0f, 1.0f, 0.12f);
		var activeColor = isCloseButton ? new Vector4(0.616f, 0.157f, 0.188f, 1.0f) : new Vector4(1.0f, 1.0f, 1.0f, 0.18f);
		var imageSize = Vector2.One * 14.0f;
		var framePadding = new Vector2(
			MathF.Max((size.X - imageSize.X) * 0.5f, 0.0f),
			MathF.Max((size.Y - imageSize.Y) * 0.5f, 0.0f));

		ImGui.PushStyleColor(ImGuiCol.Button, baseColor);
		ImGui.PushStyleColor(ImGuiCol.ButtonHovered, hoveredColor);
		ImGui.PushStyleColor(ImGuiCol.ButtonActive, activeColor);
		ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 0.0f);
		ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0.0f);
		ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, framePadding);

		if (ImGui.ImageButton(id, _icons.Get(iconName), imageSize))
		{
			onClick();
		}

		var rect = GetLastItemRect();
		ImGui.PopStyleVar(3);
		ImGui.PopStyleColor(3);
		return rect;
	}

	private static void AddLastItemRect(List<WindowChromeRect> rects)
	{
		AddRect(rects, GetLastItemRect());
	}

	private static void AddRect(List<WindowChromeRect> rects, WindowChromeRect rect)
	{
		if (rect.IsEmpty == false)
		{
			rects.Add(rect);
		}
	}

	private static WindowChromeRect GetLastItemRect()
	{
		var min = ImGui.GetItemRectMin();
		var max = ImGui.GetItemRectMax();
		return new WindowChromeRect(min.X, min.Y, max.X, max.Y);
	}

	private WindowChromeRect DrawFileMenu()
	{
		var isOpen = ImGui.BeginMenu("File");
		var menuRect = GetLastItemRect();
		if (isOpen == false)
		{
			return menuRect;
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
		return menuRect;
	}

	private static WindowChromeRect DrawEditMenu()
	{
		var isOpen = ImGui.BeginMenu("Edit");
		var menuRect = GetLastItemRect();
		if (isOpen)
		{
			ImGui.EndMenu();
		}

		return menuRect;
	}

	private WindowChromeRect DrawImportMenu(EditorScene scene)
	{
		var isOpen = ImGui.BeginMenu("Import");
		var menuRect = GetLastItemRect();
		if (isOpen == false)
		{
			return menuRect;
		}

		var hasOpenProject = _projectService.HasOpenProject;
		if (hasOpenProject == false)
		{
			ImGui.BeginDisabled();
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
				try
				{
					_sceneImporter.ImportScene(path, scene.World);
				}
				catch (Exception ex)
				{
					ShowError(ex.Message);
				}
			}
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
		return menuRect;
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

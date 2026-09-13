using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using WolfEngine.Build;
using WolfEngine.Editor.Projects;
using WolfEngine.Platform;
using WolfEngine.Utility;

namespace WolfEngine.Editor.UI;

public interface IMenuBar
{
	public void Draw(EditorScene scene);
}

public sealed class MenuBar : IMenuBar
{
	private const string NewProjectPopupId = "New Project";
	private const string NotificationPopupId = "Editor Notification";
	private const string WorkspaceNamePopupId = "Workspace Name";
	private const string DeleteWorkspacePopupId = "Delete Workspace";
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
	private readonly IAudioAssetImporter _audioAssetImporter;
	private readonly IIconManager _icons;
	private readonly IWindowChromeController _windowChromeController;
	private readonly IEditorWorkspaceService _workspaces;
	private readonly EditorWindowRegistry _windows;
	private readonly IEditorSceneWorkspace _sceneWorkspace;
	private readonly IEditorPlaySession _playSession;
	private readonly IGameplayAssemblyHost _gameplayAssemblyHost;
	private readonly IEditorNotificationService _notificationService;
	private readonly IEditorCommandService _commandService;
	private readonly IGameBuildService _gameBuildService;
	private readonly IEditorOperationService _operationService;

	private string _newProjectName = string.Empty;
	private string _newProjectParentFolder = string.Empty;
	private bool _openNewProjectPopup;
	private bool _openNotificationPopup;
	private string _notificationTitle = string.Empty;
	private string _notificationMessage = string.Empty;
	private string _workspaceName = string.Empty;
	private string _workspaceNameError = string.Empty;
	private Guid? _renameWorkspaceId;
	private Guid? _deleteWorkspaceId;
	private Guid? _draggingWorkspaceId;
	private bool _openWorkspaceNamePopup;
	private bool _openDeleteWorkspacePopup;

	public MenuBar(
		IFileDialogService fileDialogService,
		IProjectSceneImporter sceneImporter,
		FramerateTool framerateTool,
		IEditorProjectService projectService,
		ITextureAssetImporter textureAssetImporter,
		IAudioAssetImporter audioAssetImporter,
		IIconManager icons,
		IWindowChromeController windowChromeController,
		IEditorWorkspaceService workspaces,
		EditorWindowRegistry windows,
		IEditorSceneWorkspace sceneWorkspace,
		IEditorPlaySession playSession,
		IGameplayAssemblyHost gameplayAssemblyHost,
		IEditorNotificationService notificationService,
		IEditorCommandService commandService,
		IGameBuildService gameBuildService,
		IEditorOperationService operationService)
	{
		_fileDialogService = fileDialogService;
		_sceneImporter = sceneImporter;
		_framerateTool = framerateTool;
		_projectService = projectService;
		_textureAssetImporter = textureAssetImporter;
		_audioAssetImporter = audioAssetImporter;
		_icons = icons;
		_windowChromeController = windowChromeController;
		_workspaces = workspaces;
		_windows = windows;
		_sceneWorkspace = sceneWorkspace ?? throw new ArgumentNullException(nameof(sceneWorkspace));
		_playSession = playSession ?? throw new ArgumentNullException(nameof(playSession));
		_gameplayAssemblyHost = gameplayAssemblyHost ?? throw new ArgumentNullException(nameof(gameplayAssemblyHost));
		_notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
		_commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));
		_gameBuildService = gameBuildService ?? throw new ArgumentNullException(nameof(gameBuildService));
		_operationService = operationService ?? throw new ArgumentNullException(nameof(operationService));
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
		AddRect(exclusionRects, DrawWindowMenu());
		
		AddRect(exclusionRects, DrawGameplayReloadButton());
		AddRect(exclusionRects, DrawPlayControls());

		var rightInset = isWindowsChrome
			? (WindowsCaptionButtonWidth * 3.0f) + WindowsCaptionButtonSpacing + ImGui.GetStyle().ItemSpacing.X
			: 0.0f;
		DrawWorkspaceTabs(exclusionRects, rightInset);

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

	private void DrawWorkspaceTabs(List<WindowChromeRect> exclusionRects, float rightInset)
	{
		const string FramerateTemplateLabel = "Frame 0000.00 ms avg | 0000.00 ms max";

		var style = ImGui.GetStyle();
		var leftBoundary = ImGui.GetCursorPosX() + style.ItemSpacing.X;
		var buttonHeight = ImGui.GetFrameHeight();
		var framerateWidth = ImGui.CalcTextSize(FramerateTemplateLabel).X + (style.FramePadding.X * 2.0f);
		var rightBoundary = ImGui.GetWindowWidth() - style.WindowPadding.X - rightInset - framerateWidth - style.ItemSpacing.X;
		var controlsWidth = buttonHeight + style.ItemSpacing.X;
		var desiredTabWidth = _workspaces.Workspaces.Sum(workspace => ImGui.CalcTextSize(workspace.Name).X + style.FramePadding.X * 2.0f + style.ItemSpacing.X);
		var groupWidth = MathF.Min(MathF.Max(160.0f, desiredTabWidth + controlsWidth), MathF.Max(160.0f, rightBoundary - leftBoundary));
		var startX = MathF.Max(leftBoundary, MathF.Min((ImGui.GetWindowWidth() - groupWidth) * 0.5f, rightBoundary - groupWidth));
		var notSelectedColor = ImGui.GetStyle().Colors[(int)ImGuiCol.TitleBg];
		var activeId = _workspaces.ActiveWorkspace.Id;
		Guid? moveId = null;
		var moveIndex = -1;

		ImGui.SameLine();
		ImGui.SetCursorPosX(startX);
		var childWidth = MathF.Max(80.0f, groupWidth - controlsWidth);
		ImGui.SetNextWindowContentSize(new Vector2(desiredTabWidth, 0.0f));
		ImGui.BeginChild("WorkspaceTabs", new Vector2(childWidth, buttonHeight), ImGuiChildFlags.None,
			ImGuiWindowFlags.HorizontalScrollbar | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoSavedSettings);
		for (var index = 0; index < _workspaces.Workspaces.Count; index++)
		{
			var workspace = _workspaces.Workspaces[index];
			if (index > 0) ImGui.SameLine();
			var selected = activeId == workspace.Id;
			if(selected == false) ImGui.PushStyleColor(ImGuiCol.Button, notSelectedColor);
			var width = ImGui.CalcTextSize(workspace.Name).X + style.FramePadding.X * 2.0f;
			if (ImGui.Button($"{workspace.Name}##Workspace-{workspace.Id:N}", new Vector2(width, buttonHeight)))
			{
				_workspaces.Activate(workspace.Id);
			}
			if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) BeginRenameWorkspace(workspace);
			if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left)) _draggingWorkspaceId = workspace.Id;
			if (_draggingWorkspaceId is { } dragged && dragged != workspace.Id &&
			    ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem))
			{
				moveId = dragged;
				moveIndex = index;
			}
			if (ImGui.BeginPopupContextItem($"WorkspaceContext-{workspace.Id:N}"))
			{
				if (ImGui.MenuItem("Rename")) BeginRenameWorkspace(workspace);
				if (_workspaces.Workspaces.Count == 1) ImGui.BeginDisabled();
				if (ImGui.MenuItem("Delete Workspace")) { _deleteWorkspaceId = workspace.Id; _openDeleteWorkspacePopup = true; }
				if (_workspaces.Workspaces.Count == 1) ImGui.EndDisabled();
				ImGui.EndPopup();
			}
			if(selected == false) ImGui.PopStyleColor();
		}
		ImGui.EndChild();
		if (moveId is { } id) _workspaces.Move(id, moveIndex);
		if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) _draggingWorkspaceId = null;
		ImGui.SameLine();
		ImGui.PushStyleColor(ImGuiCol.Button, notSelectedColor);
		if (ImGui.Button("+", new Vector2(buttonHeight, buttonHeight))) BeginCreateWorkspace();
		ImGui.PopStyleColor();
		AddRect(exclusionRects, new WindowChromeRect(
			ImGui.GetWindowPos().X + startX, ImGui.GetWindowPos().Y,
			ImGui.GetWindowPos().X + startX + groupWidth, ImGui.GetWindowPos().Y + ImGui.GetWindowHeight()));
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

		var authoringLocked = _playSession.IsActive;
		if (authoringLocked)
		{
			ImGui.BeginDisabled();
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
				_operationService.TryStart(
					"Opening project",
					progress =>
					{
						progress.Report("Loading project assets...");
						if (_projectService.OpenProject(selectedFolder, out var errorMessage) == false) throw new InvalidOperationException(errorMessage);
					},
					completed: PersistLastOpenedProject,
					failed: exception => ShowError($"Failed to open project: {exception.Message}"));
			}
		}

		if (ImGui.MenuItem("New Scene", "Ctrl/Cmd+N"))
		{
			_commandService.RequestNewScene();
		}

		var hasOpenProject = _projectService.HasOpenProject;
		if (hasOpenProject == false)
		{
			ImGui.BeginDisabled();
		}

		if (ImGui.MenuItem("Save Scene", "Ctrl/Cmd+S"))
		{
			_commandService.SaveScene();
		}

		if (ImGui.MenuItem("Refresh Asset Database", "Ctrl/Cmd+R"))
		{
			_commandService.RefreshAssetDatabase();
		}

		if (ImGui.MenuItem("Reload Engine Shaders"))
		{
			_commandService.ReloadEngineShaders();
		}

		if (ImGui.MenuItem("Build..."))
		{
			var outputFolder = _fileDialogService.OpenFolder(new FileDialogOptions
			{
				Title = "Choose Build Output Folder"
			});
			if (string.IsNullOrWhiteSpace(outputFolder) == false)
			{
				GameBuildResult? buildResult = null;
				_operationService.TryStart(
					"Building game",
					progress =>
					{
						progress.Report("Cooking assets and compiling gameplay...");
						buildResult = _gameBuildService.Build(outputFolder);
					},
					completed: () =>
					{
						if (buildResult is { } result)
							_notificationService.ReportInfo($"Built {result.CookedAssetCount} assets to '{result.OutputPath}'.");
					},
					failed: exception => ShowError($"Game build failed: {exception.Message}"));
			}
		}

		if (hasOpenProject == false)
		{
			ImGui.EndDisabled();
		}

		if (authoringLocked)
		{
			ImGui.EndDisabled();
		}

		if (ImGui.MenuItem("Preferences"))
		{
			_windows.Open(EditorWindowIds.Preferences);
		}

		ImGui.EndMenu();
		return menuRect;
	}

	private WindowChromeRect DrawEditMenu()
	{
		var isOpen = ImGui.BeginMenu("Edit");
		var menuRect = GetLastItemRect();
		if (isOpen)
		{
			if (ImGui.MenuItem("Project Settings..."))
			{
				_windows.Open(EditorWindowIds.ProjectSettings);
			}
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
		if (hasOpenProject == false || _playSession.IsActive)
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

		if (ImGui.MenuItem("Import Audio..."))
		{
			var result = _audioAssetImporter.ImportAudio();
			if (!result.Success && !result.Cancelled) ShowError(result.ErrorMessage ?? "Audio import failed.");
		}

		if (ImGui.MenuItem("Material..."))
		{
			_windows.Open(EditorWindowIds.MaterialImporter);
		}

		if (hasOpenProject == false || _playSession.IsActive)
		{
			ImGui.EndDisabled();
		}

		ImGui.EndMenu();
		return menuRect;
	}

	private WindowChromeRect DrawWindowMenu()
	{
		var isOpen = ImGui.BeginMenu("Window");
		var menuRect = GetLastItemRect();
		if (isOpen)
		{
			foreach (var window in _windows.Windows)
			{
				var isWindowOpen = _workspaces.IsWindowOpen(window.Id);
				if (ImGui.MenuItem(window.DisplayName, string.Empty, isWindowOpen)) _windows.Open(window.Id);
			}
			ImGui.EndMenu();
		}

		return menuRect;
	}

	private WindowChromeRect DrawPlayControls()
	{
		ImGui.SameLine();
		WindowChromeRect rect;
		switch (_playSession.State)
		{
			case EditorPlayState.Edit:
				if (ImGui.ImageButton("Play", _icons.Get("play"), Vector2.One * 15.5f))
				{
					_playSession.EnterPlay();
				}
				rect = GetLastItemRect();
				break;
			case EditorPlayState.Playing:
				if (ImGui.ImageButton("Pause", _icons.Get("pause"), Vector2.One * 15.5f))
				{
					_playSession.Pause();
				}
				rect = GetLastItemRect();
				ImGui.SameLine();
				if (ImGui.ImageButton("Stop", _icons.Get("stop"), Vector2.One * 15.5f))
				{
					_playSession.Stop();
				}
				rect = Union(rect, GetLastItemRect());
				break;
			case EditorPlayState.Paused:
				if (ImGui.ImageButton("Resume", _icons.Get("play"), Vector2.One * 15.5f))
				{
					_playSession.Resume();
				}
				rect = GetLastItemRect();
				ImGui.SameLine();
				if (ImGui.ImageButton("Stop", _icons.Get("stop"), Vector2.One * 15.5f))
				{
					_playSession.Stop();
				}
				rect = Union(rect, GetLastItemRect());
				break;
			default:
				rect = WindowChromeRect.Empty;
				break;
		}

		return rect;
	}

	private WindowChromeRect DrawGameplayReloadButton()
	{
		var hasOpenProject = _projectService.HasOpenProject;
		var isBuildInProgress = _gameplayAssemblyHost.IsBuildInProgress;
		if (hasOpenProject == false || isBuildInProgress)
		{
			ImGui.BeginDisabled();
		}

		ImGui.SameLine();
		var clicked = ImGui.ImageButton("GameplayReload", _icons.Get("refresh"), Vector2.One * 15.5f);
		var buttonRect = GetLastItemRect();
		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip(isBuildInProgress ? "Gameplay build is already running." : "Build and reload gameplay");
		}

		if (hasOpenProject == false || isBuildInProgress)
		{
			ImGui.EndDisabled();
		}

		if (clicked)
		{
			_gameplayAssemblyHost.RequestBuildAndReload();
		}

		return buttonRect;
	}

	private static WindowChromeRect Union(WindowChromeRect left, WindowChromeRect right)
	{
		if (left.IsEmpty)
		{
			return right;
		}

		if (right.IsEmpty)
		{
			return left;
		}

		return new WindowChromeRect(
			MathF.Min(left.Left, right.Left),
			MathF.Min(left.Top, right.Top),
			MathF.Max(left.Right, right.Right),
			MathF.Max(left.Bottom, right.Bottom));
	}

	private void DrawPopups()
	{
		while (_notificationService.TryDequeue(out var notification))
		{
			_notificationTitle = notification.Kind == EditorNotificationKind.Error ? "Error" : "Notice";
			_notificationMessage = notification.Message;
			_openNotificationPopup = true;
		}

		if (_openNewProjectPopup)
		{
			ImGui.OpenPopup(NewProjectPopupId);
			_openNewProjectPopup = false;
		}

		if (_openNotificationPopup)
		{
			ImGui.OpenPopup(NotificationPopupId);
			_openNotificationPopup = false;
		}
		if (_openWorkspaceNamePopup)
		{
			ImGui.OpenPopup(WorkspaceNamePopupId);
			_openWorkspaceNamePopup = false;
		}
		if (_openDeleteWorkspacePopup)
		{
			ImGui.OpenPopup(DeleteWorkspacePopupId);
			_openDeleteWorkspacePopup = false;
		}

		DrawNewProjectPopup();
		DrawNotificationPopup();
		DrawWorkspaceNamePopup();
		DrawDeleteWorkspacePopup();
	}

	private void BeginCreateWorkspace()
	{
		_renameWorkspaceId = null;
		_workspaceName = string.Empty;
		_workspaceNameError = string.Empty;
		_openWorkspaceNamePopup = true;
	}

	private void BeginRenameWorkspace(EditorWorkspace workspace)
	{
		_renameWorkspaceId = workspace.Id;
		_workspaceName = workspace.Name;
		_workspaceNameError = string.Empty;
		_openWorkspaceNamePopup = true;
	}

	private void DrawWorkspaceNamePopup()
	{
		var open = true;
		if (!ImGui.BeginPopupModal(WorkspaceNamePopupId, ref open, ImGuiWindowFlags.AlwaysAutoResize)) return;
		if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
		ImGui.SetNextItemWidth(360.0f);
		var confirm = ImGui.InputText("Name", ref _workspaceName, 65, ImGuiInputTextFlags.EnterReturnsTrue);
		if (!string.IsNullOrEmpty(_workspaceNameError)) ImGui.TextColored(new Vector4(0.95f, 0.35f, 0.35f, 1.0f), _workspaceNameError);
		confirm |= ImGui.Button(_renameWorkspaceId.HasValue ? "Rename" : "Create", new Vector2(100.0f, 0.0f));
		ImGui.SameLine();
		if (ImGui.Button("Cancel", new Vector2(100.0f, 0.0f)) || ImGui.IsKeyPressed(ImGuiKey.Escape)) ImGui.CloseCurrentPopup();
		if (confirm)
		{
			var success = _renameWorkspaceId is { } id
				? _workspaces.TryRename(id, _workspaceName, out _workspaceNameError)
				: _workspaces.TryCreate(_workspaceName, out _, out _workspaceNameError);
			if (success) ImGui.CloseCurrentPopup();
		}
		ImGui.EndPopup();
	}

	private void DrawDeleteWorkspacePopup()
	{
		var open = true;
		if (!ImGui.BeginPopupModal(DeleteWorkspacePopupId, ref open, ImGuiWindowFlags.AlwaysAutoResize)) return;
		var workspace = _deleteWorkspaceId is { } id ? _workspaces.Workspaces.FirstOrDefault(item => item.Id == id) : null;
		ImGui.TextWrapped(workspace is null ? "This workspace no longer exists." : $"Delete workspace '{workspace.Name}' and its layout?");
		if (workspace is not null && ImGui.Button("Delete", new Vector2(100.0f, 0.0f)))
		{
			_workspaces.Delete(workspace.Id);
			_deleteWorkspaceId = null;
			ImGui.CloseCurrentPopup();
		}
		if (workspace is not null) ImGui.SameLine();
		if (ImGui.Button("Cancel", new Vector2(100.0f, 0.0f)) || ImGui.IsKeyPressed(ImGuiKey.Escape)) ImGui.CloseCurrentPopup();
		ImGui.EndPopup();
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

	private void DrawNotificationPopup()
	{
		var isOpen = true;
		ImGui.SetNextWindowSize(new Vector2(480.0f, 0.0f), ImGuiCond.Appearing);
		if (ImGui.BeginPopupModal(NotificationPopupId, ref isOpen, ImGuiWindowFlags.AlwaysAutoResize) == false)
		{
			return;
		}

		if (string.IsNullOrWhiteSpace(_notificationTitle) == false)
		{
			ImGui.TextUnformatted(_notificationTitle);
			ImGui.Separator();
		}

		ImGui.TextWrapped(_notificationMessage);
		ImGui.Spacing();
		if (ImGui.Button("OK", new Vector2(100.0f, 0.0f)))
		{
			_notificationTitle = string.Empty;
			_notificationMessage = string.Empty;
			ImGui.CloseCurrentPopup();
		}

		ImGui.EndPopup();
	}

	private void ShowError(string errorMessage)
	{
		_notificationService.ReportError(errorMessage);
	}

	private void PersistLastOpenedProject()
	{
		EditorPreferences.SetLastProjectPath(_projectService.ProjectRootPath);
		EditorPreferences.Save();
	}
}

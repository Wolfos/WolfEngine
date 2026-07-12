using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using WolfEngine.Utility;
using Wolfie.IAE.Projects;
using Wolfie.IAE.UnityAssets;
using Wolfie.IAE.ManagedAssets;
using Wolfie.IAE.ExternalTools;

namespace Wolfie.IAE.UI;

public sealed class WolfieGui(
	WolfieProjectService projectService,
	UnityAssetScanner assetScanner,
	ManagedAssetService managedAssets,
	BlenderLauncher blenderLauncher,
	IBlenderModelPublisher modelPublisher,
	ManagedSourceAutoPublisher autoPublisher,
	IFileDialogService fileDialog,
	IIconManager icons,
	WolfiePreferences preferences)
{
	private const float MacTitlebarButtonInset = 70.0f;
	private const float FolderTreeWidth = 220.0f;
	private const float FolderTreeIconSize = 15.5f;
	private const float GridMinItemWidth = 132.0f;
	private const float CardHeight = 104.0f;
	private WolfieProject? _project;
	private string? _projectFile;
	private UnityAssetScanResult? _assets;
	private string _unityPath = string.Empty;
	private string _parentLocation = string.Empty;
	private string _projectName = string.Empty;
	private string _selectedPath = string.Empty;
	private string _selectedFolderPath = "Assets";
	private string _searchText = string.Empty;
	private string _error = string.Empty;
	private bool _startupRestoreAttempted;
	private WolfieTemplate? _pendingTemplate;
	private string _newAssetName = string.Empty;
	private string _creationError = string.Empty;
	private bool _openCreatePopup;
	private bool _focusCreateName;
	private bool _openPreferencesPopup;
	private string _blenderPath = string.Empty;
	private string _preferencesError = string.Empty;
	private Task? _publishTask;
	private string? _publishingPath;

	public void Draw()
	{
		if (!_startupRestoreAttempted)
		{
			_startupRestoreAttempted = true;
			RestoreLastProject();
		}
		DrawDockSpace();
		DrawMainMenu();
		DrawPreferencesPopup();
		if (_project is null) DrawStartup();
		else DrawWorkspace();
	}

	private void DrawStartup()
	{
		ImGui.SetNextWindowSize(new Vector2(720, 430), ImGuiCond.FirstUseEver);
		ImGui.Begin("Welcome to Wolfie", ImGuiWindowFlags.NoCollapse);
		ImGui.Text("Integrated Asset Environment");
		ImGui.Separator();
		if (ImGui.Button("Open an existing Wolfie project", new Vector2(-1, 42))) OpenProject();
		ImGui.Spacing();
		ImGui.Text("Create a new Wolfie project");
		ImGui.Spacing();
		PathField("Unity project", ref _unityPath, "Select Unity Project");
		PathField("Wolfie project location (parent folder)", ref _parentLocation, "Select Parent Folder for Wolfie Project");
		ImGui.Text("Wolfie project name");
		ImGui.SetNextItemWidth(-1);
		ImGui.InputText("##WolfieName", ref _projectName, 256);
		ImGui.TextDisabled("A new folder with this name will be created.");
		if (!string.IsNullOrWhiteSpace(_parentLocation) && !string.IsNullOrWhiteSpace(_projectName))
			ImGui.TextDisabled($"Will create: {Path.Combine(_parentLocation, _projectName.Trim())}");
		if (ImGui.Button("Create Project", new Vector2(-1, 38))) CreateProject();
		DrawError();
		ImGui.End();
	}

	private void PathField(string label, ref string value, string dialogTitle)
	{
		ImGui.Text(label);
		ImGui.SetNextItemWidth(-88);
		ImGui.InputText("##" + label, ref value, 2048);
		ImGui.SameLine();
		if (ImGui.Button("Browse##" + label, new Vector2(80, 0)))
		{
			var selected = fileDialog.OpenFolder(new FileDialogOptions { Title = dialogTitle, InitialDirectory = value });
			if (selected is not null) value = selected;
		}
	}

	private void DrawWorkspace()
	{
		CompletePublishIfReady();
		DrainAutoPublishNotifications();
		ImGui.Begin("Assets");
		ImGui.Text($"Wolfie: {_project!.Name}");
		ImGui.TextDisabled(_projectFile ?? string.Empty);
		ImGui.Text($"Connected Unity project: {Path.GetFileName(_project.UnityProjectPath)}");
		ImGui.TextDisabled(_project.UnityProjectPath);
		if (DrawIconButton("refresh", "Refresh")) Refresh();
		ImGui.SameLine();
		ImGui.TextDisabled(_publishingPath is null ? "Unity-only content (read-only)" :
			$"Publishing {Path.GetFileName(_publishingPath)}...");
		ImGui.Separator();
		if (_assets is { Warnings.Count: > 0 })
			ImGui.TextDisabled($"Scan completed with {_assets.Warnings.Count} inaccessible item(s).");
		DrawError();
		if (_assets is not null)
		{
			ImGui.BeginChild("AssetBrowserArea", new Vector2(0, -ImGui.GetFrameHeightWithSpacing()),
				ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
			DrawAssetBrowser(_assets.Root);
			ImGui.EndChild();
		}
		ImGui.Separator();
		ImGui.Text("Selected path:");
		ImGui.SameLine();
		ImGui.TextDisabled(string.IsNullOrEmpty(_selectedPath) ? "None" : _selectedPath);
		ImGui.End();
	}

	private void DrawMainMenu()
	{
		if (ImGui.BeginMainMenuBar())
		{
			if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && ImGui.GetCursorPosX() < MacTitlebarButtonInset)
				ImGui.SetCursorPosX(MacTitlebarButtonInset);
			if (ImGui.BeginMenu("Project"))
			{
				if (ImGui.MenuItem("Open...")) OpenProject();
				if (ImGui.MenuItem("Close", string.Empty, false, _project is not null)) CloseProject();
				ImGui.EndMenu();
			}
			if (ImGui.BeginMenu("Edit"))
			{
				if (ImGui.MenuItem("Preferences...")) OpenPreferences();
				ImGui.EndMenu();
			}
			ImGui.EndMainMenuBar();
		}
	}

	private void DrawAssetBrowser(UnityAssetEntry root)
	{
		var selectedFolder = FindEntry(root, _selectedFolderPath) ?? root.Children.First();
		ImGui.BeginChild("UnityFolderTree", new Vector2(FolderTreeWidth, 0));
		foreach (var topLevel in root.Children) DrawFolderTreeNode(topLevel);
		ImGui.EndChild();
		ImGui.SameLine(0, 0);
		DrawVerticalSeparator();
		ImGui.SameLine(0, 0);
		ImGui.BeginGroup();
		DrawBrowserHeader(selectedFolder.RelativePath);
		ImGui.Separator();
		ImGui.BeginChild("UnityAssetGrid");
		DrawGrid(selectedFolder);
		DrawCreateContextMenu(selectedFolder);
		ImGui.EndChild();
		ImGui.EndGroup();
		DrawCreateAssetPopup();
	}

	private void DrawFolderTreeNode(UnityAssetEntry entry)
	{
		ImGui.PushID(entry.RelativePath);
		var folderSelected = string.Equals(_selectedFolderPath, entry.RelativePath, StringComparison.Ordinal);
		var folders = entry.Children.Where(child => child.Type == UnityAssetEntryType.Folder).ToArray();
		var flags = ImGuiTreeNodeFlags.SpanFullWidth | ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.FramePadding |
		            (folderSelected ? ImGuiTreeNodeFlags.Selected : 0);
		if (folders.Length == 0) flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;
		if (entry.RelativePath == "Assets") flags |= ImGuiTreeNodeFlags.DefaultOpen;
		ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.62f, 0.65f, 0.69f, 1));
		var nodeCursorX = ImGui.GetCursorScreenPos().X;
		var open = ImGui.TreeNodeEx("##FolderNode", flags);
		ImGui.PopStyleColor();
		var itemMin = ImGui.GetItemRectMin();
		var itemMax = ImGui.GetItemRectMax();
		var rowHeight = itemMax.Y - itemMin.Y;
		var labelStartX = nodeCursorX + ImGui.GetTreeNodeToLabelSpacing();
		var iconSize = MathF.Min(FolderTreeIconSize, MathF.Max(1, rowHeight - 2));
		var iconPosition = new Vector2(labelStartX, itemMin.Y + (rowHeight - iconSize) * .5f);
		if (icons.TryGet("folder", out var treeIcon))
			ImGui.GetWindowDrawList().AddImage(treeIcon, iconPosition, iconPosition + Vector2.One * iconSize);
		var textSize = ImGui.CalcTextSize(entry.Name);
		var textPosition = new Vector2(iconPosition.X + iconSize + 4, itemMin.Y + (rowHeight - textSize.Y) * .5f);
		var folderNameColor = entry.IsManaged ? ImGui.GetColorU32(ImGuiCol.Text) : ImGui.GetColorU32(ImGuiCol.TextDisabled);
		ImGui.GetWindowDrawList().AddText(textPosition, folderNameColor, entry.Name);
		if (ImGui.IsItemClicked()) { _selectedFolderPath = entry.RelativePath; _selectedPath = entry.RelativePath; }
		if (open && folders.Length > 0)
		{
			foreach (var child in folders) DrawFolderTreeNode(child);
			ImGui.TreePop();
		}
		ImGui.PopID();
	}

	private void DrawBrowserHeader(string relativePath)
	{
		var parts = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
		var currentPath = string.Empty;
		for (var i = 0; i < parts.Length; i++)
		{
			currentPath = i == 0 ? parts[i] : currentPath + "/" + parts[i];
			if (i > 0) { ImGui.SameLine(0, 6); ImGui.TextDisabled(">"); ImGui.SameLine(0, 6); }
			if (ImGui.SmallButton(parts[i])) { _selectedFolderPath = currentPath; _selectedPath = currentPath; }
		}
		var style = ImGui.GetStyle();
		const float searchWidth = 220;
		var labelWidth = ImGui.CalcTextSize("Search").X;
		var currentX = ImGui.GetCursorPosX();
		var contentMaxX = currentX + ImGui.GetContentRegionAvail().X;
		var start = MathF.Max(currentX + style.ItemSpacing.X,
			contentMaxX - searchWidth - labelWidth - style.ItemInnerSpacing.X);
		ImGui.SameLine(); ImGui.SetCursorPosX(start); ImGui.TextDisabled("Search"); ImGui.SameLine();
		ImGui.SetNextItemWidth(searchWidth); ImGui.InputText("##WolfieAssetSearch", ref _searchText, 256);
	}

	private void DrawGrid(UnityAssetEntry folder)
	{
		var items = folder.Children; // Search is intentionally visual-only in this milestone.
		if (items.Count == 0) { ImGui.TextDisabled("This folder is empty."); return; }
		var columns = Math.Max(1, (int)MathF.Floor(MathF.Max(ImGui.GetContentRegionAvail().X, GridMinItemWidth) / GridMinItemWidth));
		if (!ImGui.BeginTable("WolfieAssetsGrid", columns, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.PadOuterX)) return;
		foreach (var entry in items)
		{
			ImGui.TableNextColumn();
			DrawCard(entry);
		}
		ImGui.EndTable();
	}

	private void DrawCreateContextMenu(UnityAssetEntry folder)
	{
		if (!folder.RelativePath.Equals("Assets", StringComparison.Ordinal) &&
		    !folder.RelativePath.StartsWith("Assets/", StringComparison.Ordinal)) return;
		if (!ImGui.BeginPopupContextWindow("AssetFolderActions",
			ImGuiPopupFlags.MouseButtonRight | ImGuiPopupFlags.NoOpenOverItems)) return;
		if (ImGui.BeginMenu("Create"))
		{
			IReadOnlyList<WolfieTemplate> templates = [];
			try { templates = managedAssets.GetTemplates(_projectFile!); }
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{ _error = $"Could not list templates: {exception.Message}"; }
			if (templates.Count == 0) ImGui.TextDisabled("No templates");
			foreach (var template in templates)
			{
				if (ImGui.MenuItem(template.Name + "##" + template.RelativePath))
				{
					_pendingTemplate = template;
					_newAssetName = template.Name;
					_creationError = string.Empty;
					_focusCreateName = true;
					_openCreatePopup = true;
				}
				if (ImGui.IsItemHovered()) ImGui.SetTooltip(template.RelativePath);
			}
			ImGui.EndMenu();
		}
		ImGui.EndPopup();
	}

	private void DrawCreateAssetPopup()
	{
		if (_openCreatePopup) { ImGui.OpenPopup("Create Managed Asset"); _openCreatePopup = false; }
		var isOpen = true;
		ImGui.SetNextWindowSize(new Vector2(420, 0), ImGuiCond.Appearing);
		if (!ImGui.BeginPopupModal("Create Managed Asset", ref isOpen, ImGuiWindowFlags.AlwaysAutoResize)) return;
		if (_pendingTemplate is not null)
		{
			ImGui.TextUnformatted("Create from " + _pendingTemplate.Name);
			ImGui.Spacing();
			ImGui.SetNextItemWidth(280);
			if (_focusCreateName) { ImGui.SetKeyboardFocusHere(); _focusCreateName = false; }
			var submitted = ImGui.InputText("##NewManagedAssetName", ref _newAssetName, 256,
				ImGuiInputTextFlags.EnterReturnsTrue);
			ImGui.SameLine();
			ImGui.TextDisabled(Path.GetExtension(_pendingTemplate.RelativePath));
			if (!string.IsNullOrWhiteSpace(_creationError))
			{
				ImGui.Spacing();
				ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1, .35f, .3f, 1));
				ImGui.TextWrapped(_creationError);
				ImGui.PopStyleColor();
			}
			ImGui.Spacing();
			if (submitted || ImGui.Button("Create", new Vector2(100, 0)))
			{
				try
				{
					managedAssets.CreateFromTemplate(_projectFile!, _selectedFolderPath,
						_pendingTemplate.RelativePath, _newAssetName);
					_pendingTemplate = null;
					Refresh();
					ImGui.CloseCurrentPopup();
				}
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
					ArgumentException or InvalidOperationException)
				{ _creationError = exception.Message; }
			}
			ImGui.SameLine();
			if (ImGui.Button("Cancel", new Vector2(100, 0))) { _pendingTemplate = null; ImGui.CloseCurrentPopup(); }
		}
		ImGui.EndPopup();
	}

	private void OpenPreferences()
	{
		_blenderPath = preferences.BlenderPath ?? string.Empty;
		_preferencesError = string.Empty;
		_openPreferencesPopup = true;
	}

	private void DrawPreferencesPopup()
	{
		if (_openPreferencesPopup) { ImGui.OpenPopup("Wolfie Preferences"); _openPreferencesPopup = false; }
		var isOpen = true;
		ImGui.SetNextWindowSize(new Vector2(620, 0), ImGuiCond.Appearing);
		if (!ImGui.BeginPopupModal("Wolfie Preferences", ref isOpen, ImGuiWindowFlags.AlwaysAutoResize)) return;
		ImGui.TextUnformatted("External tools");
		ImGui.Separator();
		ImGui.TextUnformatted("Blender");
		ImGui.TextDisabled("Path to the Blender executable or application");
		ImGui.SetNextItemWidth(500);
		ImGui.InputText("##BlenderPath", ref _blenderPath, 2048);
		ImGui.SameLine();
		if (ImGui.Button("Browse..."))
		{
			var isApplicationBundle = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) &&
				string.Equals(Path.GetExtension(_blenderPath), ".app", StringComparison.OrdinalIgnoreCase);
			var initial = string.IsNullOrWhiteSpace(_blenderPath) ? null :
				(Directory.Exists(_blenderPath) && !isApplicationBundle ? _blenderPath : Path.GetDirectoryName(_blenderPath));
			var selected = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
				? fileDialog.OpenFile(new FileDialogOptions
					{ Title = "Select Blender Application", InitialDirectory = initial, AllowedExtensions = ["app"] })
				: fileDialog.OpenFile(new FileDialogOptions { Title = "Select Blender Executable", InitialDirectory = initial });
			if (selected is not null) _blenderPath = selected;
		}
		if (!string.IsNullOrWhiteSpace(_preferencesError))
		{
			ImGui.Spacing();
			ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1, .35f, .3f, 1));
			ImGui.TextWrapped(_preferencesError);
			ImGui.PopStyleColor();
		}
		ImGui.Spacing();
		if (ImGui.Button("Save", new Vector2(100, 0)))
		{
			try { preferences.SetBlenderPath(_blenderPath); ImGui.CloseCurrentPopup(); }
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
			{ _preferencesError = exception.Message; }
		}
		ImGui.SameLine();
		if (ImGui.Button("Cancel", new Vector2(100, 0))) ImGui.CloseCurrentPopup();
		ImGui.EndPopup();
	}

	private void DrawCard(UnityAssetEntry entry)
	{
		ImGui.PushID(entry.RelativePath);
		ImGui.BeginChild("AssetCard", new Vector2(0, CardHeight), ImGuiChildFlags.None,
			ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
		ImGui.InvisibleButton("CardButton", new Vector2(ImGui.GetContentRegionAvail().X, CardHeight - 6));
		var clicked = ImGui.IsItemClicked();
		var doubleClicked = clicked && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);
		if (clicked) _selectedPath = entry.RelativePath;
		if (doubleClicked && entry.Type == UnityAssetEntryType.Folder) _selectedFolderPath = entry.RelativePath;
		if (doubleClicked && entry.Type == UnityAssetEntryType.File && entry.ManagedAssetId.HasValue &&
		    string.Equals(entry.Extension, ".blend", StringComparison.OrdinalIgnoreCase)) OpenInBlender(entry);
		var min = ImGui.GetItemRectMin(); var max = ImGui.GetItemRectMax(); var draw = ImGui.GetWindowDrawList();
		if (string.Equals(_selectedPath, entry.RelativePath, StringComparison.Ordinal))
			draw.AddRectFilled(min, max, ImGui.GetColorU32(ImGuiCol.HeaderActive), 4);
		else if (ImGui.IsItemHovered()) draw.AddRectFilled(min, max, ImGui.GetColorU32(ImGuiCol.HeaderHovered), 4);
		var iconSize = new Vector2(42, 34); var iconMin = new Vector2(min.X + ((max.X - min.X) - iconSize.X) * .5f, min.Y + 12); var iconMax = iconMin + iconSize;
		var color = ImGui.GetColorU32(ImGuiCol.TextDisabled);
		var nameColor = entry.IsManaged ? ImGui.GetColorU32(ImGuiCol.Text) : color;
		var iconName = entry.Type == UnityAssetEntryType.Folder ? "folder" : "object";
		if (icons.TryGet(iconName, out var textureId)) draw.AddImage(textureId, iconMin, iconMax);
		else draw.AddRect(iconMin, iconMax, color, 2);
		var nameSize = ImGui.CalcTextSize(entry.Name); var nameX = min.X + MathF.Max(4, ((max.X - min.X) - nameSize.X) * .5f);
		draw.AddText(new Vector2(nameX, iconMax.Y + 10), nameColor, entry.Name);
		if (entry.IsManaged && entry.Type == UnityAssetEntryType.File &&
		    entry.RelativePath.StartsWith("Assets/", StringComparison.Ordinal))
			draw.AddText(new Vector2(min.X + 5, min.Y + 4), ImGui.GetColorU32(ImGuiCol.Text), "Managed");
		if (ImGui.BeginPopupContextItem("AssetActions"))
		{
			if (entry.ManagedAssetId.HasValue && string.Equals(entry.Extension, ".blend", StringComparison.OrdinalIgnoreCase))
			{
				var canPublish = _publishTask is null;
				if (ImGui.MenuItem("Publish Model", string.Empty, false, canPublish)) PublishModel(entry);
				ImGui.Separator();
			}
			if (entry.Type == UnityAssetEntryType.File &&
			    entry.RelativePath.StartsWith("Assets/", StringComparison.Ordinal) && IsTexture(entry.Extension))
			{
				if (!entry.IsManaged && ImGui.MenuItem("Manage")) Manage(entry);
				if (entry.IsManaged && ImGui.MenuItem("Unmanage")) Unmanage(entry);
			}
			ImGui.EndPopup();
		}
		ImGui.EndChild(); ImGui.PopID();
	}

	private static bool IsTexture(string extension) => extension.ToLowerInvariant() is
		".png" or ".jpg" or ".jpeg" or ".tga" or ".tif" or ".tiff" or ".psd" or ".exr" or ".hdr";

	private void OpenInBlender(UnityAssetEntry entry)
	{
		try
		{
			blenderLauncher.Open(_projectFile!, entry.RelativePath, preferences.BlenderPath);
			_error = string.Empty;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or
			ArgumentException or InvalidOperationException)
		{ _error = $"Could not open the asset in Blender: {exception.Message}"; }
	}

	private void PublishModel(UnityAssetEntry entry)
	{
		if (_publishTask is not null) return;
		_publishingPath = entry.RelativePath;
		_error = string.Empty;
		try
		{
			_publishTask = modelPublisher.PublishAsync(_project!, _projectFile!, entry.RelativePath,
				preferences.BlenderPath);
		}
		catch (Exception exception)
		{
			_publishingPath = null;
			_error = $"Could not publish the model: {exception.Message}";
		}
	}

	private void CompletePublishIfReady()
	{
		if (_publishTask is null || !_publishTask.IsCompleted) return;
		try
		{
			_publishTask.GetAwaiter().GetResult();
			Refresh();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
			System.ComponentModel.Win32Exception or ArgumentException or InvalidOperationException or TimeoutException)
		{ _error = $"Could not publish the model: {exception.Message}"; }
		finally { _publishTask = null; _publishingPath = null; }
	}

	private void DrainAutoPublishNotifications()
	{
		var refresh = false;
		while (autoPublisher.TryDequeue(out var notification))
		{
			if (notification.Succeeded) refresh = true;
			else _error = $"Could not automatically publish {Path.GetFileName(notification.RelativeSourcePath)}: {notification.Error}";
		}
		if (refresh) Refresh();
	}

	private void Manage(UnityAssetEntry entry)
	{
		try
		{
			managedAssets.ManageTexture(_project!, _projectFile!, entry.RelativePath);
			Refresh();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or InvalidOperationException)
		{ _error = $"Could not manage the asset: {exception.Message}"; }
	}

	private void Unmanage(UnityAssetEntry entry)
	{
		try
		{
			managedAssets.Unmanage(_projectFile!, entry.RelativePath);
			Refresh();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or InvalidOperationException)
		{ _error = $"Could not unmanage the asset: {exception.Message}"; }
	}

	private bool DrawIconButton(string iconName, string tooltip)
	{
		if (!icons.TryGet(iconName, out var textureId)) return ImGui.Button(tooltip);
		var clicked = ImGui.ImageButton("##" + tooltip, textureId, new Vector2(18, 18));
		if (ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
		return clicked;
	}

	private static UnityAssetEntry? FindEntry(UnityAssetEntry entry, string path)
	{
		if (string.Equals(entry.RelativePath, path, StringComparison.Ordinal)) return entry;
		foreach (var child in entry.Children)
			if (child.Type == UnityAssetEntryType.Folder && FindEntry(child, path) is { } found) return found;
		return null;
	}

	private static void DrawVerticalSeparator()
	{
		var draw = ImGui.GetWindowDrawList(); var min = ImGui.GetCursorScreenPos();
		draw.AddLine(min, new Vector2(min.X, min.Y + ImGui.GetContentRegionAvail().Y), ImGui.GetColorU32(ImGuiCol.Separator), 2);
		ImGui.Dummy(new Vector2(2, ImGui.GetContentRegionAvail().Y));
	}

	private void CreateProject()
	{
		try
		{
			_project = projectService.Create(_unityPath, _parentLocation, _projectName, out var projectFile);
			_projectFile = projectFile;
			preferences.SetLastProjectPath(projectFile);
			_error = string.Empty;
			Refresh();
			autoPublisher.Start(_project!, _projectFile!);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
		{
			_error = exception.Message;
		}
	}

	private void OpenProject()
	{
		var selected = fileDialog.OpenFile(new FileDialogOptions { Title = "Open Wolfie Project", AllowedExtensions = ["wolfieproject"] });
		if (selected is null) return;
		try
		{
			OpenProject(selected, persistPreference: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
		{
			_error = $"Could not open the Wolfie project: {exception.Message}";
		}
	}

	private void RestoreLastProject()
	{
		if (string.IsNullOrWhiteSpace(preferences.LastProjectPath)) return;
		try
		{
			OpenProject(preferences.LastProjectPath, persistPreference: false);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
		{
			_error = $"The previous Wolfie project could not be reopened: {exception.Message}";
		}
	}

	private void OpenProject(string projectFile, bool persistPreference)
	{
		_project = projectService.Open(projectFile);
		_projectFile = WolfiePath.NormalizeAbsolute(projectFile);
		if (persistPreference) preferences.SetLastProjectPath(_projectFile);
		_error = string.Empty;
		Refresh();
		autoPublisher.Start(_project!, _projectFile!);
	}

	private void Refresh()
	{
		if (_project is null) return;
		try { _assets = assetScanner.Scan(_project, _projectFile!, managedAssets); _error = string.Empty; }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
		{ _assets = null; _error = $"Could not scan Unity assets: {exception.Message}"; }
	}

	private void CloseProject()
	{
		autoPublisher.Stop();
		_project = null; _projectFile = null; _assets = null; _selectedPath = string.Empty; _selectedFolderPath = "Assets"; _error = string.Empty;
	}

	private void DrawError()
	{
		if (string.IsNullOrWhiteSpace(_error)) return;
		ImGui.Spacing();
		ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1, 0.35f, 0.3f, 1));
		ImGui.TextWrapped(_error);
		ImGui.PopStyleColor();
	}

	private static void DrawDockSpace()
	{
		var viewport = ImGui.GetMainViewport();
		ImGui.SetNextWindowPos(viewport.WorkPos); ImGui.SetNextWindowSize(viewport.WorkSize); ImGui.SetNextWindowViewport(viewport.ID);
		ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0); ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0); ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
		const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse |
		                               ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoBringToFrontOnFocus |
		                               ImGuiWindowFlags.NoNavFocus | ImGuiWindowFlags.NoBackground;
		ImGui.Begin("WolfieDockSpace", flags); ImGui.PopStyleVar(3);
		ImGui.DockSpace(ImGui.GetID("WolfieMainDockSpace"), Vector2.Zero, ImGuiDockNodeFlags.PassthruCentralNode); ImGui.End();
	}
}

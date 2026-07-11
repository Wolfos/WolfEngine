using System.Numerics;
using ImGuiNET;
using WolfEngine.Utility;
using Wolfie.IAE.Projects;
using Wolfie.IAE.UnityAssets;

namespace Wolfie.IAE.UI;

public sealed class WolfieGui(
	WolfieProjectService projectService,
	UnityAssetScanner assetScanner,
	IFileDialogService fileDialog)
{
	private WolfieProject? _project;
	private string? _projectFile;
	private UnityAssetScanResult? _assets;
	private string _unityPath = string.Empty;
	private string _parentLocation = string.Empty;
	private string _projectName = string.Empty;
	private string _selectedPath = string.Empty;
	private string _error = string.Empty;

	public void Draw()
	{
		DrawDockSpace();
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
		ImGui.SetNextItemWidth(-1);
		ImGui.InputText("##WolfieName", ref _projectName, 256);
		ImGui.TextDisabled("Wolfie project name (a new folder with this name will be created)");
		if (!string.IsNullOrWhiteSpace(_parentLocation) && !string.IsNullOrWhiteSpace(_projectName))
			ImGui.TextDisabled($"Will create: {Path.Combine(_parentLocation, _projectName.Trim())}");
		if (ImGui.Button("Create Project", new Vector2(-1, 38))) CreateProject();
		DrawError();
		ImGui.End();
	}

	private void PathField(string label, ref string value, string dialogTitle)
	{
		ImGui.SetNextItemWidth(-88);
		ImGui.InputText("##" + label, ref value, 2048);
		ImGui.SameLine();
		if (ImGui.Button("Browse##" + label, new Vector2(80, 0)))
		{
			var selected = fileDialog.OpenFolder(new FileDialogOptions { Title = dialogTitle, InitialDirectory = value });
			if (selected is not null) value = selected;
		}
		ImGui.TextDisabled(label);
	}

	private void DrawWorkspace()
	{
		if (ImGui.BeginMainMenuBar())
		{
			if (ImGui.BeginMenu("Project"))
			{
				if (ImGui.MenuItem("Open...")) OpenProject();
				if (ImGui.MenuItem("Close")) CloseProject();
				ImGui.EndMenu();
			}
			ImGui.EndMainMenuBar();
		}

		ImGui.Begin("Unity Assets");
		ImGui.Text($"Wolfie: {_project!.Name}");
		ImGui.TextDisabled(_projectFile ?? string.Empty);
		ImGui.Text($"Connected Unity project: {Path.GetFileName(_project.UnityProjectPath)}");
		ImGui.TextDisabled(_project.UnityProjectPath);
		if (ImGui.Button("Refresh")) Refresh();
		ImGui.SameLine();
		ImGui.TextDisabled("Unity-only content (read-only)");
		ImGui.Separator();
		if (_assets is not null) DrawEntry(_assets.Root);
		if (_assets is { Warnings.Count: > 0 })
			ImGui.TextDisabled($"Scan completed with {_assets.Warnings.Count} inaccessible item(s).");
		ImGui.Separator();
		ImGui.Text("Selected path:");
		ImGui.SameLine();
		ImGui.TextDisabled(string.IsNullOrEmpty(_selectedPath) ? "None" : _selectedPath);
		DrawError();
		ImGui.End();
	}

	private void DrawEntry(UnityAssetEntry entry)
	{
		var selected = string.Equals(_selectedPath, entry.RelativePath, StringComparison.Ordinal);
		var flags = ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.OpenOnArrow |
		            (selected ? ImGuiTreeNodeFlags.Selected : 0);
		if (entry.Type == UnityAssetEntryType.File) flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;
		if (entry.RelativePath == "Assets") flags |= ImGuiTreeNodeFlags.DefaultOpen;
		ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.62f, 0.65f, 0.69f, 1));
		var open = ImGui.TreeNodeEx(entry.RelativePath + "##unity", flags, entry.Name);
		ImGui.PopStyleColor();
		if (ImGui.IsItemClicked()) _selectedPath = entry.RelativePath;
		if (open && entry.Type == UnityAssetEntryType.Folder)
		{
			foreach (var child in entry.Children) DrawEntry(child);
			ImGui.TreePop();
		}
	}

	private void CreateProject()
	{
		try
		{
			_project = projectService.Create(_unityPath, _parentLocation, _projectName, out var projectFile);
			_projectFile = projectFile;
			_error = string.Empty;
			Refresh();
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
			_project = projectService.Open(selected);
			_projectFile = WolfiePath.NormalizeAbsolute(selected);
			_error = string.Empty;
			Refresh();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
		{
			_error = $"Could not open the Wolfie project: {exception.Message}";
		}
	}

	private void Refresh()
	{
		if (_project is null) return;
		try { _assets = assetScanner.Scan(_project.UnityProjectPath); _error = string.Empty; }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
		{ _assets = null; _error = $"Could not scan Unity assets: {exception.Message}"; }
	}

	private void CloseProject()
	{
		_project = null; _projectFile = null; _assets = null; _selectedPath = string.Empty; _error = string.Empty;
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

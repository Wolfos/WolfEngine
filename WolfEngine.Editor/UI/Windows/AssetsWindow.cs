using System.Numerics;
using ImGuiNET;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;
using WolfEngine.Utility;

namespace WolfEngine.Editor.UI;

public sealed partial class AssetsWindow : EditorWindow, IEditorAssetDeletionHandler
{
	private const float FolderTreeIconSize = 15.5f;
	private const float PaneSeparatorThickness = 2.0f;
	private const float DragPreviewRounding = 4.0f;
	private const string ErrorPopupId = "AssetsWindowError";
	private const string DeletePopupId = "AssetsWindowDelete";
	private const string RenamePopupId = "AssetsWindowRename";

	private readonly IEditorProjectService _projectService;
	private readonly IProjectAssetPipelineService _assetPipelineService;
	private readonly IAssetThumbnailLoader _assetThumbnailLoader;
	private readonly IAssetSelectionService _assetSelectionService;
	private readonly IEditorAssetHandlerRegistry _assetHandlerRegistry;
	private readonly IIconManager _icons;
	private readonly IEditorInteractionState _interactionState;
	private readonly IEditorCommandService _commandService;
	private readonly IFileManagerService _fileManagerService;
	private readonly EditorCameraSystem? _editorCameraSystem;
	private readonly AssetsWindowDragDropState _dragDrop = new(DragPreviewRounding);
	private readonly AssetsWindowSelectionState _selection = new();
	private readonly AssetsWindowBrowserModelCache _browserModelCache = new();
	private string _errorMessage = string.Empty;
	private bool _openErrorPopup;
	private PendingDeleteTarget? _pendingDeleteTarget;
	private bool _openDeletePopup;
	private PendingRenameTarget? _pendingRenameTarget;
	private PendingRenameTarget? _scheduledRenameTarget;
	private int _scheduledRenameDelayFrames;
	private bool _openRenamePopup;
	private string _renameName = string.Empty;
	private string _renameErrorMessage = string.Empty;
	private string _assetSearchText = string.Empty;

	public AssetsWindow(
		IEditorProjectService projectService,
		IProjectAssetPipelineService assetPipelineService,
		IAssetThumbnailLoader assetThumbnailLoader,
		IAssetSelectionService assetSelectionService,
		IEditorAssetHandlerRegistry assetHandlerRegistry,
		IIconManager icons,
		IEditorInteractionState interactionState,
		IEditorCommandService commandService,
		IFileManagerService fileManagerService,
		EditorCameraSystem? editorCameraSystem = null)
	{
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
		_assetPipelineService = assetPipelineService ?? throw new ArgumentNullException(nameof(assetPipelineService));
		_assetThumbnailLoader = assetThumbnailLoader ?? throw new ArgumentNullException(nameof(assetThumbnailLoader));
		_assetSelectionService =
			assetSelectionService ?? throw new ArgumentNullException(nameof(assetSelectionService));
		_assetHandlerRegistry = assetHandlerRegistry ?? throw new ArgumentNullException(nameof(assetHandlerRegistry));
		_icons = icons ?? throw new ArgumentNullException(nameof(icons));
		_interactionState = interactionState ?? throw new ArgumentNullException(nameof(interactionState));
		_commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));
		_fileManagerService = fileManagerService ?? throw new ArgumentNullException(nameof(fileManagerService));
		_editorCameraSystem = editorCameraSystem;
	}

	internal string? PendingDeleteKindForTesting => _pendingDeleteTarget?.Kind.ToString();
	internal string? PendingDeleteRelativePathForTesting => _pendingDeleteTarget?.RelativePath;
	internal string? PendingRenameKindForTesting => _pendingRenameTarget?.Kind.ToString();
	internal string? PendingRenameRelativePathForTesting => _pendingRenameTarget?.RelativePath;

	internal void SetSelectedFolderForTesting(string relativeFolderPath)
	{
		_selection.SetSelectedFolderPath(relativeFolderPath);
	}

	internal string? MoveDragTargetForTesting(string kind, string relativePath, string targetFolderPath)
	{
		var dragTarget = string.Equals(kind, DragTargetKind.Source.ToString(), StringComparison.Ordinal)
			? AssetBrowserDragTarget.ForSource(relativePath)
			: AssetBrowserDragTarget.ForFolder(relativePath);
		return AssetsWindowDragDropState.MoveDragTarget(_projectService, dragTarget, targetFolderPath);
	}

	internal void HandleCreationResultForTesting(EditorAssetCreationResult result)
	{
		HandleCreationResult(result);
	}

	internal void ProcessDeferredRenameForTesting()
	{
		ProcessScheduledRename();
	}

	internal void OpenFolderInFileManagerForTesting(string folderPath)
	{
		OpenFolderInFileManager(folderPath);
	}

	internal void RevealSourceInFileManagerForTesting(string relativeSourcePath)
	{
		RevealSourceInFileManager(relativeSourcePath);
	}

	public override string Name => "Assets";

	public override void Draw(EditorScene scene)
	{
		ImGui.SetNextWindowPos(new Vector2(0.0f, 520.0f), ImGuiCond.FirstUseEver);
		ImGui.SetNextWindowSize(new Vector2(640.0f, 300.0f), ImGuiCond.FirstUseEver);
		Begin();
		if (ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
		{
			_interactionState.SetFocusedWindow(EditorFocusedWindow.Assets);
			HandleRenameShortcut();
		}

		if (_projectService.HasOpenProject == false || string.IsNullOrWhiteSpace(_projectService.AssetsPath))
		{
			ImGui.BeginDisabled();
			ImGui.TextUnformatted("No project open.");
			ImGui.EndDisabled();
			DrawDeletePopup();
			DrawRenamePopup();
			DrawErrorPopup();
			ImGui.End();
			return;
		}

		var assetCatalog = _projectService.CurrentAssetCatalog;
		if (assetCatalog is null || assetCatalog.Mounts.Count == 0)
		{
			assetCatalog = new AssetCatalog([
				new DirectoryAssetMount("project", "Project", _projectService.ProjectRootPath!, false,
					_projectService.CurrentAssetDatabase)
			]);
		}
		var browserModel = _browserModelCache.GetOrBuild(
			assetCatalog,
			_projectService.AssetsPath,
			_projectService.AssetDatabaseRevision);
		_lastBrowserModel = browserModel;
		_browserItems.Clear();
		_browserControls.Clear();
		_folderTreeWidth = Math.Clamp(_folderTreeWidth, 160.0f, MathF.Max(160.0f, ImGui.GetContentRegionAvail().X * 0.45f));
		_selection.Prune(browserModel, _projectService, _assetSelectionService);
		var selectedFolder = browserModel.FoldersByPath[_selection.SelectedFolderPath];

		_dragDrop.BeginFrame();
		DrawFolderTree(browserModel);
		ImGui.SameLine(0.0f, 0.0f);
		DrawFolderTreeSplitter();
		ImGui.SameLine(0.0f, 0.0f);
		DrawContentArea(browserModel, selectedFolder, scene);
		_dragDrop.Complete(_projectService, CompleteSuccessfulMove, ShowError);
		DrawBrowserContextMenu(scene);
		ProcessScheduledRename();
		DrawDeletePopup();
		DrawRenamePopup();
		DrawErrorPopup();
		ImGui.End();
	}

	private void DrawFolderTree(AssetsWindowBrowserModel browserModel)
	{
		AssetsWindowDrawing.PushPaneStyle();
		ImGui.PushStyleVar(ImGuiStyleVar.IndentSpacing, 14.0f);
		ImGui.BeginChild("AssetsFolderTree", new Vector2(_folderTreeWidth, 0.0f), ImGuiChildFlags.None);
		for (var i = 0; i < browserModel.RootFolders.Count; i++)
		{
			DrawFolderTreeNode(browserModel.RootFolders[i]);
		}

		HandleFolderBackgroundContextMenu();

		ImGui.EndChild();
		ImGui.PopStyleVar();
		ImGui.PopStyleColor(2);
	}

	private void DrawFolderTreeNode(AssetsWindowFolderNode folder)
	{
		ImGui.PushID(folder.RelativePath);

		var isSelected = string.Equals(
			_selection.SelectedFolderPath,
			folder.RelativePath,
			StringComparison.OrdinalIgnoreCase);
		var containsRevealTarget = _selection.FolderTreeRevealPath is not null &&
		                           ProjectPathUtility.IsSameOrDescendant(
			                           _selection.FolderTreeRevealPath,
			                           folder.RelativePath);
		var hasChildren = folder.Children.Count > 0;
		var flags = ImGuiTreeNodeFlags.SpanFullWidth | ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.FramePadding;
		if (hasChildren == false)
		{
			flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;
		}

		if (isSelected)
		{
			flags |= ImGuiTreeNodeFlags.Selected;
			PushSelectedHeaderColors();
		}

		if (containsRevealTarget && hasChildren)
		{
			ImGui.SetNextItemOpen(true, ImGuiCond.Always);
		}

		var nodeCursorX = ImGui.GetCursorScreenPos().X;
		var open = ImGui.TreeNodeEx("##FolderNode", flags);
		var leftClicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
		var rightClicked = ImGui.IsItemClicked(ImGuiMouseButton.Right);
		RecordBrowserControl("tree:" + folder.RelativePath);
		if (isSelected && _selection.FolderTreeRevealPath is not null)
		{
			ImGui.SetScrollHereY(0.5f);
			if (ImGui.IsItemVisible())
			{
				_selection.ClearFolderRevealPath();
			}
		}
		if (!IsReadOnlyBrowserFolder(folder.RelativePath))
			_dragDrop.RegisterFolderDropTarget(folder.RelativePath);
		DrawFolderTreeLabel(folder, nodeCursorX, open && hasChildren);
		if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal))
		{
			ImGui.SetTooltip(folder.RelativePath);
		}

		if (leftClicked)
		{
			_interactionState.SetFocusedWindow(EditorFocusedWindow.Assets);
			SelectFolder(folder.RelativePath);
		}

		if (rightClicked)
		{
			_interactionState.SetFocusedWindow(EditorFocusedWindow.Assets);
			RequestBrowserContextMenu(BrowserContextTarget.ForFolder(folder.RelativePath));
		}


		if (hasChildren && open)
		{
			for (var i = 0; i < folder.Children.Count; i++)
			{
				DrawFolderTreeNode(folder.Children[i]);
			}

			ImGui.TreePop();
		}

		if (isSelected)
		{
			ImGui.PopStyleColor(3);
		}

		ImGui.PopID();
	}

	private void DrawFolderTreeLabel(AssetsWindowFolderNode folder, float nodeCursorX, bool open)
	{
		var itemMin = ImGui.GetItemRectMin();
		var itemMax = ImGui.GetItemRectMax();
		var rowHeight = itemMax.Y - itemMin.Y;
		var labelStartX = nodeCursorX + ImGui.GetTreeNodeToLabelSpacing();
		var iconSize = MathF.Min(FolderTreeIconSize, MathF.Max(1.0f, rowHeight - 2.0f));
		var iconPosition = new Vector2(labelStartX, itemMin.Y + (rowHeight - iconSize) * 0.5f);
		var textSize = ImGui.CalcTextSize(folder.Name);
		var textPosition = new Vector2(iconPosition.X + iconSize + 4.0f, itemMin.Y + (rowHeight - textSize.Y) * 0.5f);
		var drawList = ImGui.GetWindowDrawList();

		if (_icons.TryGet(open ? "asset_folder_open" : "folder", out var textureId))
		{
			drawList.AddImage(
				textureId,
				iconPosition,
				iconPosition + Vector2.One * iconSize,
				Vector2.Zero,
				Vector2.One,
				ImGui.GetColorU32(AssetBrowserAppearance.FolderColor));
		}
		else
		{
			drawList.AddRect(iconPosition, iconPosition + Vector2.One * iconSize, ImGui.GetColorU32(ImGuiCol.Border));
		}

		drawList.AddText(textPosition, ImGui.GetColorU32(ImGuiCol.Text), folder.Name);
		if (_dragDrop.IsActiveDropTarget(folder.RelativePath))
		{
			drawList.AddRect(itemMin, itemMax, ImGui.GetColorU32(ImGuiCol.HeaderActive), 2.0f, ImDrawFlags.None, 2.0f);
		}
	}

	private void CompleteSuccessfulMove(AssetBrowserDragTarget dragTarget, string movedPath)
	{
		_browserModelCache.Invalidate();

		switch (dragTarget.Kind)
		{
			case DragTargetKind.Source:
				if (_assetSelectionService.SelectedAssetId is { } selectedAssetId &&
				    _projectService.TryGetAsset(selectedAssetId, out var selectedAsset))
				{
					SelectAsset(selectedAsset, requestFocus: false);
				}
				else
				{
					_selection.SetSelectedFolderPath(ProjectPathUtility.GetFolderPath(movedPath));
				}

				break;
			case DragTargetKind.Folder:
				_selection.UpdateSelectedFolderAfterRelocation(dragTarget.RelativePath, movedPath);
				_selection.RevealFolderPath(movedPath);
				break;
		}

		_selection.ExpandedSourceId = null;
		ValidateSelectionAfterProjectMutation();
	}

	private void DrawFolderScopedContextMenu(string folderPath)
	{
		if (IsReadOnlyBrowserFolder(folderPath))
		{
			ImGui.TextDisabled("Read-only mounted content");
			return;
		}
		if (ImGui.BeginMenu("Create"))
		{
			if (ImGui.MenuItem("Folder"))
			{
				CreateFolder(folderPath);
			}

			ImGui.Separator();
			DrawCreateMenuItems(_assetHandlerRegistry.GetCreateMenuItems(), folderPath);
			ImGui.EndMenu();
		}

		var canDelete =
			string.Equals(folderPath, AssetPipelinePaths.AssetsFolderName, StringComparison.OrdinalIgnoreCase) == false;
		if (canDelete)
		{
			if (ImGui.MenuItem("Rename"))
			{
				RequestRename(PendingRenameTarget.ForFolder(folderPath));
			}

			RecordBrowserControl("context:rename");
		}

		if (canDelete && ImGui.MenuItem("Delete Folder"))
		{
			RequestDelete(PendingDeleteTarget.ForFolder(folderPath));
		}

		ImGui.Separator();
		if (ImGui.MenuItem($"Show in {_fileManagerService.FileManagerName}"))
		{
			OpenFolderInFileManager(folderPath);
		}
	}

	// Only the project's Assets root maps to editable project files; every mounted root is browse-only.
	private static bool IsReadOnlyBrowserFolder(string folderPath) =>
		AssetsWindowBrowserPaths.IsProjectPath(folderPath) == false;

	private void OpenFolderInFileManager(string folderPath)
	{
		try
		{
			_fileManagerService.OpenFolder(_projectService.GetAbsolutePath(folderPath));
		}
		catch (Exception ex)
		{
			ShowError($"Failed to open folder in {_fileManagerService.FileManagerName}: {ex.Message}");
		}
	}

	private void RevealSourceInFileManager(string relativeSourcePath)
	{
		try
		{
			_fileManagerService.RevealPath(_projectService.GetAbsolutePath(relativeSourcePath));
		}
		catch (Exception ex)
		{
			ShowError($"Failed to show asset in {_fileManagerService.FileManagerName}: {ex.Message}");
		}
	}

	private void RevealSourceInFileManager(Guid assetId, string relativeSourcePath)
	{
		try
		{
			_fileManagerService.RevealPath(_projectService.GetAbsoluteAssetPath(assetId, relativeSourcePath));
		}
		catch (Exception ex)
		{
			ShowError($"Failed to show asset in {_fileManagerService.FileManagerName}: {ex.Message}");
		}
	}

	private void CreateFolder(string parentFolderPath)
	{
		try
		{
			var folderPath = _projectService.CreateFolder(parentFolderPath, GetNewFolderName(parentFolderPath));
			_browserModelCache.Invalidate();
			_selection.RevealFolderPath(folderPath);
			_selection.ExpandedSourceId = null;
			ScheduleRename(PendingRenameTarget.ForFolder(folderPath));
		}
		catch (Exception ex)
		{
			ShowError($"Failed to create folder: {ex.Message}");
		}
	}

	private string GetNewFolderName(string parentFolderPath)
	{
		const string baseName = "New Folder";
		var normalizedParentPath = ProjectPathUtility.NormalizeAssetsFolderPath(parentFolderPath);
		var candidateName = baseName;
		var index = 1;
		while (Directory.Exists(_projectService.GetAbsolutePath(
			       ProjectPathUtility.NormalizeRelativePath($"{normalizedParentPath}/{candidateName}"))) ||
		       File.Exists(_projectService.GetAbsolutePath(
			       ProjectPathUtility.NormalizeRelativePath($"{normalizedParentPath}/{candidateName}"))))
		{
			candidateName = $"{baseName} {index}";
			index++;
		}

		return candidateName;
	}

	private void DrawSourceContextMenu(EditorScene scene, AssetsWindowSourceItem sourceItem,
		BrowserContextTarget contextTarget, string renameLabel, string deleteLabel)
	{
		var asset = ResolveTargetAsset(contextTarget);
		if (asset is not null && asset.Type == AssetType.Model3D && ImGui.MenuItem("Add to Scene"))
		{
			try
			{
				_assetPipelineService.InstantiateImportedModel(
					_projectService.CurrentAssetCatalog, asset.Id, scene.World, GetSceneSpawnPosition());
				_interactionState.MarkSceneDirty(scene.World);
			}
			catch (Exception ex)
			{
				ShowError($"Failed to add model to scene: {ex.Message}");
			}
		}

		if (asset is not null && asset.Type == AssetType.Prefab && ImGui.MenuItem("Add to Scene"))
		{
			try
			{
				_assetPipelineService.InstantiatePrefab(
					_projectService.CurrentAssetCatalog, asset.Id, scene, GetSceneSpawnPosition());
				_interactionState.MarkSceneDirty(scene.World);
			}
			catch (Exception ex)
			{
				ShowError($"Failed to add prefab to scene: {ex.Message}");
			}
		}

		if (sourceItem.SubAssets.Count > 0 && contextTarget.Kind == BrowserContextKind.Source)
		{
			var isExpanded = _selection.ExpandedSourceId == sourceItem.SourceId;
			if (ImGui.MenuItem(isExpanded ? "Hide Sub-assets" : "Show Sub-assets"))
			{
				_selection.ExpandedSourceId =
					AssetsWindowBrowserModelBuilder.ToggleExpandedSource(_selection.ExpandedSourceId,
						sourceItem.SourceId);
			}
		}

		if (sourceItem.IsReadOnly) ImGui.BeginDisabled();
		if (ImGui.MenuItem(renameLabel))
		{
			RequestRename(PendingRenameTarget.ForSource(contextTarget.RelativeSourcePath!));
		}

		RecordBrowserControl("context:rename");

		if (ImGui.MenuItem(deleteLabel))
		{
			RequestDelete(PendingDeleteTarget.ForSource(contextTarget.RelativeSourcePath!));
		}
		if (sourceItem.IsReadOnly) ImGui.EndDisabled();

		ImGui.Separator();
		if (ImGui.MenuItem($"Show in {_fileManagerService.FileManagerName}"))
		{
			RevealSourceInFileManager(contextTarget.AssetId!.Value, contextTarget.RelativeSourcePath!);
		}
	}

	private Vector3? GetSceneSpawnPosition()
	{
		if (_editorCameraSystem is null ||
		    _editorCameraSystem.TryGetCameraPose(out var position, out var forward) == false)
		{
			return null;
		}

		return position + Vector3.Normalize(forward) * 5.0f;
	}

	private void DrawCreateMenuItems(IReadOnlyList<EditorAssetCreateMenuItem> items, string targetFolderPath)
	{
		for (var i = 0; i < items.Count; i++)
		{
			var item = items[i];
			if (item.Children.Count > 0)
			{
				if (ImGui.BeginMenu(item.Label))
				{
					DrawCreateMenuItems(item.Children, targetFolderPath);
					ImGui.EndMenu();
				}

				continue;
			}

			if (ImGui.MenuItem(item.Label) && item.CreateAction is not null)
			{
				HandleCreationResult(item.CreateAction(targetFolderPath));
			}
		}
	}

	private void HandleCreationResult(EditorAssetCreationResult result)
	{
		if (result.Success && result.AssetId.HasValue)
		{
			_browserModelCache.Invalidate();
			if (_projectService.TryGetAsset(result.AssetId.Value, out var createdAsset))
			{
				_selection.SetSelectedFolderPath(ProjectPathUtility.GetFolderPath(createdAsset.RelativeSourcePath));
				ScheduleRename(PendingRenameTarget.ForSource(createdAsset.RelativeSourcePath));
			}

			_selection.ExpandedSourceId = null;
			_assetSelectionService.Select(result.AssetId.Value);
		}
		else if (string.IsNullOrWhiteSpace(result.ErrorMessage) == false)
		{
			ShowError(result.ErrorMessage);
		}
	}

	private void DrawDeletePopup()
	{
		if (_openDeletePopup)
		{
			ImGui.OpenPopup(DeletePopupId);
			_openDeletePopup = false;
		}

		var isOpen = true;
		ImGui.SetNextWindowSize(new Vector2(420.0f, 0.0f), ImGuiCond.Appearing);
		if (ImGui.BeginPopupModal(DeletePopupId, ref isOpen, ImGuiWindowFlags.AlwaysAutoResize) == false)
		{
			return;
		}

		if (_pendingDeleteTarget is not null)
		{
			ImGui.TextWrapped(_pendingDeleteTarget.ConfirmationText);
			ImGui.Spacing();
			if (ImGui.Button("Delete", new Vector2(100.0f, 0.0f)))
			{
				ExecutePendingDelete();
				ImGui.CloseCurrentPopup();
			}

			ImGui.SameLine();
			if (ImGui.Button("Cancel", new Vector2(100.0f, 0.0f)))
			{
				_pendingDeleteTarget = null;
				ImGui.CloseCurrentPopup();
			}
		}

		ImGui.EndPopup();
	}

	private void DrawRenamePopup()
	{
		if (_openRenamePopup)
		{
			ImGui.OpenPopup(RenamePopupId);
			_openRenamePopup = false;
		}

		var isOpen = true;
		ImGui.SetNextWindowSize(new Vector2(420.0f, 0.0f), ImGuiCond.Appearing);
		if (ImGui.BeginPopupModal(RenamePopupId, ref isOpen, ImGuiWindowFlags.AlwaysAutoResize) == false)
		{
			return;
		}

		if (_pendingRenameTarget is not null)
		{
			ImGui.TextUnformatted(_pendingRenameTarget.Title);
			ImGui.Spacing();
			ImGui.SetNextItemWidth(280.0f);
			if (_pendingRenameTarget.FocusInput)
			{
				ImGui.SetKeyboardFocusHere();
				_pendingRenameTarget = _pendingRenameTarget with { FocusInput = false };
			}

			var submitted = ImGui.InputText(
				"##RenameName",
				ref _renameName,
				256,
				ImGuiInputTextFlags.EnterReturnsTrue);
			if (string.IsNullOrEmpty(_pendingRenameTarget.Suffix) == false)
			{
				ImGui.SameLine();
				ImGui.TextDisabled(_pendingRenameTarget.Suffix);
			}

			if (string.IsNullOrWhiteSpace(_renameErrorMessage) == false)
			{
				ImGui.Spacing();
				ImGui.TextWrapped(_renameErrorMessage);
			}

			ImGui.Spacing();
			if (submitted || ImGui.Button("Rename", new Vector2(100.0f, 0.0f)))
			{
				if (ExecutePendingRename())
				{
					ImGui.CloseCurrentPopup();
				}
			}

			ImGui.SameLine();
			if (ImGui.Button("Cancel", new Vector2(100.0f, 0.0f)) || ImGui.IsKeyPressed(ImGuiKey.Escape))
			{
				_pendingRenameTarget = null;
				_renameErrorMessage = string.Empty;
				ImGui.CloseCurrentPopup();
			}
		}

		ImGui.EndPopup();
	}

	private void DrawErrorPopup()
	{
		if (_openErrorPopup)
		{
			ImGui.OpenPopup(ErrorPopupId);
			_openErrorPopup = false;
		}

		var isOpen = true;
		ImGui.SetNextWindowSize(new Vector2(420.0f, 0.0f), ImGuiCond.Appearing);
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

	private void ExecutePendingDelete()
	{
		if (_pendingDeleteTarget is null)
		{
			return;
		}

		try
		{
			switch (_pendingDeleteTarget.Kind)
			{
				case DeleteTargetKind.Source:
					_projectService.DeleteAssetSource(_pendingDeleteTarget.RelativePath);
					break;
				case DeleteTargetKind.Folder:
					_projectService.DeleteFolder(_pendingDeleteTarget.RelativePath);
					break;
			}

			_browserModelCache.Invalidate();
			ValidateSelectionAfterProjectMutation();
		}
		catch (Exception ex)
		{
			ShowError($"Failed to delete '{_pendingDeleteTarget.DisplayName}': {ex.Message}");
		}
		finally
		{
			_pendingDeleteTarget = null;
		}
	}

	private bool ExecutePendingRename()
	{
		if (_pendingRenameTarget is null)
		{
			return true;
		}

		try
		{
			switch (_pendingRenameTarget.Kind)
			{
				case RenameTargetKind.Source:
					_projectService.RenameAssetSource(_pendingRenameTarget.RelativePath, _renameName);
					if (_assetSelectionService.SelectedAssetId is { } selectedAssetId &&
					    _projectService.TryGetAsset(selectedAssetId, out var selectedAsset))
					{
						SelectAsset(selectedAsset, requestFocus: false);
					}
					break;
				case RenameTargetKind.Folder:
					var newFolderPath = _projectService.RenameFolder(_pendingRenameTarget.RelativePath, _renameName);
					_selection.UpdateSelectedFolderAfterRelocation(_pendingRenameTarget.RelativePath, newFolderPath);
					break;
			}

			_browserModelCache.Invalidate();
			ValidateSelectionAfterProjectMutation();
			_pendingRenameTarget = null;
			_renameErrorMessage = string.Empty;
			return true;
		}
		catch (Exception ex)
		{
			_renameErrorMessage = ex.Message;
			return false;
		}
	}

	private void RequestDelete(PendingDeleteTarget deleteTarget)
	{
		_pendingDeleteTarget = deleteTarget;
		_openDeletePopup = true;
	}

	private void RequestRename(PendingRenameTarget renameTarget)
	{
		_pendingRenameTarget = renameTarget;
		_renameName = renameTarget.EditableName;
		_renameErrorMessage = string.Empty;
		_openRenamePopup = true;
	}

	private void ScheduleRename(PendingRenameTarget renameTarget)
	{
		_scheduledRenameTarget = renameTarget;
		_scheduledRenameDelayFrames = 1;
	}

	private void ProcessScheduledRename()
	{
		if (_scheduledRenameTarget is not { } renameTarget)
		{
			return;
		}

		if (_scheduledRenameDelayFrames > 0)
		{
			_scheduledRenameDelayFrames--;
			return;
		}

		_scheduledRenameTarget = null;
		RequestRename(renameTarget);
	}

	public bool RequestRenameSelectedItem()
	{
		if (_assetSelectionService.SelectedAssetId is { } selectedAssetId &&
		    _projectService.TryGetAsset(selectedAssetId, out var selectedAsset))
		{
			if (_projectService.IsAssetReadOnly(selectedAssetId)) return false;
			RequestRename(PendingRenameTarget.ForSource(selectedAsset.RelativeSourcePath));
			return true;
		}

		if (AssetsWindowBrowserPaths.IsRoot(_selection.SelectedFolderPath) ||
		    IsReadOnlyBrowserFolder(_selection.SelectedFolderPath))
		{
			return false;
		}

		RequestRename(PendingRenameTarget.ForFolder(_selection.SelectedFolderPath));
		return true;
	}

	private void HandleRenameShortcut()
	{
		if (ImGui.GetIO().WantTextInput || ImGui.IsKeyPressed(ImGuiKey.F2) == false)
		{
			return;
		}

		RequestRenameSelectedItem();
	}

	public bool RequestDeleteSelectedItem()
	{
		if (_assetSelectionService.SelectedAssetId is { } selectedAssetId &&
		    _projectService.TryGetAsset(selectedAssetId, out var selectedAsset))
		{
			if (_projectService.IsAssetReadOnly(selectedAssetId)) return false;
			RequestDelete(PendingDeleteTarget.ForSource(selectedAsset.RelativeSourcePath));
			return true;
		}

		if (AssetsWindowBrowserPaths.IsRoot(_selection.SelectedFolderPath) ||
		    IsReadOnlyBrowserFolder(_selection.SelectedFolderPath))
		{
			return false;
		}

		RequestDelete(PendingDeleteTarget.ForFolder(_selection.SelectedFolderPath));
		return true;
	}

	private void SelectFolder(string relativeFolderPath)
	{
		_selection.SetSelectedFolderPath(relativeFolderPath);
		_selection.ExpandedSourceId = null;
		_assetSelectionService.Clear();
	}

	private void SelectAsset(AssetDatabaseEntry asset, bool requestFocus = true, string? folderPath = null)
	{
		if (string.IsNullOrWhiteSpace(_assetSearchText))
		{
			_selection.SetSelectedFolderPath(folderPath ?? ProjectPathUtility.GetFolderPath(asset.RelativeSourcePath));
		}

		_selection.ObserveBrowserSelection(asset.Id);
		_assetSelectionService.Select(asset.Id, requestFocus);
	}

	private void OpenAsset(AssetDatabaseEntry asset, EditorScene scene)
	{
		if (asset.Type != AssetType.Scene)
		{
			return;
		}

		_commandService.RequestLoadScene(asset.Id);
	}

	private void ValidateSelectionAfterProjectMutation()
	{
		_selection.ValidateAfterProjectMutation(_projectService, _assetSelectionService);
	}

	private AssetDatabaseEntry? ResolveTargetAsset(BrowserContextTarget contextTarget)
	{
		return contextTarget.AssetId.HasValue && _projectService.TryGetAsset(contextTarget.AssetId.Value, out var asset)
			? asset
			: null;
	}

	private unsafe void PushSelectedHeaderColors()
	{
		var selectedColor = ImGui.GetStyleColorVec4(ImGuiCol.HeaderActive);
		ImGui.PushStyleColor(ImGuiCol.Header, *selectedColor);
		ImGui.PushStyleColor(ImGuiCol.HeaderHovered, *selectedColor);
		ImGui.PushStyleColor(ImGuiCol.HeaderActive, *selectedColor);
	}

	private void ShowError(string errorMessage)
	{
		_errorMessage = errorMessage;
		_openErrorPopup = true;
	}

}

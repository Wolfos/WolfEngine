using System.Numerics;
using ImGuiNET;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;

namespace WolfEngine.Editor.UI;

public sealed class AssetsWindow : EditorWindow, IEditorAssetDeletionHandler
{
	private static readonly Vector2 ThumbnailSize = new(42.0f, 42.0f);
	private const float FolderTreeWidth = 220.0f;
	private const float FolderTreeIconSize = 15.5f;
	private const float FolderCardHeight = 88.0f;
	private const float SourceCardHeaderHeight = 104.0f;
	private const float SourceCardToggleHeight = 26.0f;
	private const float SubAssetRowHeight = 24.0f;
	private const float GridMinItemWidth = 132.0f;
	private const float PaneSeparatorThickness = 2.0f;
	private const float SearchInputWidth = 220.0f;
	private const float SearchInputMinWidth = 96.0f;
	private const float DragPreviewRounding = 4.0f;
	private const string ErrorPopupId = "AssetsWindowError";
	private const string DeletePopupId = "AssetsWindowDelete";
	private const string RenamePopupId = "AssetsWindowRename";
	private const string CurrentFolderContextMenuId = "AssetsWindowCurrentFolderContextMenu";
	private const string LocalItemContextMenuId = "ItemContextMenu";

	private readonly IEditorProjectService _projectService;
	private readonly IProjectAssetPipelineService _assetPipelineService;
	private readonly IAssetThumbnailLoader _assetThumbnailLoader;
	private readonly IAssetSelectionService _assetSelectionService;
	private readonly IEditorAssetHandlerRegistry _assetHandlerRegistry;
	private readonly IIconManager _icons;
	private readonly IEditorInteractionState _interactionState;
	private readonly IEditorCommandService _commandService;
	private readonly AssetsWindowDragDropState _dragDrop = new(DragPreviewRounding);
	private readonly AssetsWindowSelectionState _selection = new();
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
		IEditorCommandService commandService)
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

		var browserModel =
			AssetsWindowBrowserModelBuilder.Build(_projectService.CurrentAssetDatabase.Assets,
				_projectService.AssetsPath);
		_selection.Prune(browserModel, _projectService, _assetSelectionService);
		var selectedFolder = browserModel.FoldersByPath[_selection.SelectedFolderPath];

		_dragDrop.BeginFrame();
		DrawFolderTree(browserModel);
		ImGui.SameLine(0.0f, 0.0f);
		AssetsWindowDrawing.DrawVerticalPaneSeparator(PaneSeparatorThickness);
		ImGui.SameLine(0.0f, 0.0f);
		DrawContentArea(selectedFolder, scene);
		_dragDrop.Complete(_projectService, CompleteSuccessfulMove, ShowError);
		ProcessScheduledRename();
		DrawDeletePopup();
		DrawRenamePopup();
		DrawErrorPopup();
		ImGui.End();
	}

	private void DrawFolderTree(AssetsWindowBrowserModel browserModel)
	{
		AssetsWindowDrawing.PushPaneStyle();
		ImGui.BeginChild("AssetsFolderTree", new Vector2(FolderTreeWidth, 0.0f), ImGuiChildFlags.None);
		DrawFolderTreeNode(browserModel.RootFolder);
		_selection.ClearFolderRevealPath();
		if (ImGui.BeginPopupContextWindow(CurrentFolderContextMenuId + "Tree",
			    ImGuiPopupFlags.MouseButtonRight | ImGuiPopupFlags.NoOpenOverItems))
		{
			DrawFolderScopedContextMenu(_selection.SelectedFolderPath);
			ImGui.EndPopup();
		}

		ImGui.EndChild();
		ImGui.PopStyleColor(2);
	}

	private void DrawContentArea(AssetsWindowFolderNode selectedFolder, EditorScene scene)
	{
		ImGui.BeginGroup();
		DrawBreadcrumbRow(selectedFolder.RelativePath);
		AssetsWindowDrawing.DrawHorizontalPaneSeparator(PaneSeparatorThickness);

		AssetsWindowDrawing.PushPaneStyle();
		ImGui.BeginChild("AssetsContentPane", new Vector2(0.0f, 0.0f), ImGuiChildFlags.None);
		var folderContents = AssetsWindowBrowserModelBuilder.GetFolderContents(selectedFolder, _assetSearchText);
		DrawCurrentFolderContents(folderContents, scene);
		_dragDrop.RegisterContentPaneDropTarget(_selection.SelectedFolderPath);
		if (ImGui.BeginPopupContextWindow(CurrentFolderContextMenuId,
			    ImGuiPopupFlags.MouseButtonRight | ImGuiPopupFlags.NoOpenOverItems))
		{
			DrawFolderScopedContextMenu(_selection.SelectedFolderPath);
			ImGui.EndPopup();
		}

		ImGui.EndChild();
		ImGui.PopStyleColor(2);
		ImGui.EndGroup();
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
		_dragDrop.RegisterFolderDropTarget(folder.RelativePath);
		DrawFolderTreeLabel(folder, nodeCursorX);

		if (leftClicked)
		{
			_interactionState.SetFocusedWindow(EditorFocusedWindow.Assets);
			SelectFolder(folder.RelativePath);
		}

		if (rightClicked)
		{
			_interactionState.SetFocusedWindow(EditorFocusedWindow.Assets);
			ImGui.OpenPopup(LocalItemContextMenuId);
		}

		var popupOpen = ImGui.BeginPopup(LocalItemContextMenuId);
		if (popupOpen)
		{
			DrawFolderScopedContextMenu(folder.RelativePath);
			ImGui.EndPopup();
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

	private void DrawFolderTreeLabel(AssetsWindowFolderNode folder, float nodeCursorX)
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

		if (TryGetFolderIconTexture(out var textureId))
		{
			drawList.AddImage(textureId, iconPosition, iconPosition + Vector2.One * iconSize);
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

	private void DrawBreadcrumbRow(string relativeFolderPath)
	{
		DrawBreadcrumbs(relativeFolderPath);
		DrawSearchInput();
	}

	private void DrawBreadcrumbs(string relativeFolderPath)
	{
		var parts = relativeFolderPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
		var currentPath = string.Empty;
		for (var i = 0; i < parts.Length; i++)
		{
			currentPath = i == 0 ? parts[i] : $"{currentPath}/{parts[i]}";
			if (i > 0)
			{
				ImGui.SameLine(0.0f, 6.0f);
				ImGui.TextDisabled(">");
				ImGui.SameLine(0.0f, 6.0f);
			}

			if (ImGui.SmallButton(parts[i]))
			{
				SelectFolder(currentPath);
			}
		}
	}

	private void DrawSearchInput()
	{
		var style = ImGui.GetStyle();
		var label = "Search";
		var labelWidth = ImGui.CalcTextSize(label).X;
		var currentX = ImGui.GetCursorPosX();
		var contentMaxX = currentX + ImGui.GetContentRegionAvail().X;
		var idealTotalWidth = labelWidth + style.ItemInnerSpacing.X + SearchInputWidth;
		var searchStartX = MathF.Max(currentX + style.ItemSpacing.X, contentMaxX - idealTotalWidth);
		var inputWidth = MathF.Min(
			SearchInputWidth,
			MathF.Max(SearchInputMinWidth, contentMaxX - searchStartX - labelWidth - style.ItemInnerSpacing.X));

		ImGui.SameLine(0.0f, style.ItemSpacing.X);
		ImGui.SetCursorPosX(searchStartX);
		ImGui.TextDisabled(label);
		ImGui.SameLine(0.0f, style.ItemInnerSpacing.X);
		ImGui.SetNextItemWidth(inputWidth);
		ImGui.InputText("##AssetsSearch", ref _assetSearchText, 256);
	}

	private void DrawCurrentFolderContents(AssetsWindowFolderContents contents, EditorScene scene)
	{
		if (contents.Folders.Count == 0 && contents.Sources.Count == 0)
		{
			ImGui.TextDisabled(contents.IsSearchActive ? "No assets match the search." : "This folder is empty.");
			return;
		}

		var availableWidth = MathF.Max(ImGui.GetContentRegionAvail().X, GridMinItemWidth);
		var columnCount = Math.Max(1, (int)MathF.Floor(availableWidth / GridMinItemWidth));
		if (ImGui.BeginTable("AssetsGrid", columnCount,
			    ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.PadOuterX) == false)
		{
			return;
		}

		var columnIndex = 0;
		for (var i = 0; i < contents.Folders.Count; i++)
		{
			AssetsWindowDrawing.AdvanceTable(ref columnIndex, columnCount);
			DrawFolderCard(contents.Folders[i]);
		}

		for (var i = 0; i < contents.Sources.Count; i++)
		{
			AssetsWindowDrawing.AdvanceTable(ref columnIndex, columnCount);
			DrawSourceCard(contents.Sources[i], scene);
		}

		ImGui.EndTable();
	}

	private void DrawFolderCard(AssetsWindowFolderNode folder)
	{
		ImGui.PushID(folder.RelativePath);
		ImGui.BeginChild("FolderCard", new Vector2(0.0f, FolderCardHeight), ImGuiChildFlags.None,
			ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
		var buttonSize = new Vector2(ImGui.GetContentRegionAvail().X, FolderCardHeight - 6.0f);
		ImGui.InvisibleButton("FolderCardButton", buttonSize);
		var leftClicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
		var rightClicked = ImGui.IsItemClicked(ImGuiMouseButton.Right);
		var doubleClicked = leftClicked && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);
		if (leftClicked)
		{
			_dragDrop.Press(AssetBrowserDragTarget.ForFolder(folder.RelativePath));
		}

		_dragDrop.RegisterFolderDropTarget(folder.RelativePath);
		DrawFolderCardContents(folder);

		if (doubleClicked)
		{
			_interactionState.SetFocusedWindow(EditorFocusedWindow.Assets);
			SelectFolder(folder.RelativePath);
		}

		if (rightClicked)
		{
			_interactionState.SetFocusedWindow(EditorFocusedWindow.Assets);
			ImGui.OpenPopup(LocalItemContextMenuId);
		}

		var popupOpen = ImGui.BeginPopup(LocalItemContextMenuId);
		if (popupOpen)
		{
			DrawFolderScopedContextMenu(folder.RelativePath);
			ImGui.EndPopup();
		}

		ImGui.EndChild();
		ImGui.PopID();
	}

	private void DrawFolderCardContents(AssetsWindowFolderNode folder)
	{
		var itemMin = ImGui.GetItemRectMin();
		var itemMax = ImGui.GetItemRectMax();
		var drawList = ImGui.GetWindowDrawList();
		if (ImGui.IsItemHovered())
		{
			drawList.AddRectFilled(itemMin, itemMax, ImGui.GetColorU32(ImGuiCol.HeaderHovered), 4.0f);
		}

		var thumbnailMin =
			new Vector2(itemMin.X + ((itemMax.X - itemMin.X) - ThumbnailSize.X) * 0.5f, itemMin.Y + 12.0f);
		var thumbnailMax = thumbnailMin + ThumbnailSize;
		if (TryGetFolderIconTexture(out var textureId))
		{
			drawList.AddImage(textureId, thumbnailMin, thumbnailMax);
		}
		else
		{
			drawList.AddRect(thumbnailMin, thumbnailMax, AssetsWindowDrawing.SeparatorColor());
		}

		if (_dragDrop.IsActiveDropTarget(folder.RelativePath))
		{
			drawList.AddRect(itemMin, itemMax, ImGui.GetColorU32(ImGuiCol.HeaderActive), 4.0f, ImDrawFlags.None, 2.0f);
		}

		AssetsWindowDrawing.DrawCardTextBlock(
			drawList,
			itemMin,
			itemMax,
			thumbnailMax.Y + 10.0f,
			folder.Name,
			null);
	}

	private void DrawSourceCard(AssetsWindowSourceItem source, EditorScene scene)
	{
		var isExpanded = _selection.ExpandedSourceId == source.SourceId;
		var toggleHeight = source.SubAssets.Count > 0 ? SourceCardToggleHeight : 0.0f;
		var totalHeight = SourceCardHeaderHeight + toggleHeight +
		                  (isExpanded ? source.SubAssets.Count * SubAssetRowHeight : 0.0f);
		ImGui.PushID(source.SourceId.ToString());
		ImGui.BeginChild("SourceCard", new Vector2(0.0f, totalHeight), ImGuiChildFlags.None,
			ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);

		ImGui.InvisibleButton("SourceHeaderButton",
			new Vector2(ImGui.GetContentRegionAvail().X, SourceCardHeaderHeight - 6.0f));
		var headerLeftClicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
		var headerRightClicked = ImGui.IsItemClicked(ImGuiMouseButton.Right);
		var headerDoubleClicked = headerLeftClicked && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);
		DrawSourceHeaderContents(source, ImGui.GetItemRectMin(), ImGui.GetItemRectMax());

		if (headerLeftClicked)
		{
			_dragDrop.Press(AssetBrowserDragTarget.ForSource(source.RelativeSourcePath));
			_interactionState.SetFocusedWindow(EditorFocusedWindow.Assets);
			var wasPrimarySelected = _assetSelectionService.SelectedAssetId == source.PrimaryAsset.Id;
			SelectAsset(source.PrimaryAsset);
			if (headerDoubleClicked)
			{
				OpenAsset(source.PrimaryAsset, scene);
			}
			else if (wasPrimarySelected && source.SubAssets.Count > 0)
			{
				_selection.ExpandedSourceId =
					AssetsWindowBrowserModelBuilder.ToggleExpandedSource(_selection.ExpandedSourceId, source.SourceId);
			}
		}

		if (headerRightClicked)
		{
			_interactionState.SetFocusedWindow(EditorFocusedWindow.Assets);
			SelectAsset(source.PrimaryAsset, requestFocus: false);
			ImGui.OpenPopup(LocalItemContextMenuId);
		}

		var popupOpen = ImGui.BeginPopup(LocalItemContextMenuId);
		if (popupOpen)
		{
			DrawSourceContextMenu(
				scene,
				source,
				BrowserContextTarget.ForSource(source.RelativeSourcePath, source.SourceId, source.PrimaryAsset.Id),
				renameLabel: "Rename",
				deleteLabel: "Delete");
			ImGui.EndPopup();
		}

		if (source.SubAssets.Count > 0)
		{
			ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 2.0f);
			if (ImGui.SmallButton(isExpanded ? "Hide Sub-assets" : $"Show Sub-assets ({source.SubAssets.Count})"))
			{
				_selection.ExpandedSourceId =
					AssetsWindowBrowserModelBuilder.ToggleExpandedSource(_selection.ExpandedSourceId, source.SourceId);
			}
		}

		if (isExpanded)
		{
			for (var i = 0; i < source.SubAssets.Count; i++)
			{
				DrawSubAssetRow(source, source.SubAssets[i], scene);
			}
		}

		ImGui.EndChild();
		ImGui.PopID();
	}

	private void DrawSourceHeaderContents(AssetsWindowSourceItem source, Vector2 itemMin, Vector2 itemMax)
	{
		var drawList = ImGui.GetWindowDrawList();
		var isSelected = _assetSelectionService.SelectedAssetId == source.PrimaryAsset.Id;
		var backgroundColor = isSelected
			? ImGui.GetColorU32(ImGuiCol.HeaderActive)
			: ImGui.IsItemHovered()
				? ImGui.GetColorU32(ImGuiCol.HeaderHovered)
				: 0u;
		if (backgroundColor != 0)
		{
			drawList.AddRectFilled(itemMin, itemMax, backgroundColor, 4.0f);
		}

		var thumbnailMin =
			new Vector2(itemMin.X + ((itemMax.X - itemMin.X) - ThumbnailSize.X) * 0.5f, itemMin.Y + 12.0f);
		var thumbnailMax = thumbnailMin + ThumbnailSize;
		DrawAssetThumbnail(drawList, thumbnailMin, thumbnailMax, source.PrimaryAsset);
		AssetsWindowDrawing.DrawCardTextBlock(
			drawList,
			itemMin,
			itemMax,
			thumbnailMax.Y + 10.0f,
			Path.GetFileName(source.RelativeSourcePath),
			GetAssetSubtitle(source.PrimaryAsset, source.SubAssets.Count));
	}

	private void DrawSubAssetRow(AssetsWindowSourceItem source, AssetDatabaseEntry subAsset, EditorScene scene)
	{
		ImGui.PushID(subAsset.Id.ToString());
		var isSelected = _assetSelectionService.SelectedAssetId == subAsset.Id;
		if (ImGui.Selectable($"{subAsset.Name}  [{subAsset.Type}]", isSelected, ImGuiSelectableFlags.SpanAllColumns))
		{
			_interactionState.SetFocusedWindow(EditorFocusedWindow.Assets);
			SelectAsset(subAsset);
		}

		if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
		{
			OpenAsset(subAsset, scene);
		}

		var rightClicked = ImGui.IsItemClicked(ImGuiMouseButton.Right);
		if (rightClicked)
		{
			_interactionState.SetFocusedWindow(EditorFocusedWindow.Assets);
			SelectAsset(subAsset, requestFocus: false);
			ImGui.OpenPopup(LocalItemContextMenuId);
		}

		var popupOpen = ImGui.BeginPopup(LocalItemContextMenuId);
		if (popupOpen)
		{
			DrawSourceContextMenu(
				scene,
				source,
				BrowserContextTarget.ForSubAsset(source.RelativeSourcePath, source.SourceId, subAsset.Id),
				renameLabel: "Rename Source Asset",
				deleteLabel: "Delete Source Asset");
			ImGui.EndPopup();
		}

		ImGui.PopID();
	}

	private void CompleteSuccessfulMove(AssetBrowserDragTarget dragTarget, string movedPath)
	{
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
		if (canDelete && ImGui.MenuItem("Rename"))
		{
			RequestRename(PendingRenameTarget.ForFolder(folderPath));
		}

		if (canDelete && ImGui.MenuItem("Delete Folder"))
		{
			RequestDelete(PendingDeleteTarget.ForFolder(folderPath));
		}
	}

	private void CreateFolder(string parentFolderPath)
	{
		try
		{
			var folderPath = _projectService.CreateFolder(parentFolderPath, GetNewFolderName(parentFolderPath));
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
				_assetPipelineService.InstantiateImportedModel(_projectService.ProjectRootPath!, asset.Id, scene.World);
				_interactionState.MarkSceneDirty();
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
				_assetPipelineService.InstantiatePrefab(_projectService.ProjectRootPath!, asset.Id, scene);
				_interactionState.MarkSceneDirty();
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

		if (ImGui.MenuItem(renameLabel))
		{
			RequestRename(PendingRenameTarget.ForSource(contextTarget.RelativeSourcePath!));
		}

		if (ImGui.MenuItem(deleteLabel))
		{
			RequestDelete(PendingDeleteTarget.ForSource(contextTarget.RelativeSourcePath!));
		}
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
			RequestRename(PendingRenameTarget.ForSource(selectedAsset.RelativeSourcePath));
			return true;
		}

		if (string.Equals(_selection.SelectedFolderPath, AssetPipelinePaths.AssetsFolderName,
			    StringComparison.OrdinalIgnoreCase))
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
			RequestDelete(PendingDeleteTarget.ForSource(selectedAsset.RelativeSourcePath));
			return true;
		}

		if (string.Equals(_selection.SelectedFolderPath, AssetPipelinePaths.AssetsFolderName,
			    StringComparison.OrdinalIgnoreCase))
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

	private void SelectAsset(AssetDatabaseEntry asset, bool requestFocus = true)
	{
		_selection.SetSelectedFolderPath(ProjectPathUtility.GetFolderPath(asset.RelativeSourcePath));
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

	private bool TryGetFolderIconTexture(out nint textureId)
	{
		return _icons.TryGet("folder", out textureId);
	}

	private void DrawAssetThumbnail(ImDrawListPtr drawList, Vector2 min, Vector2 max, AssetDatabaseEntry asset)
	{
		if (asset.Type == AssetType.Texture2D && _assetThumbnailLoader.TryGetTextureThumbnailId(asset, out var textureId))
		{
			drawList.AddImage(textureId, min, max);
			return;
		}

		drawList.AddRect(min, max, AssetsWindowDrawing.SeparatorColor());
		var label = _assetHandlerRegistry.TryGetHandler(asset.Type, out var handler)
			? handler.ThumbnailLabel
			: GetFallbackThumbnailLabel(asset.Type);
		var textSize = ImGui.CalcTextSize(label);
		var textPos = new Vector2(min.X + ((max.X - min.X) - textSize.X) * 0.5f,
			min.Y + ((max.Y - min.Y) - textSize.Y) * 0.5f);
		drawList.AddText(textPos, ImGui.GetColorU32(ImGuiCol.TextDisabled), label);
	}

	private string GetAssetSubtitle(AssetDatabaseEntry asset, int childCount = 0)
	{
		var baseSubtitle = _assetHandlerRegistry.TryGetHandler(asset.Type, out var handler)
			? handler.GetSubtitle(asset)
			: asset.Type switch
			{
				AssetType.Model3D => "3D Model",
				_ => asset.Type.ToString()
			};
		if (childCount <= 0)
		{
			return baseSubtitle;
		}

		var suffix = childCount == 1 ? "1 sub-asset" : $"{childCount} sub-assets";
		return $"{baseSubtitle} | {suffix}";
	}

	private static string GetFallbackThumbnailLabel(AssetType assetType)
	{
		return assetType switch
		{
			AssetType.Scene => "SCN",
			AssetType.Prefab => "PFB",
			AssetType.Model3D => "3D",
			_ => assetType.ToString().ToUpperInvariant()
		};
	}

	private void ShowError(string errorMessage)
	{
		_errorMessage = errorMessage;
		_openErrorPopup = true;
	}

}

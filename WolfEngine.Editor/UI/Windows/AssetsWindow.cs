using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor;
using WolfEngine.Editor.Projects;
using WolfEngine.Importing;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor.UI;

public sealed class AssetsWindow : EditorWindow, IEditorAssetDeletionHandler
{
	private static readonly Vector2 ThumbnailSize = new(46.0f, 46.0f);
	private const float FolderTreeWidth = 220.0f;
	private const float FolderTreeIconSize = 15.5f;
	private const float FolderCardHeight = 116.0f;
	private const float SourceCardHeaderHeight = 116.0f;
	private const float SourceCardToggleHeight = 26.0f;
	private const float SubAssetRowHeight = 24.0f;
	private const float GridMinItemWidth = 210.0f;
	private const string ErrorPopupId = "AssetsWindowError";
	private const string DeletePopupId = "AssetsWindowDelete";
	private const string CurrentFolderContextMenuId = "AssetsWindowCurrentFolderContextMenu";
	private const string LocalItemContextMenuId = "ItemContextMenu";

	private readonly IEditorProjectService _projectService;
	private readonly IProjectAssetPipelineService _assetPipelineService;
	private readonly IImageLoader _imageLoader;
	private readonly IAssetSelectionService _assetSelectionService;
	private readonly IEditorAssetHandlerRegistry _assetHandlerRegistry;
	private readonly IIconManager _icons;
	private readonly IEditorInteractionState _interactionState;
	private readonly IEditorCommandService _commandService;
	private string _errorMessage = string.Empty;
	private bool _openErrorPopup;
	private string _selectedFolderPath = AssetPipelinePaths.AssetsFolderName;
	private Guid? _expandedSourceId;
	private PendingDeleteTarget? _pendingDeleteTarget;
	private bool _openDeletePopup;

	public AssetsWindow(
		IEditorProjectService projectService,
		IProjectAssetPipelineService assetPipelineService,
		IImageLoader imageLoader,
		IAssetSelectionService assetSelectionService,
		IEditorAssetHandlerRegistry assetHandlerRegistry,
		IIconManager icons,
		IEditorInteractionState interactionState,
		IEditorCommandService commandService)
	{
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
		_assetPipelineService = assetPipelineService ?? throw new ArgumentNullException(nameof(assetPipelineService));
		_imageLoader = imageLoader ?? throw new ArgumentNullException(nameof(imageLoader));
		_assetSelectionService = assetSelectionService ?? throw new ArgumentNullException(nameof(assetSelectionService));
		_assetHandlerRegistry = assetHandlerRegistry ?? throw new ArgumentNullException(nameof(assetHandlerRegistry));
		_icons = icons ?? throw new ArgumentNullException(nameof(icons));
		_interactionState = interactionState ?? throw new ArgumentNullException(nameof(interactionState));
		_commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));
	}

	internal string? PendingDeleteKindForTesting => _pendingDeleteTarget?.Kind.ToString();
	internal string? PendingDeleteRelativePathForTesting => _pendingDeleteTarget?.RelativePath;

	internal void SetSelectedFolderForTesting(string relativeFolderPath)
	{
		_selectedFolderPath = ProjectPathUtility.NormalizeAssetsFolderPath(relativeFolderPath);
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
		}

		if (_projectService.HasOpenProject == false || string.IsNullOrWhiteSpace(_projectService.AssetsPath))
		{
			ImGui.BeginDisabled();
			ImGui.TextUnformatted("No project open.");
			ImGui.EndDisabled();
			DrawDeletePopup();
			DrawErrorPopup();
			ImGui.End();
			return;
		}

		var browserModel = AssetsWindowBrowserModelBuilder.Build(_projectService.CurrentAssetDatabase.Assets, _projectService.AssetsPath);
		PruneState(browserModel);
		var selectedFolder = browserModel.FoldersByPath[_selectedFolderPath];

		DrawFolderTree(browserModel);
		ImGui.SameLine();
		DrawContentArea(selectedFolder, scene);
		DrawDeletePopup();
		DrawErrorPopup();
		ImGui.End();
	}

	private void DrawFolderTree(AssetsWindowBrowserModel browserModel)
	{
		PushPaneStyle();
		ImGui.BeginChild("AssetsFolderTree", new Vector2(FolderTreeWidth, 0.0f), ImGuiChildFlags.Borders);
		DrawFolderTreeNode(browserModel.RootFolder);
		if (ImGui.BeginPopupContextWindow(CurrentFolderContextMenuId + "Tree", ImGuiPopupFlags.MouseButtonRight | ImGuiPopupFlags.NoOpenOverItems))
		{
			DrawFolderScopedContextMenu(_selectedFolderPath);
			ImGui.EndPopup();
		}

		ImGui.EndChild();
		ImGui.PopStyleColor(2);
	}

	private void DrawContentArea(AssetsWindowFolderNode selectedFolder, EditorScene scene)
	{
		ImGui.BeginGroup();
		DrawBreadcrumbs(selectedFolder.RelativePath);
		ImGui.Separator();

		PushPaneStyle();
		ImGui.BeginChild("AssetsContentPane", new Vector2(0.0f, 0.0f), ImGuiChildFlags.Borders);
		DrawCurrentFolderContents(selectedFolder, scene);
		if (ImGui.BeginPopupContextWindow(CurrentFolderContextMenuId, ImGuiPopupFlags.MouseButtonRight | ImGuiPopupFlags.NoOpenOverItems))
		{
			DrawFolderScopedContextMenu(_selectedFolderPath);
			ImGui.EndPopup();
		}

		ImGui.EndChild();
		ImGui.PopStyleColor(2);
		ImGui.EndGroup();
	}

	private void DrawFolderTreeNode(AssetsWindowFolderNode folder)
	{
		ImGui.PushID(folder.RelativePath);

		var isSelected = string.Equals(_selectedFolderPath, folder.RelativePath, StringComparison.OrdinalIgnoreCase);
		var containsSelectedDescendant = ProjectPathUtility.IsSameOrDescendant(_selectedFolderPath, folder.RelativePath);
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

		if (containsSelectedDescendant && hasChildren)
		{
			ImGui.SetNextItemOpen(true, ImGuiCond.Always);
		}

		var nodeCursorX = ImGui.GetCursorScreenPos().X;
		var open = ImGui.TreeNodeEx("##FolderNode", flags);
		var leftClicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
		var rightClicked = ImGui.IsItemClicked(ImGuiMouseButton.Right);
		DrawFolderTreeLabel(folder, nodeCursorX);

		if (leftClicked)
		{
			_interactionState.SetFocusedWindow(EditorFocusedWindow.Assets);
			SelectFolder(folder.RelativePath);
		}

		if (rightClicked)
		{
			_interactionState.SetFocusedWindow(EditorFocusedWindow.Assets);
			SelectFolder(folder.RelativePath);
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

	private void DrawCurrentFolderContents(AssetsWindowFolderNode folder, EditorScene scene)
	{
		if (folder.Children.Count == 0 && folder.Sources.Count == 0)
		{
			ImGui.TextDisabled("This folder is empty.");
			return;
		}

		var availableWidth = MathF.Max(ImGui.GetContentRegionAvail().X, GridMinItemWidth);
		var columnCount = Math.Max(1, (int)MathF.Floor(availableWidth / GridMinItemWidth));
		if (ImGui.BeginTable("AssetsGrid", columnCount, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.PadOuterX) == false)
		{
			return;
		}

		var columnIndex = 0;
		for (var i = 0; i < folder.Children.Count; i++)
		{
			AdvanceTable(ref columnIndex, columnCount);
			DrawFolderCard(folder.Children[i]);
		}

		for (var i = 0; i < folder.Sources.Count; i++)
		{
			AdvanceTable(ref columnIndex, columnCount);
			DrawSourceCard(folder.Sources[i], scene);
		}

		ImGui.EndTable();
	}

	private void DrawFolderCard(AssetsWindowFolderNode folder)
	{
		ImGui.PushID(folder.RelativePath);
		ImGui.BeginChild("FolderCard", new Vector2(0.0f, FolderCardHeight), ImGuiChildFlags.Borders, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
		var buttonSize = new Vector2(ImGui.GetContentRegionAvail().X, FolderCardHeight - 6.0f);
		ImGui.InvisibleButton("FolderCardButton", buttonSize);
		var leftClicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
		var rightClicked = ImGui.IsItemClicked(ImGuiMouseButton.Right);
		var doubleClicked = leftClicked && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);
		DrawFolderCardContents(folder);

		if (doubleClicked)
		{
			_interactionState.SetFocusedWindow(EditorFocusedWindow.Assets);
			SelectFolder(folder.RelativePath);
		}

		if (rightClicked)
		{
			_interactionState.SetFocusedWindow(EditorFocusedWindow.Assets);
			SelectFolder(folder.RelativePath);
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
		var backgroundColor = ImGui.IsItemHovered()
			? ImGui.GetColorU32(ImGuiCol.HeaderHovered)
			: ImGui.GetColorU32(ImGuiCol.Button);
		drawList.AddRectFilled(itemMin, itemMax, backgroundColor, 4.0f);
		drawList.AddRect(itemMin, itemMax, ImGui.GetColorU32(ImGuiCol.Border), 4.0f);

		var thumbnailMin = new Vector2(itemMin.X + ((itemMax.X - itemMin.X) - ThumbnailSize.X) * 0.5f, itemMin.Y + 12.0f);
		var thumbnailMax = thumbnailMin + ThumbnailSize;
		if (TryGetFolderIconTexture(out var textureId))
		{
			drawList.AddImage(textureId, thumbnailMin, thumbnailMax);
		}
		else
		{
			drawList.AddRect(thumbnailMin, thumbnailMax, ImGui.GetColorU32(ImGuiCol.Border));
		}

		DrawCardTextBlock(
			drawList,
			itemMin,
			itemMax,
			thumbnailMax.Y + 10.0f,
			folder.Name,
			null);
	}

	private void DrawSourceCard(AssetsWindowSourceItem source, EditorScene scene)
	{
		var isExpanded = _expandedSourceId == source.SourceId;
		var toggleHeight = source.SubAssets.Count > 0 ? SourceCardToggleHeight : 0.0f;
		var totalHeight = SourceCardHeaderHeight + toggleHeight + (isExpanded ? source.SubAssets.Count * SubAssetRowHeight : 0.0f);
		ImGui.PushID(source.SourceId.ToString());
		ImGui.BeginChild("SourceCard", new Vector2(0.0f, totalHeight), ImGuiChildFlags.Borders, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);

		ImGui.InvisibleButton("SourceHeaderButton", new Vector2(ImGui.GetContentRegionAvail().X, SourceCardHeaderHeight - 6.0f));
		var headerLeftClicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
		var headerRightClicked = ImGui.IsItemClicked(ImGuiMouseButton.Right);
		var headerDoubleClicked = headerLeftClicked && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);
		DrawSourceHeaderContents(source, ImGui.GetItemRectMin(), ImGui.GetItemRectMax());

		if (headerLeftClicked)
		{
			_interactionState.SetFocusedWindow(EditorFocusedWindow.Assets);
			var wasPrimarySelected = _assetSelectionService.SelectedAssetId == source.PrimaryAsset.Id;
			SelectAsset(source.PrimaryAsset);
			if (headerDoubleClicked)
			{
				OpenAsset(source.PrimaryAsset, scene);
			}
			else if (wasPrimarySelected && source.SubAssets.Count > 0)
			{
				_expandedSourceId = AssetsWindowBrowserModelBuilder.ToggleExpandedSource(_expandedSourceId, source.SourceId);
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
				deleteLabel: "Delete");
			ImGui.EndPopup();
		}

		if (source.SubAssets.Count > 0)
		{
			ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 2.0f);
			if (ImGui.SmallButton(isExpanded ? "Hide Sub-assets" : $"Show Sub-assets ({source.SubAssets.Count})"))
			{
				_expandedSourceId = AssetsWindowBrowserModelBuilder.ToggleExpandedSource(_expandedSourceId, source.SourceId);
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
				: ImGui.GetColorU32(ImGuiCol.Button);
		drawList.AddRectFilled(itemMin, itemMax, backgroundColor, 4.0f);
		drawList.AddRect(itemMin, itemMax, ImGui.GetColorU32(ImGuiCol.Border), 4.0f);

		var thumbnailMin = new Vector2(itemMin.X + ((itemMax.X - itemMin.X) - ThumbnailSize.X) * 0.5f, itemMin.Y + 12.0f);
		var thumbnailMax = thumbnailMin + ThumbnailSize;
		DrawAssetThumbnail(drawList, thumbnailMin, thumbnailMax, source.PrimaryAsset);
		DrawCardTextBlock(
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
				deleteLabel: "Delete Source Asset");
			ImGui.EndPopup();
		}

		ImGui.PopID();
	}

	private void DrawFolderScopedContextMenu(string folderPath)
	{
		if (ImGui.BeginMenu("Create"))
		{
			DrawCreateMenuItems(_assetHandlerRegistry.GetCreateMenuItems(), folderPath);
			ImGui.EndMenu();
		}

		var canDelete = string.Equals(folderPath, AssetPipelinePaths.AssetsFolderName, StringComparison.OrdinalIgnoreCase) == false;
		if (canDelete && ImGui.MenuItem("Delete Folder"))
		{
			RequestDelete(PendingDeleteTarget.ForFolder(folderPath));
		}
	}

	private void DrawSourceContextMenu(EditorScene scene, AssetsWindowSourceItem sourceItem, BrowserContextTarget contextTarget, string deleteLabel)
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
			var isExpanded = _expandedSourceId == sourceItem.SourceId;
			if (ImGui.MenuItem(isExpanded ? "Hide Sub-assets" : "Show Sub-assets"))
			{
				_expandedSourceId = AssetsWindowBrowserModelBuilder.ToggleExpandedSource(_expandedSourceId, sourceItem.SourceId);
			}
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
				_selectedFolderPath = ProjectPathUtility.GetFolderPath(createdAsset.RelativeSourcePath);
			}

			_expandedSourceId = null;
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

	private void RequestDelete(PendingDeleteTarget deleteTarget)
	{
		_pendingDeleteTarget = deleteTarget;
		_openDeletePopup = true;
	}

	public bool RequestDeleteSelectedItem()
	{
		if (_assetSelectionService.SelectedAssetId is { } selectedAssetId &&
		    _projectService.TryGetAsset(selectedAssetId, out var selectedAsset))
		{
			RequestDelete(PendingDeleteTarget.ForSource(selectedAsset.RelativeSourcePath));
			return true;
		}

		if (string.Equals(_selectedFolderPath, AssetPipelinePaths.AssetsFolderName, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		RequestDelete(PendingDeleteTarget.ForFolder(_selectedFolderPath));
		return true;
	}

	private void SelectFolder(string relativeFolderPath)
	{
		_selectedFolderPath = ProjectPathUtility.NormalizeAssetsFolderPath(relativeFolderPath);
		_expandedSourceId = null;
		_assetSelectionService.Clear();
	}

	private void SelectAsset(AssetDatabaseEntry asset, bool requestFocus = true)
	{
		_selectedFolderPath = ProjectPathUtility.GetFolderPath(asset.RelativeSourcePath);
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

	private void PruneState(AssetsWindowBrowserModel browserModel)
	{
		_selectedFolderPath = AssetsWindowBrowserModelBuilder.NormalizeSelectedFolderPath(browserModel, _selectedFolderPath);
		if (_expandedSourceId.HasValue && browserModel.SourcesBySourceId.ContainsKey(_expandedSourceId.Value) == false)
		{
			_expandedSourceId = null;
		}

		if (_assetSelectionService.SelectedAssetId is { } selectedAssetId && _projectService.TryGetAsset(selectedAssetId, out var selectedAsset))
		{
			_selectedFolderPath = ProjectPathUtility.GetFolderPath(selectedAsset.RelativeSourcePath);
		}
		else if (_assetSelectionService.SelectedAssetId.HasValue)
		{
			_assetSelectionService.Clear();
		}
	}

	private void ValidateSelectionAfterProjectMutation()
	{
		if (_assetSelectionService.SelectedAssetId is { } selectedAssetId && _projectService.TryGetAsset(selectedAssetId, out _) == false)
		{
			_assetSelectionService.Clear();
		}

		if (_projectService.HasOpenProject)
		{
			_selectedFolderPath = GetNearestExistingFolderPath(_selectedFolderPath);
		}

		if (_expandedSourceId.HasValue && _projectService.CurrentAssetDatabase.Assets.Any(asset => asset.SourceId == _expandedSourceId.Value) == false)
		{
			_expandedSourceId = null;
		}
	}

	private string GetNearestExistingFolderPath(string relativeFolderPath)
	{
		var normalizedFolderPath = ProjectPathUtility.NormalizeAssetsFolderPath(relativeFolderPath);
		while (string.Equals(normalizedFolderPath, AssetPipelinePaths.AssetsFolderName, StringComparison.OrdinalIgnoreCase) == false)
		{
			var absoluteFolderPath = _projectService.GetAbsolutePath(normalizedFolderPath);
			if (Directory.Exists(absoluteFolderPath))
			{
				return normalizedFolderPath;
			}

			normalizedFolderPath = ProjectPathUtility.GetParentFolderPath(normalizedFolderPath);
		}

		return AssetPipelinePaths.AssetsFolderName;
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
		if (asset.Type == AssetType.Texture2D && asset.TextureSummary is not null && string.IsNullOrWhiteSpace(asset.TextureSummary.RelativeSourceAssetPath) == false)
		{
			var assetAbsolutePath = _projectService.GetAbsolutePath(asset.TextureSummary.RelativeSourceAssetPath);
			if (_imageLoader.TryGetImGuiTextureId(assetAbsolutePath, out var textureId, StbImageLoader.IsSrgb(asset.TextureSummary.Semantic)))
			{
				drawList.AddImage(textureId, min, max);
				return;
			}
		}

		drawList.AddRect(min, max, ImGui.GetColorU32(ImGuiCol.Border));
		var label = _assetHandlerRegistry.TryGetHandler(asset.Type, out var handler)
			? handler.ThumbnailLabel
			: GetFallbackThumbnailLabel(asset.Type);
		var textSize = ImGui.CalcTextSize(label);
		var textPos = new Vector2(min.X + ((max.X - min.X) - textSize.X) * 0.5f, min.Y + ((max.Y - min.Y) - textSize.Y) * 0.5f);
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

	private static void DrawCenteredText(ImDrawListPtr drawList, string text, float minX, float maxX, float y, uint color)
	{
		var textSize = ImGui.CalcTextSize(text);
		var availableWidth = maxX - minX;
		var textX = minX + MathF.Max((availableWidth - textSize.X) * 0.5f, 0.0f);
		drawList.AddText(new Vector2(textX, y), color, text);
	}

	private static void DrawCardTextBlock(ImDrawListPtr drawList, Vector2 itemMin, Vector2 itemMax, float startY, string title, string? subtitle)
	{
		var textInset = 8.0f;
		var titleMin = new Vector2(itemMin.X + textInset, startY);
		var titleMax = new Vector2(itemMax.X - textInset, startY + ImGui.GetTextLineHeight());
		drawList.AddText(titleMin, ImGui.GetColorU32(ImGuiCol.Text), ClipTextToWidth(title, titleMax.X - titleMin.X));

		if (string.IsNullOrWhiteSpace(subtitle))
		{
			return;
		}

		var subtitleY = startY + ImGui.GetTextLineHeightWithSpacing();
		var subtitleMin = new Vector2(itemMin.X + textInset, subtitleY);
		var subtitleMax = new Vector2(itemMax.X - textInset, subtitleY + ImGui.GetTextLineHeight());
		drawList.AddText(subtitleMin, ImGui.GetColorU32(ImGuiCol.TextDisabled), ClipTextToWidth(subtitle, subtitleMax.X - subtitleMin.X));
	}

	private static string ClipTextToWidth(string text, float maxWidth)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return string.Empty;
		}

		if (ImGui.CalcTextSize(text).X <= maxWidth)
		{
			return text;
		}

		const string ellipsis = "...";
		for (var length = text.Length - 1; length > 0; length--)
		{
			var candidate = text[..length] + ellipsis;
			if (ImGui.CalcTextSize(candidate).X <= maxWidth)
			{
				return candidate;
			}
		}

		return ellipsis;
	}

	private static void PushPaneStyle()
	{
		ImGui.PushStyleColor(ImGuiCol.ChildBg, ImGui.GetColorU32(ImGuiCol.WindowBg));
		ImGui.PushStyleColor(ImGuiCol.Border, ImGui.GetColorU32(ImGuiCol.Separator));
	}

	private static void AdvanceTable(ref int columnIndex, int columnCount)
	{
		if (columnIndex == 0)
		{
			ImGui.TableNextRow();
		}

		ImGui.TableSetColumnIndex(columnIndex);
		columnIndex = (columnIndex + 1) % columnCount;
	}

	private void ShowError(string errorMessage)
	{
		_errorMessage = errorMessage;
		_openErrorPopup = true;
	}

	private sealed record BrowserContextTarget(BrowserContextKind Kind, string FolderPath, string? RelativeSourcePath, Guid? SourceId, Guid? AssetId)
	{
		public static BrowserContextTarget ForCurrentFolder(string folderPath) => new(BrowserContextKind.CurrentFolder, folderPath, null, null, null);
		public static BrowserContextTarget ForFolder(string folderPath) => new(BrowserContextKind.Folder, folderPath, null, null, null);
		public static BrowserContextTarget ForSource(string relativeSourcePath, Guid sourceId, Guid assetId) =>
			new(BrowserContextKind.Source, ProjectPathUtility.GetFolderPath(relativeSourcePath), relativeSourcePath, sourceId, assetId);
		public static BrowserContextTarget ForSubAsset(string relativeSourcePath, Guid sourceId, Guid assetId) =>
			new(BrowserContextKind.SubAsset, ProjectPathUtility.GetFolderPath(relativeSourcePath), relativeSourcePath, sourceId, assetId);
	}

	private enum BrowserContextKind
	{
		CurrentFolder,
		Folder,
		Source,
		SubAsset
	}

	private sealed record PendingDeleteTarget(DeleteTargetKind Kind, string RelativePath, string DisplayName, string ConfirmationText)
	{
		public static PendingDeleteTarget ForSource(string relativeSourcePath)
		{
			var displayName = Path.GetFileName(relativeSourcePath);
			return new PendingDeleteTarget(
				DeleteTargetKind.Source,
				relativeSourcePath,
				displayName,
				$"Delete '{displayName}' and all derived assets? This permanently removes the source file and its .meta file.");
		}

		public static PendingDeleteTarget ForFolder(string relativeFolderPath)
		{
			var displayName = Path.GetFileName(relativeFolderPath);
			return new PendingDeleteTarget(
				DeleteTargetKind.Folder,
				relativeFolderPath,
				displayName,
				$"Delete folder '{displayName}' and everything inside it? This permanently removes all files and derived assets under that folder.");
		}
	}

	private enum DeleteTargetKind
	{
		Source,
		Folder
	}
}

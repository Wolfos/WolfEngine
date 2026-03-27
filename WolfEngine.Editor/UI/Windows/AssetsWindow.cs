using System.Numerics;
using ImGuiNET;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor.UI;

public sealed class AssetsWindow : EditorWindow
{
	private static readonly Vector2 ThumbnailSize = new(36.0f, 36.0f);
	private const string ErrorPopupId = "AssetsWindowError";

	private readonly IEditorProjectService _projectService;
	private readonly IProjectAssetPipelineService _assetPipelineService;
	private readonly IImageLoader _imageLoader;
	private readonly IAssetSelectionService _assetSelectionService;
	private readonly IEditorAssetHandlerRegistry _assetHandlerRegistry;
	private readonly IEditorSceneWorkspace _sceneWorkspace;
	private string _errorMessage = string.Empty;
	private bool _openErrorPopup;

	public AssetsWindow(
		IEditorProjectService projectService,
		IProjectAssetPipelineService assetPipelineService,
		IImageLoader imageLoader,
		IAssetSelectionService assetSelectionService,
		IEditorAssetHandlerRegistry assetHandlerRegistry,
		IEditorSceneWorkspace sceneWorkspace)
	{
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
		_assetPipelineService = assetPipelineService ?? throw new ArgumentNullException(nameof(assetPipelineService));
		_imageLoader = imageLoader ?? throw new ArgumentNullException(nameof(imageLoader));
		_assetSelectionService = assetSelectionService ?? throw new ArgumentNullException(nameof(assetSelectionService));
		_assetHandlerRegistry = assetHandlerRegistry ?? throw new ArgumentNullException(nameof(assetHandlerRegistry));
		_sceneWorkspace = sceneWorkspace ?? throw new ArgumentNullException(nameof(sceneWorkspace));
	}

	public override string Name => "Assets";

	public override void Draw(EditorScene scene)
	{
		ImGui.SetNextWindowPos(new Vector2(0.0f, 520.0f), ImGuiCond.FirstUseEver);
		ImGui.SetNextWindowSize(new Vector2(320.0f, 240.0f), ImGuiCond.FirstUseEver);
		Begin();

		if (_projectService.HasOpenProject == false)
		{
			ImGui.BeginDisabled();
			ImGui.TextUnformatted("No project open.");
			ImGui.EndDisabled();
			DrawContextMenu(scene);
			DrawErrorPopup();
			ImGui.End();
			return;
		}

		ImGui.TextUnformatted(_projectService.ProjectRootPath ?? string.Empty);
		ImGui.Separator();

		var assetGroups = BuildAssetGroups(_projectService.CurrentAssetDatabase.Assets);
		if (assetGroups.Count == 0)
		{
			ImGui.TextUnformatted("No assets imported yet.");
			DrawContextMenu(scene);
			DrawErrorPopup();
			ImGui.End();
			return;
		}

		for (var i = 0; i < assetGroups.Count; i++)
		{
			DrawAssetGroup(assetGroups[i], scene);
		}

		DrawContextMenu(scene);
		DrawErrorPopup();
		ImGui.End();
	}

	private void DrawAssetGroup(AssetGroup assetGroup, EditorScene scene)
	{
		DrawAssetNode(assetGroup.Root, assetGroup.Children, scene);
	}

	private unsafe void DrawAssetNode(AssetDatabaseEntry asset, IReadOnlyList<AssetDatabaseEntry> children, EditorScene scene)
	{
		ImGui.PushID(asset.Id.ToString());

		var hasChildren = children.Count > 0;
		var isSelected = _assetSelectionService.SelectedAssetId == asset.Id;
		var containsSelectedChild = children.Any(child => child.Id == _assetSelectionService.SelectedAssetId);
		var style = ImGui.GetStyle();
		ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(style.FramePadding.X, 10.0f));

		var flags = ImGuiTreeNodeFlags.SpanFullWidth | ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.FramePadding;
		if (hasChildren == false)
		{
			flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;
		}

		if (isSelected)
		{
			flags |= ImGuiTreeNodeFlags.Selected;
			var selectedColor = ImGui.GetStyleColorVec4(ImGuiCol.HeaderActive);
			ImGui.PushStyleColor(ImGuiCol.Header, *selectedColor);
			ImGui.PushStyleColor(ImGuiCol.HeaderHovered, *selectedColor);
			ImGui.PushStyleColor(ImGuiCol.HeaderActive, *selectedColor);
		}

		if (containsSelectedChild)
		{
			ImGui.SetNextItemOpen(true, ImGuiCond.Always);
		}

		var nodeCursorPosition = ImGui.GetCursorScreenPos();
		var open = ImGui.TreeNodeEx("##AssetNode", flags);
		var nodeClicked = ImGui.IsItemClicked();
		var nodeRightClicked = ImGui.IsItemClicked(ImGuiMouseButton.Right);
		var nodeDoubleClicked = nodeClicked && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);
		DrawAssetRowContent(asset, nodeCursorPosition.X, children.Count);

		if (nodeClicked || nodeRightClicked)
		{
			_assetSelectionService.Select(asset.Id);
		}

		if (nodeDoubleClicked && asset.Type == AssetType.Scene)
		{
			try
			{
				_sceneWorkspace.LoadScene(asset.Id);
				EditorGui.ClearEntitySelection();
			}
			catch (Exception ex)
			{
				ShowError($"Failed to load scene: {ex.Message}");
			}
		}

		if (hasChildren && open)
		{
			for (var i = 0; i < children.Count; i++)
			{
				DrawAssetNode(children[i], [], scene);
			}

			ImGui.TreePop();
		}

		if (isSelected)
		{
			ImGui.PopStyleColor(3);
		}

		ImGui.PopStyleVar();

		ImGui.PopID();
	}

	private void DrawAssetRowContent(AssetDatabaseEntry asset, float nodeCursorX, int childCount)
	{
		var itemMin = ImGui.GetItemRectMin();
		var itemMax = ImGui.GetItemRectMax();
		var rowHeight = itemMax.Y - itemMin.Y;
		var drawList = ImGui.GetWindowDrawList();
		var thumbMin = new Vector2(
			nodeCursorX + ImGui.GetTreeNodeToLabelSpacing(),
			itemMin.Y + (rowHeight - ThumbnailSize.Y) * 0.5f);
		var thumbMax = thumbMin + ThumbnailSize;
		var textX = thumbMax.X + 8.0f;
		var titleY = itemMin.Y + 6.0f;
		var subtitleY = titleY + ImGui.GetTextLineHeightWithSpacing();

		DrawAssetThumbnail(drawList, thumbMin, thumbMax, asset);
		drawList.AddText(new Vector2(textX, titleY), ImGui.GetColorU32(ImGuiCol.Text), asset.Name);
		drawList.AddText(new Vector2(textX, subtitleY), ImGui.GetColorU32(ImGuiCol.TextDisabled), GetAssetSubtitle(asset, childCount));
	}

	private void DrawAssetThumbnail(ImDrawListPtr drawList, Vector2 min, Vector2 max, AssetDatabaseEntry asset)
	{
		if (asset.Type == AssetType.Texture2D && asset.TextureSummary is not null)
		{
			if (string.IsNullOrWhiteSpace(asset.TextureSummary.RelativeSourceAssetPath) == false)
			{
				var assetAbsolutePath = _projectService.GetAbsolutePath(asset.TextureSummary.RelativeSourceAssetPath);
				if (_imageLoader.TryGetImGuiTextureId(assetAbsolutePath, out var textureId, asset.TextureSummary.IsSrgb))
				{
					drawList.AddImage(textureId, min, max);
					return;
				}
			}
		}

		drawList.AddRect(min, max, ImGui.GetColorU32(ImGuiCol.Border));
		var label = _assetHandlerRegistry.TryGetHandler(asset.Type, out var handler)
			? handler.ThumbnailLabel
			: GetFallbackThumbnailLabel(asset.Type);
		var textSize = ImGui.CalcTextSize(label);
		var textPos = new Vector2(
			min.X + (ThumbnailSize.X - textSize.X) * 0.5f,
			min.Y + (ThumbnailSize.Y - textSize.Y) * 0.5f);
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

	private static List<AssetGroup> BuildAssetGroups(IReadOnlyList<AssetDatabaseEntry> assets)
	{
		return assets
			.GroupBy(asset => asset.SourceId)
			.Select(CreateAssetGroup)
			.OrderBy(group => group.Root.Name, StringComparer.OrdinalIgnoreCase)
			.ToList();
	}

	private static AssetGroup CreateAssetGroup(IGrouping<Guid, AssetDatabaseEntry> group)
	{
		var assets = group
			.OrderBy(GetAssetTypeSortOrder)
			.ThenBy(asset => asset.Name, StringComparer.OrdinalIgnoreCase)
			.ThenBy(asset => asset.NodeKey, StringComparer.OrdinalIgnoreCase)
			.ToList();
		var root = assets.FirstOrDefault(asset => asset.Type == AssetType.Model3D)
			?? assets.FirstOrDefault(asset => asset.IsGenerated == false && string.Equals(asset.NodeKey, "main", StringComparison.Ordinal))
			?? assets.FirstOrDefault(asset => string.Equals(asset.NodeKey, "main", StringComparison.Ordinal))
			?? assets.FirstOrDefault(asset => asset.IsGenerated == false)
			?? assets[0];
		var children = assets
			.Where(asset => asset.Id != root.Id)
			.OrderBy(GetAssetTypeSortOrder)
			.ThenBy(asset => asset.Name, StringComparer.OrdinalIgnoreCase)
			.ThenBy(asset => asset.NodeKey, StringComparer.OrdinalIgnoreCase)
			.ToList();
		return new AssetGroup(root, children);
	}

	private static int GetAssetTypeSortOrder(AssetDatabaseEntry asset)
	{
		return asset.Type switch
		{
			AssetType.Scene => 0,
			AssetType.Model3D => 1,
			AssetType.Mesh => 2,
			AssetType.Material => 3,
			AssetType.Texture2D => 4,
			AssetType.DataAsset => 5,
			_ => 10
		};
	}

	private sealed record AssetGroup(AssetDatabaseEntry Root, List<AssetDatabaseEntry> Children);

	private static string GetFallbackThumbnailLabel(AssetType assetType)
	{
		return assetType switch
		{
			AssetType.Scene => "SCN",
			AssetType.Model3D => "3D",
			_ => assetType.ToString().ToUpperInvariant()
		};
	}

	private void DrawContextMenu(EditorScene scene)
	{
		if (ImGui.BeginPopupContextWindow("AssetsContextMenu", ImGuiPopupFlags.MouseButtonRight) == false)
		{
			return;
		}

		var hasProject = _projectService.HasOpenProject;
		if (hasProject == false)
		{
			ImGui.BeginDisabled();
		}

		if (ImGui.BeginMenu("Create"))
		{
			DrawCreateMenuItems(_assetHandlerRegistry.GetCreateMenuItems());
			ImGui.EndMenu();
		}

		if (_assetSelectionService.SelectedAssetId is { } selectedAssetId
		    && _projectService.TryGetAsset(selectedAssetId, out var selectedAsset)
		    && selectedAsset.Type == AssetType.Model3D)
		{
			if (ImGui.MenuItem("Add to Scene"))
			{
				try
				{
					_assetPipelineService.InstantiateImportedModel(_projectService.ProjectRootPath!, selectedAsset.Id, scene.World);
				}
				catch (Exception ex)
				{
					ShowError($"Failed to add model to scene: {ex.Message}");
				}
			}
		}

		if (hasProject == false)
		{
			ImGui.EndDisabled();
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

	private void ShowError(string errorMessage)
	{
		_errorMessage = errorMessage;
		_openErrorPopup = true;
	}

	private void DrawCreateMenuItems(IReadOnlyList<EditorAssetCreateMenuItem> items)
	{
		for (var i = 0; i < items.Count; i++)
		{
			var item = items[i];
			if (item.Children.Count > 0)
			{
				if (ImGui.BeginMenu(item.Label))
				{
					DrawCreateMenuItems(item.Children);
					ImGui.EndMenu();
				}

				continue;
			}

			if (ImGui.MenuItem(item.Label) && item.CreateAction is not null)
			{
				HandleCreationResult(item.CreateAction());
			}
		}
	}

	private void HandleCreationResult(EditorAssetCreationResult result)
	{
		if (result.Success && result.AssetId.HasValue)
		{
			_assetSelectionService.Select(result.AssetId.Value);
		}
		else if (string.IsNullOrWhiteSpace(result.ErrorMessage) == false)
		{
			ShowError(result.ErrorMessage);
		}
	}
}

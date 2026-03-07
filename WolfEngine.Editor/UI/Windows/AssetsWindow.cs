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
	private readonly IImageLoader _imageLoader;
	private readonly IAssetSelectionService _assetSelectionService;
	private readonly IMaterialAssetCreator _materialAssetCreator;
	private string _errorMessage = string.Empty;
	private bool _openErrorPopup;

	public AssetsWindow(
		IEditorProjectService projectService,
		IImageLoader imageLoader,
		IAssetSelectionService assetSelectionService,
		IMaterialAssetCreator materialAssetCreator)
	{
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
		_imageLoader = imageLoader ?? throw new ArgumentNullException(nameof(imageLoader));
		_assetSelectionService = assetSelectionService ?? throw new ArgumentNullException(nameof(assetSelectionService));
		_materialAssetCreator = materialAssetCreator ?? throw new ArgumentNullException(nameof(materialAssetCreator));
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
			DrawContextMenu();
			DrawErrorPopup();
			ImGui.End();
			return;
		}

		ImGui.TextUnformatted(_projectService.ProjectRootPath ?? string.Empty);
		ImGui.Separator();

		var assets = _projectService.CurrentAssetDatabase.Assets
			.OrderBy(asset => asset.Name, StringComparer.OrdinalIgnoreCase)
			.ToList();
		if (assets.Count == 0)
		{
			ImGui.TextUnformatted("No assets imported yet.");
			DrawContextMenu();
			DrawErrorPopup();
			ImGui.End();
			return;
		}

		for (var i = 0; i < assets.Count; i++)
		{
			DrawAssetRow(assets[i]);
		}

		DrawContextMenu();
		DrawErrorPopup();
		ImGui.End();
	}

	private void DrawAssetRow(AssetDatabaseEntry asset)
	{
		ImGui.PushID(asset.Id.ToString());
		var isSelected = _assetSelectionService.SelectedAssetId == asset.Id;
		var rowHeight = MathF.Max(ThumbnailSize.Y + 8.0f, ImGui.GetTextLineHeightWithSpacing() * 2.0f + 8.0f);
		if (ImGui.Selectable("##AssetRow", isSelected, ImGuiSelectableFlags.None, new Vector2(ImGui.GetContentRegionAvail().X, rowHeight)))
		{
			_assetSelectionService.Select(asset.Id);
		}

		var itemMin = ImGui.GetItemRectMin();
		var drawList = ImGui.GetWindowDrawList();
		var thumbMin = itemMin + new Vector2(6.0f, (rowHeight - ThumbnailSize.Y) * 0.5f);
		var thumbMax = thumbMin + ThumbnailSize;
		var textX = thumbMax.X + 8.0f;
		var titleY = itemMin.Y + 6.0f;
		var subtitleY = titleY + ImGui.GetTextLineHeightWithSpacing();

		DrawAssetThumbnail(drawList, thumbMin, thumbMax, asset);
		drawList.AddText(new Vector2(textX, titleY), ImGui.GetColorU32(ImGuiCol.Text), asset.Name);
		drawList.AddText(new Vector2(textX, subtitleY), ImGui.GetColorU32(ImGuiCol.TextDisabled), GetAssetSubtitle(asset));

		ImGui.PopID();
	}

	private void DrawAssetThumbnail(ImDrawListPtr drawList, Vector2 min, Vector2 max, AssetDatabaseEntry asset)
	{
		if (asset.Type == AssetType.Texture2D && asset.TextureSummary is not null)
		{
			var assetAbsolutePath = _projectService.GetAbsolutePath(asset.RelativeAssetPath);
			if (_imageLoader.TryGetImGuiTextureId(assetAbsolutePath, out var textureId, asset.TextureSummary.IsSrgb))
			{
				drawList.AddImage(textureId, min, max);
				return;
			}
		}

		drawList.AddRect(min, max, ImGui.GetColorU32(ImGuiCol.Border));
		var label = asset.Type == AssetType.Material ? "MAT" : "TEX";
		var textSize = ImGui.CalcTextSize(label);
		var textPos = new Vector2(
			min.X + (ThumbnailSize.X - textSize.X) * 0.5f,
			min.Y + (ThumbnailSize.Y - textSize.Y) * 0.5f);
		drawList.AddText(textPos, ImGui.GetColorU32(ImGuiCol.TextDisabled), label);
	}

	private static string GetAssetSubtitle(AssetDatabaseEntry asset)
	{
		return asset.Type switch
		{
			AssetType.Texture2D when asset.TextureSummary is not null =>
				$"Texture | {asset.TextureSummary.Width}x{asset.TextureSummary.Height} | {asset.TextureSummary.SourceExtension}",
			AssetType.Material when asset.MaterialSummary is not null =>
				$"Material | {asset.MaterialSummary.MaterialType}",
			AssetType.Texture2D => "Texture",
			AssetType.Material => "Material",
			_ => asset.Type.ToString()
		};
	}

	private void DrawContextMenu()
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
			if (ImGui.MenuItem("Material"))
			{
				var result = _materialAssetCreator.CreateMaterial();
				if (result.Success && result.AssetId.HasValue)
				{
					_assetSelectionService.Select(result.AssetId.Value);
				}
				else if (string.IsNullOrWhiteSpace(result.ErrorMessage) == false)
				{
					ShowError(result.ErrorMessage);
				}
			}
			ImGui.EndMenu();
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
}

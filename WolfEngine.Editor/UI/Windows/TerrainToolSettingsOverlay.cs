using System.Numerics;
using ImGuiNET;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;

namespace WolfEngine.Editor.UI;

public sealed class TerrainToolSettingsOverlay
{
	private static readonly Vector2 OverlaySizeNormal = new(240.0f, 148.0f);
	private static readonly Vector2 OverlaySizeBrush = new(240.0f, 178.0f);
	private static readonly Vector2 OverlayOffset = new(16.0f, 16.0f);
	private static readonly Vector2 LayerThumbnailSize = new(24.0f, 24.0f);
	private const float LayerSelectorWidth = 180.0f;
	private readonly IEditorProjectService _projectService;
	private readonly IAssetThumbnailLoader _assetThumbnailLoader;

	public TerrainToolSettingsOverlay(
		IEditorProjectService projectService,
		IAssetThumbnailLoader assetThumbnailLoader)
	{
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
		_assetThumbnailLoader = assetThumbnailLoader ?? throw new ArgumentNullException(nameof(assetThumbnailLoader));
	}

	internal void Draw(TerrainTool terrainTool, TerrainToolSettings settings, TerrainLayerSet? layerSet, Vector2 viewportMin, Vector2 viewportMax)
	{
		var availableWidth = viewportMax.X - viewportMin.X;
		var availableHeight = viewportMax.Y - viewportMin.Y;
		if (availableWidth <= 0.0f || availableHeight <= 0.0f)
		{
			return;
		}

		var overlaySize = terrainTool == TerrainTool.Brush ? OverlaySizeBrush : OverlaySizeNormal;

		var width = MathF.Min(overlaySize.X, availableWidth - OverlayOffset.X);
		var height = MathF.Min(overlaySize.Y, availableHeight - OverlayOffset.Y);
		if (width <= 0.0f || height <= 0.0f)
		{
			return;
		}

		ImGui.SetCursorScreenPos(viewportMin + OverlayOffset);
		ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8.0f);
		ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.07f, 0.07f, 0.09f, 0.55f));
		ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(1.0f, 1.0f, 1.0f, 0.08f));

		if (ImGui.BeginChild(
			    "TerrainToolSettingsOverlay",
			    new Vector2(width, height),
			    ImGuiChildFlags.Borders,
			    ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
		{
			ImGui.TextUnformatted("Terrain Tool");
			ImGui.Separator();
			ImGui.TextUnformatted(terrainTool.ToString());
			ImGui.SliderFloat("Radius", ref settings.RadiusMeters, 1.0f, 64.0f, "%.1fm");
			ImGui.SliderFloat("Strength", ref settings.Strength, 0.01f, 1.0f, "%.2f");
			ImGui.SliderFloat("Falloff", ref settings.Falloff, 0.2f, 4.0f, "%.1f");
			if (terrainTool == TerrainTool.Brush)
			{
				DrawLayerSelector(settings, layerSet);
			}
		}

		ImGui.EndChild();
		ImGui.PopStyleColor(2);
		ImGui.PopStyleVar();
	}

	private void DrawLayerSelector(TerrainToolSettings settings, TerrainLayerSet? layerSet)
	{
		var layerCount = layerSet?.ResolvedLayerCount ?? 0;
		settings.LayerIndex = Math.Clamp(settings.LayerIndex, 0, layerCount);
		var preview = GetLayerLabel(settings.LayerIndex, layerSet);
		ImGui.SetNextItemWidth(LayerSelectorWidth);
		if (ImGui.BeginCombo("##Layer", preview))
		{
			DrawLayerOption(0, null, settings);
			for (var layerIndex = 1; layerIndex <= layerCount; layerIndex++)
			{
				DrawLayerOption(layerIndex, layerSet!.GetLayer(layerIndex - 1), settings);
			}

			ImGui.EndCombo();
		}
	}

	private void DrawLayerOption(int layerIndex, TerrainLayerDefinition? layer, TerrainToolSettings settings)
	{
		var thumbnailState = GetAlbedoThumbnailState(layer, out var textureId);
		if (thumbnailState == AssetThumbnailState.Ready)
		{
			ImGui.Image(textureId, LayerThumbnailSize);
		}
		else
		{
			ImGui.Dummy(LayerThumbnailSize);
			if (thumbnailState == AssetThumbnailState.Loading)
			{
				var min = ImGui.GetItemRectMin();
				var max = ImGui.GetItemRectMax();
				EditorGui.DrawLoadingSpinner(ImGui.GetWindowDrawList(), (min + max) * 0.5f);
			}
		}

		ImGui.SameLine();
		var selected = settings.LayerIndex == layerIndex;
		if (ImGui.Selectable($"{GetLayerLabel(layerIndex, layer)}##{layerIndex}", selected))
		{
			settings.LayerIndex = layerIndex;
		}

		if (selected)
		{
			ImGui.SetItemDefaultFocus();
		}
	}

	private AssetThumbnailState GetAlbedoThumbnailState(TerrainLayerDefinition? layer, out nint textureId)
	{
		textureId = 0;
		if (layer is null ||
		    layer.Albedo.IsValid == false ||
		    _projectService.TryGetAsset(layer.Albedo.NodeId, out var asset) == false)
		{
			return AssetThumbnailState.Unavailable;
		}

		return _assetThumbnailLoader.GetTextureThumbnailState(asset, out textureId);
	}

	private static string GetLayerLabel(int layerIndex, TerrainLayerSet? layerSet)
	{
		return layerIndex == 0
			? "Auto material"
			: GetLayerLabel(layerIndex, layerSet?.GetLayer(layerIndex - 1));
	}

	private static string GetLayerLabel(int layerIndex, TerrainLayerDefinition? layer)
	{
		if (layerIndex == 0)
		{
			return "Auto material";
		}

		return string.IsNullOrWhiteSpace(layer?.Name) ? $"Layer {layerIndex}" : layer.Name;
	}
}

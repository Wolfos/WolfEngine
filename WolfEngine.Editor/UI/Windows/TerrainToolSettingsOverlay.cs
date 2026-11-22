using System.Numerics;
using ImGuiNET;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;

namespace WolfEngine.Editor.UI;

public sealed class TerrainToolSettingsOverlay
{
	private static readonly Vector2 OverlaySizeNormal = new(240.0f, 148.0f);
	private static readonly Vector2 OverlaySizeBrush = new(240.0f, 178.0f);
	private static readonly Vector2 OverlaySizeFoliage = new(356.0f, 440.0f);
	private static readonly Vector2 OverlayOffset = new(16.0f, 16.0f);
	private static readonly Vector2 LayerThumbnailSize = new(24.0f, 24.0f);
	private const float LayerSelectorWidth = 180.0f;
	private readonly IEditorProjectService _projectService;
	private readonly IAssetThumbnailLoader _assetThumbnailLoader;
	private readonly IPropertyDrawerRegistry _propertyDrawers;

	public TerrainToolSettingsOverlay(
		IEditorProjectService projectService,
		IAssetThumbnailLoader assetThumbnailLoader,
		IPropertyDrawerRegistry propertyDrawers)
	{
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
		_assetThumbnailLoader = assetThumbnailLoader ?? throw new ArgumentNullException(nameof(assetThumbnailLoader));
		_propertyDrawers = propertyDrawers ?? throw new ArgumentNullException(nameof(propertyDrawers));
	}

	internal void Draw(
		EditorScene scene,
		TerrainTool terrainTool,
		TerrainToolController controller,
		TerrainLayerSet? layerSet,
		Vector2 viewportMin,
		Vector2 viewportMax)
	{
		var availableWidth = viewportMax.X - viewportMin.X;
		var availableHeight = viewportMax.Y - viewportMin.Y;
		if (availableWidth <= 0.0f || availableHeight <= 0.0f)
		{
			return;
		}

		var overlaySize = terrainTool switch
		{
			TerrainTool.Foliage => OverlaySizeFoliage,
			TerrainTool.Brush => OverlaySizeBrush,
			_ => OverlaySizeNormal
		};

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
			    terrainTool == TerrainTool.Foliage
				    ? ImGuiWindowFlags.None
				    : ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
		{
			if (terrainTool == TerrainTool.Foliage)
			{
				DrawFoliageSettings(scene, controller);
			}
			else
			{
				var settings = controller.Settings;
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
		}

		ImGui.EndChild();
		ImGui.PopStyleColor(2);
		ImGui.PopStyleVar();
	}

	private void DrawFoliageSettings(EditorScene scene, TerrainToolController controller)
	{
		var settings = controller.FoliageSettings;
		ImGui.SeparatorText("Foliage");
		ImGui.TextDisabled("Click or drag to paint. Hold Shift to erase.");
		ImGui.PushItemWidth(108.0f);
		ImGui.DragFloat("Radius", ref settings.RadiusMeters, 0.25f, 0.1f, 256.0f, "%.1f m", ImGuiSliderFlags.AlwaysClamp);
		ImGui.DragFloat("Density (per 100 m²)", ref settings.Density, 0.1f, 0.0f, 1000.0f, "%.1f", ImGuiSliderFlags.AlwaysClamp);
		ImGui.DragFloat("Minimum spacing", ref settings.MinimumSpacingMeters, 0.05f, 0.0f, 256.0f, "%.2f m", ImGuiSliderFlags.AlwaysClamp);
		ImGui.DragFloatRange2("Scale range", ref settings.MinScale, ref settings.MaxScale, 0.01f, 0.01f, 100.0f, "%.2f", "%.2f", ImGuiSliderFlags.AlwaysClamp);
		ImGui.SliderFloat("Normal alignment", ref settings.NormalAlignment, 0.0f, 1.0f, "%.2f", ImGuiSliderFlags.AlwaysClamp);
		ImGui.PopItemWidth();
		ImGui.Checkbox("Random yaw", ref settings.RandomYaw);

		ImGui.SeparatorText("Palette");
		DrawFoliagePalette(controller.FoliagePalette);

		ImGui.SeparatorText("Terrain");
		if (ImGui.Button("Snap to terrain"))
		{
			controller.SnapFoliageToTerrain(scene);
		}

		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip("Puts all of this terrain's foliage on its surface. Sculpting moves foliage with the ground by itself.");
		}

		if (controller.FoliageError is { } error)
		{
			ImGui.TextWrapped(error);
		}
	}

	private void DrawFoliagePalette(List<FoliagePaletteEntry> palette)
	{
		var removedIndex = -1;
		if (palette.Count > 0 && ImGui.BeginTable("FoliagePalette", 4, ImGuiTableFlags.SizingStretchProp))
		{
			ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFrameHeight());
			ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthStretch);
			ImGui.TableSetupColumn("Weight", ImGuiTableColumnFlags.WidthFixed, 64.0f);
			ImGui.TableSetupColumn("##Remove", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFrameHeight());
			ImGui.TableHeadersRow();
			for (var i = 0; i < palette.Count; i++)
			{
				var entry = palette[i];
				var enabled = entry.Enabled;
				var weight = entry.Weight;
				var name = _projectService.TryGetAsset(entry.Type.NodeId, out var asset) ? asset.Name : "Missing foliage type";
				ImGui.PushID(entry.Type.NodeId.ToString());
				ImGui.TableNextRow();

				ImGui.TableSetColumnIndex(0);
				ImGui.Checkbox("##Enabled", ref enabled);
				if (ImGui.IsItemHovered())
				{
					ImGui.SetTooltip($"Enable {name}");
				}

				ImGui.TableSetColumnIndex(1);
				ImGui.AlignTextToFramePadding();
				ImGui.TextUnformatted(name);
				if (ImGui.IsItemHovered())
				{
					ImGui.SetTooltip(name);
				}

				ImGui.TableSetColumnIndex(2);
				ImGui.SetNextItemWidth(-1.0f);
				ImGui.DragFloat("##Weight", ref weight, 0.05f, 0.0f, 1000.0f, "%.2f", ImGuiSliderFlags.AlwaysClamp);
				ImGui.TableSetColumnIndex(3);
				if (ImGui.SmallButton("X"))
				{
					removedIndex = i;
				}

				if (ImGui.IsItemHovered())
				{
					ImGui.SetTooltip($"Remove {name}");
				}

				palette[i] = entry with { Enabled = enabled, Weight = weight };
				ImGui.PopID();
			}

			ImGui.EndTable();
		}

		if (removedIndex >= 0)
		{
			palette.RemoveAt(removedIndex);
		}

		var picked = default(AssetRef<FoliageType>);
		if (_propertyDrawers.DrawAssetRef("Add type", ref picked))
		{
			AddPaletteType(palette, picked.NodeId);
		}

		ImGui.Button("Drop foliage type here", new Vector2(ImGui.GetContentRegionAvail().X, 0.0f));
		AddPaletteType(palette, FoliageAssetDragDrop.AcceptType());
	}

	private static void AddPaletteType(List<FoliagePaletteEntry> palette, Guid id)
	{
		if (id == Guid.Empty || palette.Any(entry => entry.Type.NodeId == id)) return;

		palette.Add(new FoliagePaletteEntry(new AssetRef<FoliageType> { NodeId = id }));
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

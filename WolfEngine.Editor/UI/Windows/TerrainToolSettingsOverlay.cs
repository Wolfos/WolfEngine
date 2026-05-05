using System.Numerics;
using ImGuiNET;

namespace WolfEngine.Editor.UI;

public sealed class TerrainToolSettingsOverlay
{
	private static readonly Vector2 OverlaySize = new(240.0f, 160.0f);
	private static readonly Vector2 OverlayOffset = new(16.0f, 16.0f);

	public bool BlocksPainting { get; private set; }

	internal void Draw(TerrainTool terrainTool, TerrainToolSettings settings, Vector2 viewportMin, Vector2 viewportMax)
	{
		BlocksPainting = false;

		var availableWidth = viewportMax.X - viewportMin.X;
		var availableHeight = viewportMax.Y - viewportMin.Y;
		if (availableWidth <= 0.0f || availableHeight <= 0.0f)
		{
			return;
		}

		var width = MathF.Min(OverlaySize.X, availableWidth - OverlayOffset.X);
		var height = MathF.Min(OverlaySize.Y, availableHeight - OverlayOffset.Y);
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
			var blocksPainting = ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
			ImGui.TextUnformatted("Terrain Tool");
			ImGui.Separator();
			ImGui.TextUnformatted(terrainTool.ToString());
			ImGui.SliderFloat("Radius", ref settings.RadiusMeters, 1.0f, 64.0f, "%.1fm");
			blocksPainting |= ImGui.IsItemActive();
			ImGui.SliderFloat("Strength", ref settings.Strength, 0.01f, 1.0f, "%.2f");
			blocksPainting |= ImGui.IsItemActive();
			ImGui.SliderFloat("Falloff", ref settings.Falloff, 0.2f, 4.0f, "%.1f");
			blocksPainting |= ImGui.IsItemActive();
			if (terrainTool == TerrainTool.Brush)
			{
				var displayLayer = Math.Clamp(settings.LayerIndex + 1, 1, 4);
				if (ImGui.SliderInt("Layer", ref displayLayer, 1, 4))
				{
					settings.LayerIndex = displayLayer - 1;
				}
				blocksPainting |= ImGui.IsItemActive();
			}

			BlocksPainting = blocksPainting;
		}

		ImGui.EndChild();
		ImGui.PopStyleColor(2);
		ImGui.PopStyleVar();
	}
}

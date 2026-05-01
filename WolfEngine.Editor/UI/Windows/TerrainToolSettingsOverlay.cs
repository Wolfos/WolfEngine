using System.Numerics;
using ImGuiNET;

namespace WolfEngine.Editor.UI;

public sealed class TerrainToolSettingsOverlay
{
	private static readonly Vector2 OverlaySize = new(220.0f, 72.0f);
	private static readonly Vector2 OverlayOffset = new(16.0f, 16.0f);

	internal void Draw(TerrainTool terrainTool, Vector2 viewportMin, Vector2 viewportMax)
	{
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
			ImGui.TextUnformatted("Terrain Tool");
			ImGui.Separator();
			ImGui.TextUnformatted(terrainTool.ToString());
		}

		ImGui.EndChild();
		ImGui.PopStyleColor(2);
		ImGui.PopStyleVar();
	}
}

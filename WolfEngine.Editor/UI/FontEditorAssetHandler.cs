using System.Numerics;
using ImGuiNET;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;
using WolfEngine.Importing;

namespace WolfEngine.Editor.UI;

public sealed class FontEditorAssetHandler(IEditorProjectService project, IImageLoader images) : IEditorAssetHandler
{
	public AssetType AssetType => AssetType.Font;
	public string DisplayName => "Font";
	public string ThumbnailLabel => "FONT";
	public IReadOnlyList<EditorAssetCreateMenuItem> GetCreateMenuItems() => [];
	public string GetSubtitle(AssetDatabaseEntry asset) => asset.TryGetSummary<FontAssetSummary>(out var summary)
		? $"Font | {summary.GlyphCount} glyphs | MTSDF {summary.Width}x{summary.Height}" : "Font";

	public void DrawEditor(AssetDatabaseEntry asset)
	{
		ImGui.TextUnformatted(GetSubtitle(asset));
		if (!asset.TryGetSummary<FontAssetSummary>(out var summary)) return;
		ImGui.TextWrapped($"Bake resolution: {summary.PixelsPerEm} pixels/em | Range: {summary.DistanceRange} pixels");
		ImGui.TextWrapped($"Coverage: {summary.Coverage}");
		ImGui.TextWrapped("Default font instance. Replacing the source font automatically rebuilds this preview.");
		Preview("Shaped text", "FontTextPreview", 1920f / 1152f, true);
		Preview("Distance field atlas (RGB)", "FontAtlasPreview", summary.Width / (float)summary.Height, false);

		void Preview(string label, string kind, float aspect, bool srgb)
		{
			if (!ImGui.CollapsingHeader(label, ImGuiTreeNodeFlags.DefaultOpen)) return;
			var artifact = asset.Artifacts.FirstOrDefault(a => a.Kind == kind);
			if (artifact is null) return;
			if (images.TryGetImGuiTextureId(project.GetAbsoluteAssetPath(asset.Id, artifact.RelativePath), out var texture, srgb))
			{
				var width = Math.Max(1, ImGui.GetContentRegionAvail().X);
				ImGui.Image(texture, new Vector2(width, width / aspect));
			}
			else ImGui.TextUnformatted("Preview loading...");
		}
	}
}

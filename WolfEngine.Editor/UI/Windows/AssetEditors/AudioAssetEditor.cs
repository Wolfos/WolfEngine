using ImGuiNET;
using WolfEngine.AssetPipeline;
using WolfEngine.Audio;
using WolfEngine.Editor.Projects;

namespace WolfEngine.Editor.UI;

public sealed class AudioAssetEditor
{
	private readonly IEditorProjectService _project;
	private readonly IAssetMetadataStore _metadata;
	public AudioAssetEditor(IEditorProjectService project, IAssetMetadataStore metadata) { _project = project; _metadata = metadata; }

	public void Draw(AssetDatabaseEntry asset)
	{
		if (!asset.TryGetSummary<AudioClipSummary>(out var summary)) { ImGui.TextUnformatted("Audio summary is unavailable."); return; }
		ImGui.TextUnformatted($"{summary.Codec.ToUpperInvariant()} | {summary.Channels} channel(s) | {summary.SampleRate} Hz");
		ImGui.TextUnformatted($"Duration: {TimeSpan.FromSeconds(summary.DurationSeconds):mm\\:ss\\.fff}");
		ImGui.TextUnformatted($"Cooked as: {summary.StorageMode}");
		AssetSourceMetaFile metadata;
		try { metadata = _metadata.Load(_project.GetAbsolutePath(asset.RelativeMetaPath)); }
		catch { ImGui.TextUnformatted("Failed to load audio metadata."); return; }
		var settings = metadata.GetImportSettingsOrDefault(() => new AudioImportSettings());
		var current = settings.Usage;
		EditorUIUtility.Combo("Usage", current.ToString(), () =>
		{
			foreach (var usage in Enum.GetValues<AudioUsage>())
			{
				var selected = usage == current;
				if (ImGui.Selectable(usage.ToString(), selected))
				{
					settings.Usage = usage;
					metadata.SetImportSettings(settings);
					_metadata.Save(_project.GetAbsolutePath(asset.RelativeMetaPath), metadata);
					_project.RefreshAssetSource(asset.RelativeSourcePath);
				}
				if (selected) ImGui.SetItemDefaultFocus();
			}
		});
	}
}

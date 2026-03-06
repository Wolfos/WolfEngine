using System.Numerics;
using ImGuiNET;
using WolfEngine.Editor.Projects;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor.UI;

public sealed class AssetsWindow : EditorWindow
{
	private static readonly Vector2 ThumbnailSize = new(36.0f, 36.0f);

	private readonly IEditorProjectService _projectService;
	private readonly IImageLoader _imageLoader;

	public AssetsWindow(IEditorProjectService projectService, IImageLoader imageLoader)
	{
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
		_imageLoader = imageLoader ?? throw new ArgumentNullException(nameof(imageLoader));
	}

	public override string Name => "Assets";

	public override void Draw(EditorScene scene)
	{
		ImGui.SetNextWindowPos(new Vector2(0.0f, 520.0f), ImGuiCond.FirstUseEver);
		ImGui.SetNextWindowSize(new Vector2(320.0f, 200.0f), ImGuiCond.FirstUseEver);
		Begin();

		if (_projectService.HasOpenProject == false)
		{
			ImGui.BeginDisabled();
			ImGui.TextUnformatted("No project open.");
			ImGui.EndDisabled();
			ImGui.End();
			return;
		}

		ImGui.TextUnformatted(_projectService.ProjectRootPath ?? string.Empty);
		ImGui.Separator();

		var assets = _projectService.CurrentAssetDatabase.Assets
			.Where(asset => asset.Type == AssetPipeline.AssetType.Texture2D)
			.OrderBy(asset => asset.Name, StringComparer.OrdinalIgnoreCase)
			.ToList();
		if (assets.Count == 0)
		{
			ImGui.TextUnformatted("No texture assets imported yet.");
			ImGui.End();
			return;
		}

		foreach (var asset in assets)
		{
			DrawAssetRow(asset);
		}

		ImGui.End();
	}

	private void DrawAssetRow(AssetPipeline.AssetDatabaseEntry asset)
	{
		ImGui.PushID(asset.Id.ToString());
		var assetAbsolutePath = _projectService.GetAbsolutePath(asset.RelativeAssetPath);
		var hasTexture = _imageLoader.TryGetImGuiTextureId(assetAbsolutePath, out var textureId, asset.IsSrgb);
		if (hasTexture)
		{
			ImGui.Image(textureId, ThumbnailSize);
		}
		else
		{
			ImGui.BeginChild("thumbnail", ThumbnailSize, ImGuiChildFlags.Borders, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
			ImGui.TextUnformatted("N/A");
			ImGui.EndChild();
		}

		ImGui.SameLine();
		ImGui.BeginGroup();
		ImGui.TextUnformatted(Path.GetFileName(asset.RelativeAssetPath));
		ImGui.TextDisabled($"{asset.Width}x{asset.Height} | {asset.SourceExtension}");
		ImGui.EndGroup();
		ImGui.Separator();
		ImGui.PopID();
	}
}

using System.Numerics;
using ImGuiNET;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor.UI;

public sealed class TextureAssetEditor
{
	private static readonly int[] ResolutionOptions = [16, 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192];
	private static readonly Vector2 PreviewSize = new(96.0f, 96.0f);

	private readonly IEditorProjectService _projectService;
	private readonly IImageLoader _imageLoader;
	private readonly ITextureAssetMetaStore _textureAssetMetaStore;
	private Guid? _loadedTextureAssetId;
	private TextureAssetMetaFile? _loadedTextureMeta;

	public TextureAssetEditor(
		IEditorProjectService projectService,
		IImageLoader imageLoader,
		ITextureAssetMetaStore textureAssetMetaStore)
	{
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
		_imageLoader = imageLoader ?? throw new ArgumentNullException(nameof(imageLoader));
		_textureAssetMetaStore = textureAssetMetaStore ?? throw new ArgumentNullException(nameof(textureAssetMetaStore));
	}

	public void Draw(AssetDatabaseEntry asset)
	{
		var meta = EnsureTextureMetaLoaded(asset);
		if (meta is null)
		{
			ImGui.TextUnformatted("Failed to load texture metadata.");
			return;
		}

		var absoluteAssetPath = _projectService.GetAbsolutePath(asset.RelativeAssetPath);
		if (_imageLoader.TryGetImGuiTextureId(absoluteAssetPath, out var textureId, meta.ImportSettings.IsSrgb))
		{
			ImGui.Image(textureId, PreviewSize);
		}
		else
		{
			ImGui.BeginChild("TexturePreview", PreviewSize, ImGuiChildFlags.Borders, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
			ImGui.TextUnformatted("Preview unavailable");
			ImGui.EndChild();
		}

		ImGui.Spacing();
		ImGui.TextUnformatted($"Imported: {meta.Summary.Width}x{meta.Summary.Height}, {meta.Summary.Channels} channel(s)");
		ImGui.TextUnformatted($"Color Space: {(meta.ImportSettings.IsSrgb ? "sRGB" : "Linear")}");

		var currentResolution = meta.ImportSettings.MaxResolution;
		var selectedIndex = Array.IndexOf(ResolutionOptions, currentResolution);
		if (selectedIndex < 0)
		{
			selectedIndex = ResolutionOptions.Length - 1;
		}

		EditorUIUtility.Combo("Import Resolution", FormatResolutionLabel(ResolutionOptions[selectedIndex]), () =>
		{
			for (var i = 0; i < ResolutionOptions.Length; i++)
			{
				var resolution = ResolutionOptions[i];
				var isSelected = resolution == currentResolution;
				if (ImGui.Selectable(FormatResolutionLabel(resolution), isSelected))
				{
					meta.ImportSettings.MaxResolution = resolution;
					SaveTextureMeta(asset, meta);
				}

				if (isSelected)
				{
					ImGui.SetItemDefaultFocus();
				}
			}
		});
	}

	private TextureAssetMetaFile? EnsureTextureMetaLoaded(AssetDatabaseEntry asset)
	{
		if (_loadedTextureAssetId == asset.Id && _loadedTextureMeta is not null)
		{
			return _loadedTextureMeta;
		}

		try
		{
			_loadedTextureAssetId = asset.Id;
			_loadedTextureMeta = _textureAssetMetaStore.Load(_projectService.GetAbsolutePath(asset.RelativeMetaPath));
			return _loadedTextureMeta;
		}
		catch
		{
			_loadedTextureAssetId = asset.Id;
			_loadedTextureMeta = null;
			return null;
		}
	}

	private void SaveTextureMeta(AssetDatabaseEntry asset, TextureAssetMetaFile meta)
	{
		_textureAssetMetaStore.Save(_projectService.GetAbsolutePath(asset.RelativeMetaPath), meta);
		_loadedTextureMeta = meta;
	}

	private static string FormatResolutionLabel(int resolution)
	{
		return $"{resolution}x{resolution}";
	}
}

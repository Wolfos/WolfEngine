using System.Numerics;
using ImGuiNET;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;
using WolfEngine.Importing;

namespace WolfEngine.Editor.UI;

public sealed class TextureAssetEditor
{
	private static readonly int[] ResolutionOptions = [16, 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192];
	private static readonly Vector2 PreviewSize = new(96.0f, 96.0f);

	private readonly IEditorProjectService _projectService;
	private readonly IImageLoader _imageLoader;
	private readonly IAssetMetadataStore _metadataStore;
	private AssetSourceMetaFile? _loadedMetadata;
	private Guid? _loadedTextureAssetId;

	public TextureAssetEditor(
		IEditorProjectService projectService,
		IImageLoader imageLoader,
		IAssetMetadataStore metadataStore)
	{
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
		_imageLoader = imageLoader ?? throw new ArgumentNullException(nameof(imageLoader));
		_metadataStore = metadataStore ?? throw new ArgumentNullException(nameof(metadataStore));
	}

	public void Draw(AssetDatabaseEntry asset)
	{
		if (asset.TextureSummary is null)
		{
			ImGui.TextUnformatted("Texture summary is unavailable.");
			return;
		}

		var metadata = asset.IsGenerated ? null : EnsureMetadataLoaded(asset);
		var previewIsSrgb = metadata?.TextureImportSettings is { } importSettings
			? StbImageLoader.IsSrgb(importSettings.TextureSemantic)
			: StbImageLoader.IsSrgb(asset.TextureSummary.Semantic);
		var previewRelativePath = asset.IsGenerated ? string.Empty : asset.TextureSummary.RelativeSourceAssetPath;
		if (string.IsNullOrWhiteSpace(previewRelativePath) == false &&
		    _imageLoader.TryGetImGuiTextureId(_projectService.GetAbsolutePath(previewRelativePath), out var textureId, previewIsSrgb))
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
		ImGui.TextUnformatted($"Imported: {asset.TextureSummary.Width}x{asset.TextureSummary.Height}, {asset.TextureSummary.Channels} channel(s)");
		ImGui.TextUnformatted($"Color Space: {(StbImageLoader.IsSrgb(asset.TextureSummary.Semantic) ? "sRGB" : "Linear")}");

		if (asset.IsGenerated)
		{
			ImGui.TextDisabled("Generated texture nodes are read-only.");
			return;
		}

		if (metadata is null)
		{
			ImGui.TextUnformatted("Failed to load texture metadata.");
			return;
		}

		metadata.TextureImportSettings ??= new TextureImportSettings();
		var currentSemantic = metadata.TextureImportSettings.TextureSemantic;
		var currentResolution = metadata.TextureImportSettings.MaxResolution;
		var selectedIndex = Array.IndexOf(ResolutionOptions, currentResolution);
		if (selectedIndex < 0)
		{
			selectedIndex = ResolutionOptions.Length - 1;
		}

		EditorUIUtility.Combo("Texture Semantic", FormatSemanticLabel(currentSemantic), () =>
		{
			foreach (TextureSemantic semantic in Enum.GetValues<TextureSemantic>())
			{
				var isSelected = semantic == currentSemantic;
				if (ImGui.Selectable(FormatSemanticLabel(semantic), isSelected))
				{
					metadata.TextureImportSettings.TextureSemantic = semantic;
					SaveTextureMetadata(asset, metadata);
				}

				if (isSelected)
				{
					ImGui.SetItemDefaultFocus();
				}
			}
		});

		EditorUIUtility.Combo("Import Resolution", FormatResolutionLabel(ResolutionOptions[selectedIndex]), () =>
		{
			for (var i = 0; i < ResolutionOptions.Length; i++)
			{
				var resolution = ResolutionOptions[i];
				var isSelected = resolution == currentResolution;
				if (ImGui.Selectable(FormatResolutionLabel(resolution), isSelected))
				{
					metadata.TextureImportSettings.MaxResolution = resolution;
					SaveTextureMetadata(asset, metadata);
				}

				if (isSelected)
				{
					ImGui.SetItemDefaultFocus();
				}
			}
		});
	}

	private AssetSourceMetaFile? EnsureMetadataLoaded(AssetDatabaseEntry asset)
	{
		if (_loadedTextureAssetId == asset.Id && _loadedMetadata is not null)
		{
			return _loadedMetadata;
		}

		try
		{
			_loadedTextureAssetId = asset.Id;
			_loadedMetadata = _metadataStore.Load(_projectService.GetAbsolutePath(asset.RelativeMetaPath));
			return _loadedMetadata;
		}
		catch
		{
			_loadedTextureAssetId = asset.Id;
			_loadedMetadata = null;
			return null;
		}
	}

	private void SaveTextureMetadata(AssetDatabaseEntry asset, AssetSourceMetaFile metadata)
	{
		_metadataStore.Save(_projectService.GetAbsolutePath(asset.RelativeMetaPath), metadata);
		_loadedMetadata = metadata;
		_loadedTextureAssetId = asset.Id;
		_projectService.RefreshAssetSource(asset.RelativeSourcePath);
	}

	private static string FormatResolutionLabel(int resolution)
	{
		return $"{resolution}x{resolution}";
	}

	private static string FormatSemanticLabel(TextureSemantic semantic)
	{
		return semantic switch
		{
			TextureSemantic.BaseColor => "Base Color",
			TextureSemantic.BaseColorTransparent => "Base Color Transparent",
			TextureSemantic.MetallicRoughness => "Metallic Roughness",
			_ => semantic.ToString()
		};
	}
}

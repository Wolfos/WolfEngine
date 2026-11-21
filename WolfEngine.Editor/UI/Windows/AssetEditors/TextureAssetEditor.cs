using System.Numerics;
using ImGuiNET;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;
using WolfEngine.Importing;

namespace WolfEngine.Editor.UI;

public sealed class TextureAssetEditor
{
	private static readonly int[] ResolutionOptions = [16, 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192];

	/// <summary>Cube face sizes offered in the inspector; zero derives the size from the source.</summary>
	private static readonly int[] CubemapFaceSizeOptions = [0, 64, 128, 256, 512, 1024, 2048, 4096];
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
		if (asset.TryGetSummary<TextureAssetSummary>(out var textureSummary) == false)
		{
			ImGui.TextUnformatted("Texture summary is unavailable.");
			return;
		}

		var metadata = asset.IsGenerated ? null : EnsureMetadataLoaded(asset);
		var previewIsSrgb = metadata?.TryGetImportSettings<TextureImportSettings>(out var importSettings) == true
			? StbImageLoader.IsSrgb(importSettings.TextureSemantic)
			: StbImageLoader.IsSrgb(textureSummary.Semantic);
		var previewRelativePath = asset.IsGenerated ? string.Empty : textureSummary.RelativeSourceAssetPath;
		if (string.IsNullOrWhiteSpace(previewRelativePath) == false &&
		    _imageLoader.TryGetImGuiTextureId(
			    _projectService.GetAbsoluteAssetPath(asset.Id, previewRelativePath), out var textureId, previewIsSrgb))
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
		var shapeLabel = textureSummary.Dimension == Rendering.TextureDimension.Cube
			? $"cube, {textureSummary.Width}x{textureSummary.Height} faces"
			: $"{textureSummary.Width}x{textureSummary.Height}";
		ImGui.TextUnformatted($"Imported: {shapeLabel}, {textureSummary.Channels} channel(s)");
		ImGui.TextUnformatted($"Color Space: {FormatColorSpaceLabel(textureSummary)}");

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

		var textureImportSettings = metadata.GetImportSettingsOrDefault(() => new TextureImportSettings());
		var currentSemantic = textureImportSettings.TextureSemantic;
		var currentResolution = textureImportSettings.MaxResolution;
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
					textureImportSettings.TextureSemantic = semantic;
					metadata.SetImportSettings(textureImportSettings);
					SaveTextureMetadata(asset, metadata);
				}

				if (isSelected)
				{
					ImGui.SetItemDefaultFocus();
				}
			}
		});

		var currentShape = textureImportSettings.Shape;
		EditorUIUtility.Combo("Shape", FormatShapeLabel(currentShape), () =>
		{
			foreach (TextureShape shape in Enum.GetValues<TextureShape>())
			{
				var isSelected = shape == currentShape;
				if (ImGui.Selectable(FormatShapeLabel(shape), isSelected))
				{
					textureImportSettings.Shape = shape;
					metadata.SetImportSettings(textureImportSettings);
					SaveTextureMetadata(asset, metadata);
				}

				if (isSelected)
				{
					ImGui.SetItemDefaultFocus();
				}
			}
		});

		if (currentShape == TextureShape.Cube)
		{
			var currentFaceSize = textureImportSettings.CubemapFaceSize;
			EditorUIUtility.Combo("Cube Face Size", FormatFaceSizeLabel(currentFaceSize), () =>
			{
				for (var i = 0; i < CubemapFaceSizeOptions.Length; i++)
				{
					var faceSize = CubemapFaceSizeOptions[i];
					var isSelected = faceSize == currentFaceSize;
					if (ImGui.Selectable(FormatFaceSizeLabel(faceSize), isSelected))
					{
						textureImportSettings.CubemapFaceSize = faceSize;
						metadata.SetImportSettings(textureImportSettings);
						SaveTextureMetadata(asset, metadata);
					}

					if (isSelected)
					{
						ImGui.SetItemDefaultFocus();
					}
				}
			});
		}

		EditorUIUtility.Combo("Import Resolution", FormatResolutionLabel(ResolutionOptions[selectedIndex]), () =>
		{
			for (var i = 0; i < ResolutionOptions.Length; i++)
			{
				var resolution = ResolutionOptions[i];
				var isSelected = resolution == currentResolution;
				if (ImGui.Selectable(FormatResolutionLabel(resolution), isSelected))
				{
					textureImportSettings.MaxResolution = resolution;
					metadata.SetImportSettings(textureImportSettings);
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
			_loadedMetadata = _metadataStore.Load(
				_projectService.GetAbsoluteAssetPath(asset.Id, asset.RelativeMetaPath));
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
		if (_projectService.IsAssetReadOnly(asset.Id))
			throw new InvalidOperationException($"Asset '{asset.Id}' belongs to a read-only mount.");
		_metadataStore.Save(_projectService.GetAbsoluteAssetPath(asset.Id, asset.RelativeMetaPath), metadata);
		_loadedMetadata = metadata;
		_loadedTextureAssetId = asset.Id;
		_projectService.RefreshAssetSource(asset.RelativeSourcePath);
	}

	private static string FormatResolutionLabel(int resolution)
	{
		return $"{resolution}x{resolution}";
	}

	private static string FormatFaceSizeLabel(int faceSize)
	{
		return faceSize <= 0
			? "Automatic"
			: $"{faceSize}x{faceSize}";
	}

	private static string FormatShapeLabel(TextureShape shape)
	{
		return shape switch
		{
			TextureShape.Texture2D => "2D",
			TextureShape.Cube => "Cube",
			_ => shape.ToString()
		};
	}

	private static string FormatColorSpaceLabel(TextureAssetSummary summary)
	{
		if (summary.IsHdr)
		{
			return "Linear HDR";
		}

		return StbImageLoader.IsSrgb(summary.Semantic)
			? "sRGB"
			: "Linear";
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

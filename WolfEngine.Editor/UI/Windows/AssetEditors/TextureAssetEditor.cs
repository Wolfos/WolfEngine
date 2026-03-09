using System.Numerics;
using ImGuiNET;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;
using WolfEngine.Rendering;
using WolfEngine.Rendering.UI;
using ImportImageLoader = WolfEngine.Importing.IImageLoader;
using WolfEngine.Importing;

namespace WolfEngine.Editor.UI;

public sealed class TextureAssetEditor
{
	private static readonly int[] ResolutionOptions = [16, 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192];
	private static readonly Vector2 PreviewSize = new(96.0f, 96.0f);

	private readonly IEditorProjectService _projectService;
	private readonly IImageLoader _imageLoader;
	private readonly ImportImageLoader _importImageLoader;
	private readonly ITextureAssetStore _textureAssetStore;
	private readonly RenderGraph _renderGraph;
	private Guid? _loadedTextureAssetId;
	private TextureAsset? _loadedTextureAsset;
	private TextureAssetStateFile? _loadedTextureState;

	public TextureAssetEditor(
		IEditorProjectService projectService,
		IImageLoader imageLoader,
		ImportImageLoader importImageLoader,
		ITextureAssetStore textureAssetStore,
		RenderGraph renderGraph)
	{
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
		_imageLoader = imageLoader ?? throw new ArgumentNullException(nameof(imageLoader));
		_importImageLoader = importImageLoader ?? throw new ArgumentNullException(nameof(importImageLoader));
		_textureAssetStore = textureAssetStore ?? throw new ArgumentNullException(nameof(textureAssetStore));
		_renderGraph = renderGraph ?? throw new ArgumentNullException(nameof(renderGraph));
	}

	public void Draw(AssetDatabaseEntry asset)
	{
		var textureAsset = EnsureTextureAssetLoaded(asset);
		var textureState = EnsureTextureStateLoaded(asset);
		if (textureAsset is null || textureState is null)
		{
			ImGui.TextUnformatted("Failed to load texture asset.");
			return;
		}

		var absoluteSourcePath = _projectService.GetAbsolutePath(textureAsset.RelativeSourceAssetPath);
		if (_imageLoader.TryGetImGuiTextureId(absoluteSourcePath, out var textureId, textureAsset.ImportSettings.IsSrgb))
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
		ImGui.TextUnformatted($"Imported: {textureState.Summary.Width}x{textureState.Summary.Height}, {textureState.Summary.Channels} channel(s)");
		ImGui.TextUnformatted($"Color Space: {(textureAsset.ImportSettings.IsSrgb ? "sRGB" : "Linear")}");

		var currentResolution = textureAsset.ImportSettings.MaxResolution;
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
					textureAsset.ImportSettings.MaxResolution = resolution;
					SaveTextureAsset(asset, textureAsset, textureState);
				}

				if (isSelected)
				{
					ImGui.SetItemDefaultFocus();
				}
			}
		});
	}

	private TextureAsset? EnsureTextureAssetLoaded(AssetDatabaseEntry asset)
	{
		if (_loadedTextureAssetId == asset.Id && _loadedTextureAsset is not null)
		{
			return _loadedTextureAsset;
		}

		try
		{
			_loadedTextureAssetId = asset.Id;
			_loadedTextureAsset = _textureAssetStore.LoadAsset(_projectService.GetAbsolutePath(asset.RelativeAssetPath));
			return _loadedTextureAsset;
		}
		catch
		{
			_loadedTextureAssetId = asset.Id;
			_loadedTextureAsset = null;
			return null;
		}
	}

	private TextureAssetStateFile? EnsureTextureStateLoaded(AssetDatabaseEntry asset)
	{
		if (_loadedTextureAssetId == asset.Id && _loadedTextureState is not null)
		{
			return _loadedTextureState;
		}

		try
		{
			_loadedTextureAssetId = asset.Id;
			_loadedTextureState = _textureAssetStore.LoadState(_projectService.GetAbsolutePath(asset.GetEffectiveRelativeStatePath()));
			return _loadedTextureState;
		}
		catch
		{
			_loadedTextureAssetId = asset.Id;
			_loadedTextureState = null;
			return null;
		}
	}

	private void SaveTextureAsset(AssetDatabaseEntry asset, TextureAsset textureAsset, TextureAssetStateFile textureState)
	{
		var sourceAbsolutePath = _projectService.GetAbsolutePath(textureAsset.RelativeSourceAssetPath);
		var semantic = textureAsset.ImportSettings.IsSrgb ? TextureSemantic.BaseColor : TextureSemantic.Unknown;
		var importedTexture = _importImageLoader.Load(sourceAbsolutePath, semantic);

		textureState.Summary = new TextureAssetSummary
		{
			RelativeSourceAssetPath = textureAsset.RelativeSourceAssetPath,
			RelativeRawImagePath = _textureAssetStore.GetRuntimeArtifactRelativePath(asset.Id),
			Width = importedTexture.Width,
			Height = importedTexture.Height,
			Channels = importedTexture.Channels,
			IsSrgb = importedTexture.IsSrgb,
			SourceExtension = Path.GetExtension(sourceAbsolutePath).ToLowerInvariant()
		};
		textureState.Artifacts = _textureAssetStore.CreateDefaultRuntimeArtifacts(asset.Id).ToList();

		_textureAssetStore.SaveAsset(_projectService.GetAbsolutePath(asset.RelativeAssetPath), textureAsset);
		var relativeStatePath = _textureAssetStore.GetStateRelativePath(asset.Id);
		_textureAssetStore.SaveState(_projectService.GetAbsolutePath(relativeStatePath), textureState);
		TextureRawImageSerializer.Write(
			_projectService.GetAbsolutePath(_textureAssetStore.GetRuntimeArtifactRelativePath(asset.Id)),
			importedTexture);
		SynchronizeRuntimeTexture(asset.Id, importedTexture);

		_loadedTextureAsset = textureAsset;
		_loadedTextureState = textureState;

		var updatedDatabase = _projectService.CloneCurrentAssetDatabase();
		for (var i = 0; i < updatedDatabase.Assets.Count; i++)
		{
			if (updatedDatabase.Assets[i].Id != asset.Id)
			{
				continue;
			}

			updatedDatabase.Assets[i].RelativeStatePath = relativeStatePath;
			updatedDatabase.Assets[i].RelativeMetaPath = relativeStatePath;
			updatedDatabase.Assets[i].TextureSummary = new TextureAssetSummary
			{
				RelativeSourceAssetPath = textureState.Summary.RelativeSourceAssetPath,
				RelativeRawImagePath = textureState.Summary.RelativeRawImagePath,
				Width = textureState.Summary.Width,
				Height = textureState.Summary.Height,
				Channels = textureState.Summary.Channels,
				IsSrgb = textureState.Summary.IsSrgb,
				SourceExtension = textureState.Summary.SourceExtension
			};
			break;
		}

		_projectService.SaveAssetDatabase(updatedDatabase);
	}

	private void SynchronizeRuntimeTexture(Guid assetId, ImportedTexture importedTexture)
	{
		var runtimeTexture = AssetDatabase.GetInstance<Texture>(assetId);
		if (runtimeTexture is null)
		{
			return;
		}

		runtimeTexture.ApplyImportedTexture(
			importedTexture.Width,
			importedTexture.Height,
			importedTexture.IsSrgb,
			importedTexture.PixelData ?? throw new InvalidOperationException("Imported texture pixel data is missing."));
		_renderGraph.EnsureTextureResources(runtimeTexture);
	}

	private static string FormatResolutionLabel(int resolution)
	{
		return $"{resolution}x{resolution}";
	}
}

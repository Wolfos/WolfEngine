using System.Numerics;
using ImGuiNET;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor.UI;

public sealed class AssetEditorWindow : EditorWindow
{
	private static readonly int[] ResolutionOptions = [16, 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192];
	private static readonly Vector2 PreviewSize = new(96.0f, 96.0f);

	private readonly IEditorProjectService _projectService;
	private readonly IAssetSelectionService _assetSelectionService;
	private readonly IImageLoader _imageLoader;
	private readonly ITextureAssetMetaStore _textureAssetMetaStore;
	private readonly IMaterialAssetStore _materialAssetStore;
	private readonly IMaterialTypeRegistry _materialTypeRegistry;

	private Guid? _loadedTextureAssetId;
	private TextureAssetMetaFile? _loadedTextureMeta;
	private Guid? _loadedMaterialAssetId;
	private MaterialAssetFile? _loadedMaterialAsset;
	private MaterialMetaFile? _loadedMaterialMeta;

	public AssetEditorWindow(
		IEditorProjectService projectService,
		IAssetSelectionService assetSelectionService,
		IImageLoader imageLoader,
		ITextureAssetMetaStore textureAssetMetaStore,
		IMaterialAssetStore materialAssetStore,
		IMaterialTypeRegistry materialTypeRegistry)
	{
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
		_assetSelectionService = assetSelectionService ?? throw new ArgumentNullException(nameof(assetSelectionService));
		_imageLoader = imageLoader ?? throw new ArgumentNullException(nameof(imageLoader));
		_textureAssetMetaStore = textureAssetMetaStore ?? throw new ArgumentNullException(nameof(textureAssetMetaStore));
		_materialAssetStore = materialAssetStore ?? throw new ArgumentNullException(nameof(materialAssetStore));
		_materialTypeRegistry = materialTypeRegistry ?? throw new ArgumentNullException(nameof(materialTypeRegistry));
	}

	public override string Name => "Asset Editor";

	public override void Draw(EditorScene scene)
	{
		ImGui.SetNextWindowPos(new Vector2(860.0f, 420.0f), ImGuiCond.FirstUseEver);
		ImGui.SetNextWindowSize(new Vector2(420.0f, 300.0f), ImGuiCond.FirstUseEver);
		Begin();

		if (_projectService.HasOpenProject == false)
		{
			ImGui.TextUnformatted("No project open.");
			ImGui.End();
			return;
		}

		var selectedAssetId = _assetSelectionService.SelectedAssetId;
		if (selectedAssetId.HasValue == false)
		{
			ImGui.TextUnformatted("Select an asset in the Assets window to edit it.");
			ImGui.End();
			return;
		}

		if (_projectService.TryGetAsset(selectedAssetId.Value, out var asset) == false)
		{
			ImGui.TextUnformatted("Selected asset no longer exists in the current project.");
			ImGui.End();
			return;
		}

		ImGui.TextUnformatted(asset.Name);
		ImGui.TextDisabled(asset.RelativeAssetPath);
		ImGui.Separator();

		switch (asset.Type)
		{
			case AssetType.Texture2D:
				DrawTextureEditor(asset);
				break;
			case AssetType.Material:
				DrawMaterialEditor(asset);
				break;
			default:
				ImGui.TextUnformatted($"No editor available for asset type '{asset.Type}'.");
				break;
		}

		ImGui.End();
	}

	private void DrawTextureEditor(AssetDatabaseEntry asset)
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

		if (ImGui.BeginCombo("Import Resolution", FormatResolutionLabel(ResolutionOptions[selectedIndex])))
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

			ImGui.EndCombo();
		}
	}

	private void DrawMaterialEditor(AssetDatabaseEntry asset)
	{
		var materialAsset = EnsureMaterialAssetLoaded(asset);
		var materialMeta = EnsureMaterialMetaLoaded(asset);
		if (materialAsset is null || materialMeta is null)
		{
			ImGui.TextUnformatted("Failed to load material asset.");
			return;
		}

		var descriptors = _materialTypeRegistry.GetAll();
		if (ImGui.BeginCombo("Material Type", materialAsset.MaterialType.ToString()))
		{
			for (var i = 0; i < descriptors.Count; i++)
			{
				var descriptor = descriptors[i];
				var isSelected = descriptor.Type == materialAsset.MaterialType;
				if (ImGui.Selectable(descriptor.DisplayName, isSelected))
				{
					materialAsset.MaterialType = descriptor.Type;
					materialMeta.MaterialType = descriptor.Type;
					SaveMaterialAsset(asset, materialAsset, materialMeta);
				}

				if (isSelected)
				{
					ImGui.SetItemDefaultFocus();
				}
			}

			ImGui.EndCombo();
		}

		var properties = materialAsset.GetActiveProperties();
		var propertyDefinitions = _materialTypeRegistry.GetPropertiesForMaterialType(materialAsset.MaterialType);
		DrawBaseColorEditor(asset, materialAsset, materialMeta, properties);
		DrawFloatEditor("Metallic", properties.MetallicFactor, value =>
		{
			properties.MetallicFactor = value;
			SaveMaterialAsset(asset, materialAsset, materialMeta);
		});
		DrawFloatEditor("Roughness", properties.RoughnessFactor, value =>
		{
			properties.RoughnessFactor = value;
			SaveMaterialAsset(asset, materialAsset, materialMeta);
		});

		if (HasProperty(propertyDefinitions, MaterialPropertyKind.AlphaCutoff))
		{
			var alphaCutoff = properties switch
			{
				AlphaTestMaterialProperties alphaTest => alphaTest.AlphaCutoff,
				AlphaBlendMaterialProperties alphaBlend => alphaBlend.AlphaCutoff,
				_ => 0.5f
			};
			DrawFloatEditor("Alpha Cutoff", alphaCutoff, value =>
			{
				if (properties is AlphaTestMaterialProperties alphaTest)
				{
					alphaTest.AlphaCutoff = value;
				}
				else if (properties is AlphaBlendMaterialProperties alphaBlend)
				{
					alphaBlend.AlphaCutoff = value;
				}

				SaveMaterialAsset(asset, materialAsset, materialMeta);
			});
		}

		ImGui.Separator();
		ImGui.TextUnformatted("Textures");
		DrawTextureAssignmentCombo(asset, materialAsset, materialMeta, properties.Textures, nameof(MaterialTextureAssignments.Albedo), "Albedo", properties.Textures.Albedo);
		DrawTextureAssignmentCombo(asset, materialAsset, materialMeta, properties.Textures, nameof(MaterialTextureAssignments.MetallicRoughness), "Metallic / Roughness", properties.Textures.MetallicRoughness);
		DrawTextureAssignmentCombo(asset, materialAsset, materialMeta, properties.Textures, nameof(MaterialTextureAssignments.Normal), "Normal", properties.Textures.Normal);
		DrawTextureAssignmentCombo(asset, materialAsset, materialMeta, properties.Textures, nameof(MaterialTextureAssignments.Emissive), "Emissive", properties.Textures.Emissive);
		DrawTextureAssignmentCombo(asset, materialAsset, materialMeta, properties.Textures, nameof(MaterialTextureAssignments.Occlusion), "Occlusion", properties.Textures.Occlusion);
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

	private MaterialAssetFile? EnsureMaterialAssetLoaded(AssetDatabaseEntry asset)
	{
		if (_loadedMaterialAssetId == asset.Id && _loadedMaterialAsset is not null)
		{
			return _loadedMaterialAsset;
		}

		try
		{
			_loadedMaterialAssetId = asset.Id;
			_loadedMaterialAsset = _materialAssetStore.LoadAsset(_projectService.GetAbsolutePath(asset.RelativeAssetPath));
			return _loadedMaterialAsset;
		}
		catch
		{
			_loadedMaterialAssetId = asset.Id;
			_loadedMaterialAsset = null;
			return null;
		}
	}

	private MaterialMetaFile? EnsureMaterialMetaLoaded(AssetDatabaseEntry asset)
	{
		if (_loadedMaterialAssetId == asset.Id && _loadedMaterialMeta is not null)
		{
			return _loadedMaterialMeta;
		}

		try
		{
			_loadedMaterialAssetId = asset.Id;
			_loadedMaterialMeta = _materialAssetStore.LoadMeta(_projectService.GetAbsolutePath(asset.RelativeMetaPath));
			return _loadedMaterialMeta;
		}
		catch
		{
			_loadedMaterialAssetId = asset.Id;
			_loadedMaterialMeta = null;
			return null;
		}
	}

	private void SaveTextureMeta(AssetDatabaseEntry asset, TextureAssetMetaFile meta)
	{
		_textureAssetMetaStore.Save(_projectService.GetAbsolutePath(asset.RelativeMetaPath), meta);
		_loadedTextureMeta = meta;
	}

	private void SaveMaterialAsset(AssetDatabaseEntry asset, MaterialAssetFile materialAsset, MaterialMetaFile materialMeta)
	{
		_materialAssetStore.SaveAsset(_projectService.GetAbsolutePath(asset.RelativeAssetPath), materialAsset);
		_materialAssetStore.SaveMeta(_projectService.GetAbsolutePath(asset.RelativeMetaPath), materialMeta);
		_loadedMaterialAsset = materialAsset;
		_loadedMaterialMeta = materialMeta;

		var updatedDatabase = _projectService.CloneCurrentAssetDatabase();
		for (var i = 0; i < updatedDatabase.Assets.Count; i++)
		{
			if (updatedDatabase.Assets[i].Id != asset.Id)
			{
				continue;
			}

			updatedDatabase.Assets[i].MaterialSummary ??= new MaterialAssetSummary();
			updatedDatabase.Assets[i].MaterialSummary!.MaterialType = materialAsset.MaterialType;
			break;
		}

		_projectService.SaveAssetDatabase(updatedDatabase);
	}

	private void DrawBaseColorEditor(
		AssetDatabaseEntry asset,
		MaterialAssetFile materialAsset,
		MaterialMetaFile materialMeta,
		MaterialSurfaceProperties properties)
	{
		var color = properties.BaseColor.ToVector4();
		if (ImGui.ColorEdit4("Base Color", ref color))
		{
			properties.BaseColor = ColorRgba.FromVector4(color);
			SaveMaterialAsset(asset, materialAsset, materialMeta);
		}
	}

	private void DrawFloatEditor(string label, float currentValue, Action<float> setter)
	{
		var value = currentValue;
		if (ImGui.InputFloat(label, ref value))
		{
			setter(value);
		}
	}

	private void DrawTextureAssignmentCombo(
		AssetDatabaseEntry materialEntry,
		MaterialAssetFile materialAsset,
		MaterialMetaFile materialMeta,
		MaterialTextureAssignments assignments,
		string propertyName,
		string label,
		Guid? currentValue)
	{
		var textures = _projectService.CurrentAssetDatabase.Assets
			.Where(asset => asset.Type == AssetType.Texture2D)
			.OrderBy(asset => asset.Name, StringComparer.OrdinalIgnoreCase)
			.ToList();

		var previewLabel = currentValue.HasValue && _projectService.TryGetAsset(currentValue.Value, out var selectedTexture)
			? selectedTexture.Name
			: "None";

		if (ImGui.BeginCombo(label, previewLabel) == false)
		{
			return;
		}

		var noneSelected = currentValue.HasValue == false;
		if (ImGui.Selectable("None", noneSelected))
		{
			SetTextureAssignment(assignments, propertyName, null);
			SaveMaterialAsset(materialEntry, materialAsset, materialMeta);
		}
		if (noneSelected)
		{
			ImGui.SetItemDefaultFocus();
		}

		for (var i = 0; i < textures.Count; i++)
		{
			var textureAsset = textures[i];
			var isSelected = currentValue == textureAsset.Id;
			if (ImGui.Selectable(textureAsset.Name, isSelected))
			{
				SetTextureAssignment(assignments, propertyName, textureAsset.Id);
				SaveMaterialAsset(materialEntry, materialAsset, materialMeta);
			}

			if (isSelected)
			{
				ImGui.SetItemDefaultFocus();
			}
		}

		ImGui.EndCombo();
	}

	private static void SetTextureAssignment(MaterialTextureAssignments assignments, string propertyName, Guid? value)
	{
		switch (propertyName)
		{
			case nameof(MaterialTextureAssignments.Albedo):
				assignments.Albedo = value;
				break;
			case nameof(MaterialTextureAssignments.MetallicRoughness):
				assignments.MetallicRoughness = value;
				break;
			case nameof(MaterialTextureAssignments.Normal):
				assignments.Normal = value;
				break;
			case nameof(MaterialTextureAssignments.Emissive):
				assignments.Emissive = value;
				break;
			case nameof(MaterialTextureAssignments.Occlusion):
				assignments.Occlusion = value;
				break;
			default:
				throw new InvalidOperationException($"Unknown material texture assignment '{propertyName}'.");
		}
	}

	private static bool HasProperty(IReadOnlyList<MaterialPropertyDefinition> definitions, MaterialPropertyKind kind)
	{
		for (var i = 0; i < definitions.Count; i++)
		{
			if (definitions[i].Kind == kind)
			{
				return true;
			}
		}

		return false;
	}

	private static string FormatResolutionLabel(int resolution)
	{
		return $"{resolution}x{resolution}";
	}
}

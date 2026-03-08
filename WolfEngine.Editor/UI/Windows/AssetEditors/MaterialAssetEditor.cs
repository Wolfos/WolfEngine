using ImGuiNET;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;

namespace WolfEngine.Editor.UI;

public sealed class MaterialAssetEditor
{
	private readonly IEditorProjectService _projectService;
	private readonly IMaterialAssetStore _materialAssetStore;
	private readonly IMaterialTypeRegistry _materialTypeRegistry;
	private readonly IPropertyDrawerRegistry _propertyDrawerRegistry;
	private Guid? _loadedMaterialAssetId;
	private MaterialAssetFile? _loadedMaterialAsset;
	private MaterialMetaFile? _loadedMaterialMeta;

	public MaterialAssetEditor(
		IEditorProjectService projectService,
		IMaterialAssetStore materialAssetStore,
		IMaterialTypeRegistry materialTypeRegistry,
		IPropertyDrawerRegistry propertyDrawerRegistry)
	{
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
		_materialAssetStore = materialAssetStore ?? throw new ArgumentNullException(nameof(materialAssetStore));
		_materialTypeRegistry = materialTypeRegistry ?? throw new ArgumentNullException(nameof(materialTypeRegistry));
		_propertyDrawerRegistry = propertyDrawerRegistry ?? throw new ArgumentNullException(nameof(propertyDrawerRegistry));
	}

	public void Draw(AssetDatabaseEntry asset)
	{
		var materialAsset = EnsureMaterialAssetLoaded(asset);
		var materialMeta = EnsureMaterialMetaLoaded(asset);
		if (materialAsset is null || materialMeta is null)
		{
			ImGui.TextUnformatted("Failed to load material asset.");
			return;
		}

		var descriptors = _materialTypeRegistry.GetAll();
		EditorUIUtility.Combo("Material Type", materialAsset.MaterialType.ToString(), () =>
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
		});

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
		var drawResult = _propertyDrawerRegistry.Draw(new PropertyDrawerContext(
			"Base Color",
			typeof(Color),
			properties.BaseColor));
		if (drawResult.Changed && drawResult.Value is Color color)
		{
			properties.BaseColor = color;
			SaveMaterialAsset(asset, materialAsset, materialMeta);
		}
	}

	private void DrawFloatEditor(string label, float currentValue, Action<float> setter)
	{
		var drawResult = _propertyDrawerRegistry.Draw(new PropertyDrawerContext(label, typeof(float), currentValue));
		if (drawResult.Changed && drawResult.Value is float value)
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

		EditorUIUtility.Combo(label, previewLabel, () =>
		{
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
		});
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
}

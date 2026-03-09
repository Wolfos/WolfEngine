using ImGuiNET;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.UI;

public sealed class MaterialAssetEditor
{
	private readonly IEditorProjectService _projectService;
	private readonly IMaterialAssetStore _materialAssetStore;
	private readonly IMaterialTypeRegistry _materialTypeRegistry;
	private readonly IPropertyDrawerRegistry _propertyDrawerRegistry;
	private readonly ITextureFactory _textureFactory;
	private readonly RenderGraph _renderGraph;
	private readonly GpuDrawDatabase _gpuDrawDatabase;
	private MaterialAsset? _loadedMaterialAsset;
	private Guid? _loadedMaterialAssetId;
	private MaterialAssetStateFile? _loadedMaterialState;
	private Guid? _loadedMaterialStateAssetId;

	public MaterialAssetEditor(
		IEditorProjectService projectService,
		IMaterialAssetStore materialAssetStore,
		IMaterialTypeRegistry materialTypeRegistry,
		IPropertyDrawerRegistry propertyDrawerRegistry,
		ITextureFactory textureFactory,
		RenderGraph renderGraph,
		GpuDrawDatabase gpuDrawDatabase)
	{
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
		_materialAssetStore = materialAssetStore ?? throw new ArgumentNullException(nameof(materialAssetStore));
		_materialTypeRegistry = materialTypeRegistry ?? throw new ArgumentNullException(nameof(materialTypeRegistry));
		_propertyDrawerRegistry = propertyDrawerRegistry ?? throw new ArgumentNullException(nameof(propertyDrawerRegistry));
		_textureFactory = textureFactory ?? throw new ArgumentNullException(nameof(textureFactory));
		_renderGraph = renderGraph ?? throw new ArgumentNullException(nameof(renderGraph));
		_gpuDrawDatabase = gpuDrawDatabase ?? throw new ArgumentNullException(nameof(gpuDrawDatabase));
	}

	public void Draw(AssetDatabaseEntry asset)
	{
		var materialAsset = EnsureMaterialAssetLoaded(asset);
		var materialState = EnsureMaterialStateLoaded(asset);
		if (materialAsset is null || materialState is null)
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
					materialState.Summary.MaterialType = descriptor.Type;
					SaveMaterialAsset(asset, materialAsset, materialState);
				}

				if (isSelected)
				{
					ImGui.SetItemDefaultFocus();
				}
			}
		});

		var properties = materialAsset.GetActiveProperties();
		var propertyDefinitions = _materialTypeRegistry.GetPropertiesForMaterialType(materialAsset.MaterialType);
		DrawBaseColorEditor(asset, materialAsset, materialState, properties);
		DrawFloatEditor("Metallic", properties.MetallicFactor, value =>
		{
			properties.MetallicFactor = value;
			SaveMaterialAsset(asset, materialAsset, materialState);
		});
		DrawFloatEditor("Roughness", properties.RoughnessFactor, value =>
		{
			properties.RoughnessFactor = value;
			SaveMaterialAsset(asset, materialAsset, materialState);
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

				SaveMaterialAsset(asset, materialAsset, materialState);
			});
		}

		ImGui.Separator();
		ImGui.TextUnformatted("Textures");
		var textureAssets = GetTextureAssets();
		DrawTextureAssignmentCombo(asset, materialAsset, materialState, properties.Textures, nameof(MaterialTextureAssignments.Albedo), "Albedo", properties.Textures.Albedo, textureAssets);
		DrawTextureAssignmentCombo(asset, materialAsset, materialState, properties.Textures, nameof(MaterialTextureAssignments.MetallicRoughness), "Metallic / Roughness", properties.Textures.MetallicRoughness, textureAssets);
		DrawTextureAssignmentCombo(asset, materialAsset, materialState, properties.Textures, nameof(MaterialTextureAssignments.Normal), "Normal", properties.Textures.Normal, textureAssets);
		DrawTextureAssignmentCombo(asset, materialAsset, materialState, properties.Textures, nameof(MaterialTextureAssignments.Emissive), "Emissive", properties.Textures.Emissive, textureAssets);
		DrawTextureAssignmentCombo(asset, materialAsset, materialState, properties.Textures, nameof(MaterialTextureAssignments.Occlusion), "Occlusion", properties.Textures.Occlusion, textureAssets);
	}

	private MaterialAsset? EnsureMaterialAssetLoaded(AssetDatabaseEntry asset)
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

	private MaterialAssetStateFile? EnsureMaterialStateLoaded(AssetDatabaseEntry asset)
	{
		if (_loadedMaterialStateAssetId == asset.Id && _loadedMaterialState is not null)
		{
			return _loadedMaterialState;
		}

		try
		{
			_loadedMaterialStateAssetId = asset.Id;
			_loadedMaterialState = _materialAssetStore.LoadState(_projectService.GetAbsolutePath(asset.GetEffectiveRelativeStatePath()));
			return _loadedMaterialState;
		}
		catch
		{
			_loadedMaterialStateAssetId = asset.Id;
			_loadedMaterialState = null;
			return null;
		}
	}

	private void SaveMaterialAsset(AssetDatabaseEntry asset, MaterialAsset materialAsset, MaterialAssetStateFile materialState)
	{
		_materialAssetStore.SaveAsset(_projectService.GetAbsolutePath(asset.RelativeAssetPath), materialAsset);
		var relativeStatePath = _materialAssetStore.GetStateRelativePath(asset.Id);
		_materialAssetStore.SaveState(_projectService.GetAbsolutePath(relativeStatePath), materialState);
		SynchronizeRuntimeMaterial(asset.Id, materialAsset);
		_loadedMaterialAsset = materialAsset;
		_loadedMaterialAssetId = asset.Id;
		_loadedMaterialState = materialState;
		_loadedMaterialStateAssetId = asset.Id;

		var updatedDatabase = _projectService.CloneCurrentAssetDatabase();
		for (var i = 0; i < updatedDatabase.Assets.Count; i++)
		{
			if (updatedDatabase.Assets[i].Id != asset.Id)
			{
				continue;
			}

			updatedDatabase.Assets[i].RelativeStatePath = relativeStatePath;
			updatedDatabase.Assets[i].RelativeMetaPath = relativeStatePath;
			updatedDatabase.Assets[i].MaterialSummary ??= new MaterialAssetSummary();
			updatedDatabase.Assets[i].MaterialSummary!.MaterialType = materialAsset.MaterialType;
			break;
		}

		_projectService.SaveAssetDatabase(updatedDatabase);
	}

	private void DrawBaseColorEditor(
		AssetDatabaseEntry asset,
		MaterialAsset materialAsset,
		MaterialAssetStateFile materialState,
		MaterialSurfaceProperties properties)
	{
		var drawResult = _propertyDrawerRegistry.Draw(new PropertyDrawerContext(
			"Base Color",
			typeof(ColorRGBA),
			properties.BaseColor));
		if (drawResult.Changed && drawResult.Value is ColorRGBA color)
		{
			properties.BaseColor = color;
			SaveMaterialAsset(asset, materialAsset, materialState);
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
		MaterialAsset materialAsset,
		MaterialAssetStateFile materialState,
		MaterialTextureAssignments assignments,
		string propertyName,
		string label,
		AssetLink<Texture> currentValue,
		IReadOnlyList<AssetDatabaseEntry> textureAssets)
	{
		var previewLabel = currentValue.Id != Guid.Empty && _projectService.TryGetAsset(currentValue.Id, out var selectedTexture)
			? selectedTexture.Name
			: "None";

		EditorUIUtility.Combo(label, previewLabel, () =>
		{
			var noneSelected = currentValue.Id == Guid.Empty;
			if (ImGui.Selectable("None", noneSelected))
			{
				SetTextureAssignment(assignments, propertyName, Guid.Empty);
				SaveMaterialAsset(materialEntry, materialAsset, materialState);
			}
			if (noneSelected)
			{
				ImGui.SetItemDefaultFocus();
			}

			for (var i = 0; i < textureAssets.Count; i++)
			{
				var textureAsset = textureAssets[i];
				var isSelected = currentValue.Id == textureAsset.Id;
				if (ImGui.Selectable(textureAsset.Name, isSelected))
				{
					SetTextureAssignment(assignments, propertyName, textureAsset.Id);
					SaveMaterialAsset(materialEntry, materialAsset, materialState);
				}

				if (isSelected)
				{
					ImGui.SetItemDefaultFocus();
				}
			}
		});
	}

	private IReadOnlyList<AssetDatabaseEntry> GetTextureAssets()
	{
		return _projectService.CurrentAssetDatabase.Assets
			.Where(asset => asset.Type == AssetType.Texture2D)
			.OrderBy(asset => asset.Name, StringComparer.OrdinalIgnoreCase)
			.ToList();
	}

	private void SynchronizeRuntimeMaterial(Guid assetId, MaterialAsset materialAsset)
	{
		var runtimeMaterial = AssetDatabase.GetInstance<Material>(assetId);
		if (runtimeMaterial is null)
		{
			return;
		}

		var descriptor = _materialTypeRegistry.GetDescriptor(materialAsset.MaterialType);
		var properties = materialAsset.GetActiveProperties();
		runtimeMaterial.Color = properties.BaseColor;
		runtimeMaterial.MetallicFactor = properties.MetallicFactor;
		runtimeMaterial.RoughnessFactor = properties.RoughnessFactor;
		runtimeMaterial.AlbedoTexture = ResolveTexture(properties.Textures.Albedo) ?? _textureFactory.GetWhiteTexture();
		runtimeMaterial.MetallicRoughnessTexture = ResolveTexture(properties.Textures.MetallicRoughness) ?? _textureFactory.GetWhiteTexture();
		runtimeMaterial.NormalTexture = ResolveTexture(properties.Textures.Normal) ?? _textureFactory.GetNeutralNormalTexture();
		runtimeMaterial.EmissiveTexture = ResolveTexture(properties.Textures.Emissive) ?? _textureFactory.GetWhiteTexture();
		runtimeMaterial.OcclusionTexture = ResolveTexture(properties.Textures.Occlusion) ?? _textureFactory.GetWhiteTexture();
		runtimeMaterial.AlphaMode = descriptor.RuntimeAlphaMode;
		runtimeMaterial.AlphaCutoff = properties switch
		{
			AlphaTestMaterialProperties alphaTest => alphaTest.AlphaCutoff,
			AlphaBlendMaterialProperties alphaBlend => alphaBlend.AlphaCutoff,
			_ => 0.5f
		};
		_renderGraph.RefreshMaterialResources(runtimeMaterial);
		_gpuDrawDatabase.NotifyMaterialChanged(runtimeMaterial);
	}

	private static Texture? ResolveTexture(AssetLink<Texture> link)
	{
		return link.Id == Guid.Empty ? null : AssetDatabase.GetInstance<Texture>(link.Id);
	}

	private static void SetTextureAssignment(MaterialTextureAssignments assignments, string propertyName, Guid value)
	{
		var link = new AssetLink<Texture> { Id = value };
		switch (propertyName)
		{
			case nameof(MaterialTextureAssignments.Albedo):
				assignments.Albedo = link;
				break;
			case nameof(MaterialTextureAssignments.MetallicRoughness):
				assignments.MetallicRoughness = link;
				break;
			case nameof(MaterialTextureAssignments.Normal):
				assignments.Normal = link;
				break;
			case nameof(MaterialTextureAssignments.Emissive):
				assignments.Emissive = link;
				break;
			case nameof(MaterialTextureAssignments.Occlusion):
				assignments.Occlusion = link;
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

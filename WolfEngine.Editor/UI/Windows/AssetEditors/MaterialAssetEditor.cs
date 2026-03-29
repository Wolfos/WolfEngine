using System;
using System.Collections.Generic;
using System.Linq;
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
	private MaterialAsset? _loadedMaterialAsset;
	private Guid? _loadedMaterialAssetId;

	public MaterialAssetEditor(
		IEditorProjectService projectService,
		IMaterialAssetStore materialAssetStore,
		IMaterialTypeRegistry materialTypeRegistry,
		IPropertyDrawerRegistry propertyDrawerRegistry,
		ITextureFactory textureFactory,
		RenderGraph renderGraph)
	{
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
		_materialAssetStore = materialAssetStore ?? throw new ArgumentNullException(nameof(materialAssetStore));
		_materialTypeRegistry = materialTypeRegistry ?? throw new ArgumentNullException(nameof(materialTypeRegistry));
		_propertyDrawerRegistry = propertyDrawerRegistry ?? throw new ArgumentNullException(nameof(propertyDrawerRegistry));
		_textureFactory = textureFactory ?? throw new ArgumentNullException(nameof(textureFactory));
		_renderGraph = renderGraph ?? throw new ArgumentNullException(nameof(renderGraph));
	}

	public void Draw(AssetDatabaseEntry asset)
	{
		if (asset.IsGenerated)
		{
			ImGui.TextUnformatted("Generated material");
			ImGui.TextDisabled("This material was produced from an imported 3D source and is read-only in this slice.");
			return;
		}

		var materialAsset = EnsureMaterialAssetLoaded(asset);
		if (materialAsset is null)
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
					SaveMaterialAsset(asset, materialAsset);
				}

				if (isSelected)
				{
					ImGui.SetItemDefaultFocus();
				}
			}
		});

		var properties = materialAsset.GetActiveProperties();
		var propertyDefinitions = _materialTypeRegistry.GetPropertiesForMaterialType(materialAsset.MaterialType);
		DrawBaseColorEditor(asset, materialAsset, properties);
		DrawFloatEditor("Metallic", properties.MetallicFactor, value =>
		{
			properties.MetallicFactor = value;
			SaveMaterialAsset(asset, materialAsset);
		});
		DrawFloatEditor("Roughness", properties.RoughnessFactor, value =>
		{
			properties.RoughnessFactor = value;
			SaveMaterialAsset(asset, materialAsset);
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

				SaveMaterialAsset(asset, materialAsset);
			});
		}

		ImGui.Separator();
		ImGui.TextUnformatted("Textures");
		var textureAssets = GetTextureAssets();
		DrawTextureAssignmentCombo(asset, materialAsset, properties.Textures, nameof(MaterialTextureAssignments.Albedo), "Albedo", properties.Textures.Albedo, textureAssets);
		DrawTextureAssignmentCombo(asset, materialAsset, properties.Textures, nameof(MaterialTextureAssignments.MetallicRoughness), "Metallic / Roughness", properties.Textures.MetallicRoughness, textureAssets);
		DrawTextureAssignmentCombo(asset, materialAsset, properties.Textures, nameof(MaterialTextureAssignments.Normal), "Normal", properties.Textures.Normal, textureAssets);
		DrawTextureAssignmentCombo(asset, materialAsset, properties.Textures, nameof(MaterialTextureAssignments.Emissive), "Emissive", properties.Textures.Emissive, textureAssets);
		DrawTextureAssignmentCombo(asset, materialAsset, properties.Textures, nameof(MaterialTextureAssignments.Occlusion), "Occlusion", properties.Textures.Occlusion, textureAssets);
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

	private void SaveMaterialAsset(AssetDatabaseEntry asset, MaterialAsset materialAsset)
	{
		_materialAssetStore.SaveAsset(_projectService.GetAbsolutePath(asset.RelativeAssetPath), materialAsset);
		SynchronizeRuntimeMaterial(asset.Id, materialAsset);
		_loadedMaterialAsset = materialAsset;
		_loadedMaterialAssetId = asset.Id;
		_projectService.RefreshAssetSource(asset.RelativeSourcePath);
	}

	private void DrawBaseColorEditor(AssetDatabaseEntry asset, MaterialAsset materialAsset, MaterialSurfaceProperties properties)
	{
		var drawResult = _propertyDrawerRegistry.Draw(new PropertyDrawerContext(
			"Base Color",
			typeof(ColorRGBA),
			properties.BaseColor));
		if (drawResult.Changed && drawResult.Value is ColorRGBA color)
		{
			properties.BaseColor = color;
			SaveMaterialAsset(asset, materialAsset);
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
		MaterialTextureAssignments assignments,
		string propertyName,
		string label,
		AssetRef<Texture> currentValue,
		IReadOnlyList<AssetDatabaseEntry> textureAssets)
	{
		var previewLabel = currentValue.NodeId != Guid.Empty && _projectService.TryGetAsset(currentValue.NodeId, out var selectedTexture)
			? selectedTexture.Name
			: "None";

		EditorUIUtility.Combo(label, previewLabel, () =>
		{
			var noneSelected = currentValue.NodeId == Guid.Empty;
			if (ImGui.Selectable("None", noneSelected))
			{
				SetTextureAssignment(assignments, propertyName, Guid.Empty);
				SaveMaterialAsset(materialEntry, materialAsset);
			}

			if (noneSelected)
			{
				ImGui.SetItemDefaultFocus();
			}

			for (var i = 0; i < textureAssets.Count; i++)
			{
				var textureAsset = textureAssets[i];
				var isSelected = currentValue.NodeId == textureAsset.Id;
				if (ImGui.Selectable(textureAsset.Name, isSelected))
				{
					SetTextureAssignment(assignments, propertyName, textureAsset.Id);
					SaveMaterialAsset(materialEntry, materialAsset);
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
	}

	private static Texture? ResolveTexture(AssetRef<Texture> reference)
	{
		return reference.NodeId == Guid.Empty ? null : AssetDatabase.GetInstance<Texture>(reference.NodeId);
	}

	private static void SetTextureAssignment(MaterialTextureAssignments assignments, string propertyName, Guid value)
	{
		var reference = new AssetRef<Texture> { NodeId = value };
		switch (propertyName)
		{
			case nameof(MaterialTextureAssignments.Albedo):
				assignments.Albedo = reference;
				break;
			case nameof(MaterialTextureAssignments.MetallicRoughness):
				assignments.MetallicRoughness = reference;
				break;
			case nameof(MaterialTextureAssignments.Normal):
				assignments.Normal = reference;
				break;
			case nameof(MaterialTextureAssignments.Emissive):
				assignments.Emissive = reference;
				break;
			case nameof(MaterialTextureAssignments.Occlusion):
				assignments.Occlusion = reference;
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

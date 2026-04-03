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
	private readonly IEditorAssetSnapshotService _assetSnapshotService;
	private readonly IEditorUndoRedoService _undoRedoService;
	private MaterialAsset? _loadedMaterialAsset;
	private Guid? _loadedMaterialAssetId;
	private AssetDatabaseEntry? _loadedAssetEntry;
	private EditorAssetFileSnapshot? _pendingBeforeSnapshot;
	private bool _hasPendingChanges;

	public MaterialAssetEditor(
		IEditorProjectService projectService,
		IMaterialAssetStore materialAssetStore,
		IMaterialTypeRegistry materialTypeRegistry,
		IPropertyDrawerRegistry propertyDrawerRegistry,
		IEditorAssetSnapshotService assetSnapshotService,
		IEditorUndoRedoService undoRedoService)
	{
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
		_materialAssetStore = materialAssetStore ?? throw new ArgumentNullException(nameof(materialAssetStore));
		_materialTypeRegistry = materialTypeRegistry ?? throw new ArgumentNullException(nameof(materialTypeRegistry));
		_propertyDrawerRegistry = propertyDrawerRegistry ?? throw new ArgumentNullException(nameof(propertyDrawerRegistry));
		_assetSnapshotService = assetSnapshotService ?? throw new ArgumentNullException(nameof(assetSnapshotService));
		_undoRedoService = undoRedoService ?? throw new ArgumentNullException(nameof(undoRedoService));
	}

	public void Draw(AssetDatabaseEntry asset)
	{
		if (asset.IsGenerated)
		{
			ImGui.TextUnformatted("Generated material");
			ImGui.TextDisabled("This material was produced from an imported 3D source and is read-only in this slice.");
			return;
		}

		if (_loadedMaterialAssetId.HasValue && _loadedMaterialAssetId.Value != asset.Id)
		{
			CommitPendingChanges();
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
					BeginPendingChange(asset);
					materialAsset.MaterialType = descriptor.Type;
					_hasPendingChanges = true;
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
			BeginPendingChange(asset);
			properties.MetallicFactor = value;
			_hasPendingChanges = true;
		});
		DrawFloatEditor("Roughness", properties.RoughnessFactor, value =>
		{
			BeginPendingChange(asset);
			properties.RoughnessFactor = value;
			_hasPendingChanges = true;
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
				BeginPendingChange(asset);
				if (properties is AlphaTestMaterialProperties alphaTest)
				{
					alphaTest.AlphaCutoff = value;
				}
				else if (properties is AlphaBlendMaterialProperties alphaBlend)
				{
					alphaBlend.AlphaCutoff = value;
				}

				_hasPendingChanges = true;
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

		if (_hasPendingChanges && ImGui.IsAnyItemActive() == false)
		{
			CommitPendingChanges();
		}
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
			_loadedAssetEntry = asset;
			_loadedMaterialAsset = _materialAssetStore.LoadAsset(_projectService.GetAbsolutePath(asset.RelativeAssetPath));
			return _loadedMaterialAsset;
		}
		catch
		{
			_loadedMaterialAssetId = asset.Id;
			_loadedAssetEntry = asset;
			_loadedMaterialAsset = null;
			return null;
		}
	}

	private void DrawBaseColorEditor(AssetDatabaseEntry asset, MaterialAsset materialAsset, MaterialSurfaceProperties properties)
	{
		var drawResult = _propertyDrawerRegistry.Draw(new PropertyDrawerContext(
			"Base Color",
			typeof(ColorRGBA),
			properties.BaseColor));
		if (drawResult.Changed && drawResult.Value is ColorRGBA color)
		{
			BeginPendingChange(asset);
			properties.BaseColor = color;
			_hasPendingChanges = true;
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
				BeginPendingChange(materialEntry);
				SetTextureAssignment(assignments, propertyName, Guid.Empty);
				_hasPendingChanges = true;
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
					BeginPendingChange(materialEntry);
					SetTextureAssignment(assignments, propertyName, textureAsset.Id);
					_hasPendingChanges = true;
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

	private void BeginPendingChange(AssetDatabaseEntry asset)
	{
		if (_pendingBeforeSnapshot.HasValue)
		{
			return;
		}

		_pendingBeforeSnapshot = _assetSnapshotService.CaptureMaterialAssetSnapshot(asset);
	}

	private void CommitPendingChanges()
	{
		if (_hasPendingChanges == false || _pendingBeforeSnapshot is not { } before || _loadedAssetEntry is null || _loadedMaterialAsset is null)
		{
			_hasPendingChanges = false;
			_pendingBeforeSnapshot = null;
			return;
		}

		var after = _assetSnapshotService.CaptureMaterialAssetSnapshot(_loadedAssetEntry, _loadedMaterialAsset);
		if (string.Equals(before.Json, after.Json, StringComparison.Ordinal))
		{
			_hasPendingChanges = false;
			_pendingBeforeSnapshot = null;
			return;
		}

		_assetSnapshotService.SaveMaterialAsset(_loadedAssetEntry, _loadedMaterialAsset);
		_undoRedoService.BeginCapture("Edit Material Asset");
		_undoRedoService.CommitCapture(new MaterialAssetEditUndoRedoEntry("Edit Material Asset", before, after));
		_hasPendingChanges = false;
		_pendingBeforeSnapshot = null;
	}
}

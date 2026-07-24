using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using ImGuiNET;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.UI;

public sealed class DataAssetEditor
{
	private readonly IEditorProjectService _projectService;
	private readonly IDataAssetStore _dataAssetStore;
	private readonly IPropertyDrawerRegistry _propertyDrawerRegistry;
	private readonly IEditorAssetSnapshotService _assetSnapshotService;
	private readonly IEditorUndoRedoService _undoRedoService;
	private readonly IIconManager _icons;
	private readonly IAssetSelectionService _assetSelectionService;
	private DataAssetLoadResult? _loadedAsset;
	private Guid? _loadedAssetId;
	private long _loadedAssetDatabaseRevision = -1;
	private AssetDatabaseEntry? _loadedAssetEntry;
	private EditorAssetFileSnapshot? _pendingBeforeSnapshot;
	private bool _hasPendingChanges;

	public DataAssetEditor(
		IEditorProjectService projectService,
		IDataAssetStore dataAssetStore,
		IPropertyDrawerRegistry propertyDrawerRegistry,
		IEditorAssetSnapshotService assetSnapshotService,
		IEditorUndoRedoService undoRedoService,
		IIconManager icons,
		IAssetSelectionService assetSelectionService)
	{
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
		_dataAssetStore = dataAssetStore ?? throw new ArgumentNullException(nameof(dataAssetStore));
		_propertyDrawerRegistry = propertyDrawerRegistry ?? throw new ArgumentNullException(nameof(propertyDrawerRegistry));
		_assetSnapshotService = assetSnapshotService ?? throw new ArgumentNullException(nameof(assetSnapshotService));
		_undoRedoService = undoRedoService ?? throw new ArgumentNullException(nameof(undoRedoService));
		_icons = icons ?? throw new ArgumentNullException(nameof(icons));
		_assetSelectionService = assetSelectionService ?? throw new ArgumentNullException(nameof(assetSelectionService));
	}

	public void Draw(AssetDatabaseEntry asset)
	{
		if (asset.IsGenerated)
		{
			ImGui.TextUnformatted("Generated data nodes are read-only.");
			return;
		}

		if (_loadedAssetId.HasValue && _loadedAssetId.Value != asset.Id)
		{
			CommitPendingChanges();
		}

		var loadedAsset = EnsureLoaded(asset);

		if (loadedAsset is null)
		{
			ImGui.TextUnformatted("Failed to load data asset.");
			return;
		}

		if (DrawObjectProperties(loadedAsset.Asset, loadedAsset.DataAssetType, includeHeader: false))
		{
			BeginPendingChange(asset);
			_hasPendingChanges = true;
		}

		if (_hasPendingChanges && ImGui.IsAnyItemActive() == false)
		{
			CommitPendingChanges();
		}
	}

	private DataAssetLoadResult? EnsureLoaded(AssetDatabaseEntry asset)
	{
		if (_loadedAssetId == asset.Id && _loadedAsset is not null &&
		    _loadedAssetDatabaseRevision == _projectService.AssetDatabaseRevision)
		{
			// Asset IDs survive source renames, but the path used when saving must
			// follow the latest database entry.
			_loadedAssetEntry = asset;
			return _loadedAsset;
		}

		try
		{
			_hasPendingChanges = false;
			_pendingBeforeSnapshot = null;
			_loadedAssetId = asset.Id;
			_loadedAssetEntry = asset;
			_loadedAsset = _dataAssetStore.LoadAsset(_projectService.GetAbsolutePath(asset.RelativeAssetPath));
			_loadedAssetDatabaseRevision = _projectService.AssetDatabaseRevision;
			return _loadedAsset;
		}
		catch
		{
			_loadedAssetId = asset.Id;
			_loadedAssetEntry = asset;
			_loadedAsset = null;
			_loadedAssetDatabaseRevision = _projectService.AssetDatabaseRevision;
			return null;
		}
	}

	private bool DrawObjectProperties(object target, Type targetType, bool includeHeader, string? headerLabel = null)
	{
		if (includeHeader)
		{
			return DrawCollapsibleGroup(
				headerLabel ?? targetType.Name,
				() => DrawObjectProperties(target, targetType, includeHeader: false));
		}

		if (target is TerrainLayerSet terrainLayerSet)
		{
			return DrawTerrainLayerSetProperties(terrainLayerSet, includeHeader: false, headerLabel: null);
		}

		var changed = false;
		foreach (var property in GetEditableProperties(targetType))
		{
			ImGui.PushID(property.Name);
			try
			{
				var value = property.GetValue(target);
				var drawResult = _propertyDrawerRegistry.Draw(CreatePropertyDrawerContext(property.Name, property.PropertyType, value));
				if (drawResult.Handled)
				{
					if (drawResult.Changed)
					{
						property.SetValue(target, drawResult.Value);
						changed = true;
					}

					continue;
				}

				if (TryDrawNestedProperty(target, property, value))
				{
					changed = true;
				}
			}
			finally
			{
				ImGui.PopID();
			}
		}

		return changed;
	}

	private bool DrawTerrainLayerSetProperties(TerrainLayerSet layerSet, bool includeHeader, string? headerLabel)
	{
		if (includeHeader)
		{
			return DrawCollapsibleGroup(
				headerLabel ?? nameof(TerrainLayerSet),
				() => DrawTerrainLayerSetProperties(layerSet, includeHeader: false, headerLabel: null));
		}

		var changed = false;
		var activeLayerCount = Math.Clamp(layerSet.ActiveLayerCount, 1, TerrainLayerSet.MaxLayerCount);
		if (EditorUIUtility.InputInt("Active Layer Count", ref activeLayerCount))
		{
			activeLayerCount = Math.Clamp(activeLayerCount, 1, TerrainLayerSet.MaxLayerCount);
			layerSet.ActiveLayerCount = activeLayerCount;
			changed = true;
		}

		var heightBlendSharpness = layerSet.HeightBlendSharpness;
		if (EditorUIUtility.InputFloat("Height Blend Sharpness", ref heightBlendSharpness))
		{
			layerSet.HeightBlendSharpness = heightBlendSharpness;
			changed = true;
		}

		layerSet.EnsureLayerCapacity(activeLayerCount);
		for (var layerIndex = 0; layerIndex < activeLayerCount; layerIndex++)
		{
			ImGui.PushID(layerIndex);
			try
			{
				if (EditorUIUtility.CollapsingHeader($"Layer {layerIndex + 1}") == false)
				{
					continue;
				}

				EditorUIUtility.BeginIndentedGroup();
				try
				{
					var layer = layerSet.GetLayer(layerIndex);
					var layerName = layer.Name;
					if (EditorUIUtility.InputText("Name", ref layerName))
					{
						layer.Name = layerName;
						changed = true;
					}

					var scale = layer.Scale;
					if (EditorUIUtility.InputFloat("Scale", ref scale))
					{
						layer.Scale = scale;
						changed = true;
					}

					var autoMaterial = layer.AutoMaterial;
					if (EditorUIUtility.Checkbox("Auto Material", ref autoMaterial))
					{
						layer.AutoMaterial = autoMaterial;
						changed = true;
					}

					if (autoMaterial)
					{
						var useMinimumSlope = layer.UseMinimumSlope;
						if (EditorUIUtility.Checkbox("Use Minimum Slope", ref useMinimumSlope))
						{
							layer.UseMinimumSlope = useMinimumSlope;
							changed = true;
						}

						if (useMinimumSlope)
						{
							var minimumSlopeDegrees = Math.Clamp(layer.MinimumSlopeDegrees, 0.0f, 90.0f);
							if (ImGui.SliderFloat("Minimum Slope", ref minimumSlopeDegrees, 0.0f, 90.0f, "%.0f deg"))
							{
								layer.MinimumSlopeDegrees = minimumSlopeDegrees;
								changed = true;
							}
						}
					}

					changed |= DrawTerrainLayerAssetRef(layer, nameof(TerrainLayerDefinition.Albedo));
					changed |= DrawTerrainLayerAssetRef(layer, nameof(TerrainLayerDefinition.Normal));
					changed |= DrawTerrainLayerAssetRef(layer, nameof(TerrainLayerDefinition.Orm));
					changed |= DrawTerrainLayerAssetRef(layer, nameof(TerrainLayerDefinition.Height));
				}
				finally
				{
					EditorUIUtility.EndIndentedGroup();
				}
			}
			finally
			{
				ImGui.PopID();
			}
		}

		return changed;
	}

	private static bool DrawCollapsibleGroup(string label, Func<bool> drawContents)
	{
		if (EditorUIUtility.CollapsingHeader(label) == false)
		{
			return false;
		}

		EditorUIUtility.BeginIndentedGroup();
		try
		{
			return drawContents();
		}
		finally
		{
			EditorUIUtility.EndIndentedGroup();
		}
	}

	private bool DrawTerrainLayerAssetRef(TerrainLayerDefinition layer, string propertyName)
	{
		var property = typeof(TerrainLayerDefinition).GetProperty(propertyName)
			?? throw new InvalidOperationException($"Missing terrain layer property '{propertyName}'.");
		var value = property.GetValue(layer);
		var result = _propertyDrawerRegistry.Draw(CreatePropertyDrawerContext(propertyName, property.PropertyType, value));
		if (result.Handled == false || result.Changed == false)
		{
			return false;
		}

		property.SetValue(layer, result.Value);
		return true;
	}

	private PropertyDrawerContext CreatePropertyDrawerContext(string label, Type valueType, object? value)
	{
		return new PropertyDrawerContext(
			label,
			valueType,
			value,
			AssetLinkSelectionButton: new AssetLinkSelectionButton(
				_icons.Get("search"),
				assetId => _assetSelectionService.Select(assetId)));
	}

	private bool TryDrawNestedProperty(object target, PropertyInfo property, object? value)
	{
		var propertyType = property.PropertyType;
		if (IsNestedObjectType(propertyType) == false)
		{
			DrawUnsupportedProperty(property.Name, propertyType);
			return false;
		}

		var propertyValue = value;
		var changed = false;
		if (propertyValue is null)
		{
			var constructor = propertyType.GetConstructor(Type.EmptyTypes);
			if (constructor is null)
			{
				DrawUnsupportedProperty(property.Name, propertyType);
				return false;
			}

			propertyValue = constructor.Invoke(null);
			property.SetValue(target, propertyValue);
			changed = true;
		}

		if (propertyType.IsValueType)
		{
			var boxedValue = propertyValue;
			if (DrawObjectProperties(boxedValue, propertyType, includeHeader: true, property.Name))
			{
				property.SetValue(target, boxedValue);
				changed = true;
			}

			return changed;
		}

		return DrawObjectProperties(propertyValue, propertyType, includeHeader: true, property.Name) || changed;
	}

	private static IReadOnlyList<PropertyInfo> GetEditableProperties(Type targetType)
	{
		return targetType
			.GetProperties(BindingFlags.Instance | BindingFlags.Public)
			.Where(property => property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0)
			.OrderBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
			.ToList();
	}

	private static bool IsNestedObjectType(Type type)
	{
		return type != typeof(string) &&
		       type.IsEnum == false &&
		       type.IsPrimitive == false &&
		       type != typeof(decimal) &&
		       type != typeof(Vector2) &&
		       type != typeof(Vector3) &&
		       type != typeof(Vector4) &&
		       type != typeof(ColorRGBA);
	}

	private static void DrawUnsupportedProperty(string propertyName, Type propertyType)
	{
		ImGui.TextDisabled($"{propertyName}: Unsupported ({propertyType.Name})");
	}

	private void BeginPendingChange(AssetDatabaseEntry asset)
	{
		if (_pendingBeforeSnapshot.HasValue)
		{
			return;
		}

		_pendingBeforeSnapshot = _assetSnapshotService.CaptureDataAssetSnapshot(asset);
	}

	private void CommitPendingChanges()
	{
		if (_hasPendingChanges == false || _pendingBeforeSnapshot is not { } before || _loadedAssetEntry is null || _loadedAsset is null)
		{
			_hasPendingChanges = false;
			_pendingBeforeSnapshot = null;
			return;
		}

		var after = _assetSnapshotService.CaptureDataAssetSnapshot(_loadedAssetEntry, _loadedAsset.DataAssetType, _loadedAsset.Asset);
		if (string.Equals(before.Json, after.Json, StringComparison.Ordinal))
		{
			_hasPendingChanges = false;
			_pendingBeforeSnapshot = null;
			return;
		}

		_assetSnapshotService.SaveDataAsset(_loadedAssetEntry, _loadedAsset.DataAssetType, _loadedAsset.Asset);
		_undoRedoService.BeginCapture("Edit Data Asset");
		_undoRedoService.CommitCapture(new DataAssetEditUndoRedoEntry("Edit Data Asset", before, after));
		_hasPendingChanges = false;
		_pendingBeforeSnapshot = null;
	}
}

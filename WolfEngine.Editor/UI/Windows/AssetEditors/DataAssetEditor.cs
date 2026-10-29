using System.Numerics;
using System.Reflection;
using ImGuiNET;
using WolfEngine.Animation;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.UI;

public sealed class DataAssetEditor
{
	private static readonly PropertyInfo AnimationSetClipsProperty = typeof(AnimationSet).GetProperty(nameof(AnimationSet.Clips))!;

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
			_loadedAsset = _dataAssetStore.LoadAsset(
				_projectService.GetAbsoluteAssetPath(asset.Id, asset.RelativeAssetPath));
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
		if (target is AnimationSet set) return DrawAnimationSet(set);
		if (target is BoneMask mask) return DrawBoneMask(mask);

		var changed = false;
		foreach (var property in InspectorLayout.GetEditableProperties(targetType))
		{
			InspectorLayout.DrawDecorations(property);

			ImGui.PushID(property.Name);
			try
			{
				var value = property.GetValue(target);
				var drawResult = DrawValue(property.Name, property.PropertyType, value, property);
				if (drawResult.Changed)
				{
					property.SetValue(target, drawResult.Value);
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

	private bool DrawAnimationSet(AnimationSet set)
	{
		var skeleton = set.Skeleton;
		var changed = DrawAssetRef("Skeleton", ref skeleton);
		set.Skeleton = skeleton;

		InspectorLayout.DrawDecorations(AnimationSetClipsProperty);
		foreach (var slot in set.Clips.Keys.ToArray())
		{
			ImGui.PushID(slot);
			try
			{
				var name = slot;
				if (EditorUIUtility.InputText("Slot name", ref name, 128) &&
					string.IsNullOrWhiteSpace(name) == false &&
					string.Equals(name, slot, StringComparison.Ordinal) == false &&
					set.Clips.ContainsKey(name) == false)
				{
					var renamed = set.Clips[slot];
					set.Clips.Remove(slot);
					set.Clips.Add(name, renamed);
					changed = true;
				}
				else
				{
					name = slot;
				}

				var entry = set.Clips[name];
				var clip = entry.Clip;
				if (DrawAssetRef("Clip", ref clip))
				{
					entry.Clip = clip;
					changed = true;
				}

				var speed = entry.PlaybackSpeed;
				if (EditorUIUtility.InputFloat("Playback speed", ref speed))
				{
					entry.PlaybackSpeed = speed;
					changed = true;
				}

				if (ImGui.Button("Remove slot"))
				{
					set.Clips.Remove(name);
					changed = true;
				}
			}
			finally
			{
				ImGui.PopID();
			}

			InspectorLayout.DrawSpace(12.0f);
			ImGui.Separator();
			InspectorLayout.DrawSpace();
		}

		if (ImGui.Button("Add clip slot"))
		{
			var name = "Clip" + set.Clips.Count;
			while (set.Clips.ContainsKey(name))
			{
				name += "_";
			}

			set.Clips.Add(name, new AnimationSetEntry());
			changed = true;
		}

		return changed;
	}

	private bool DrawBoneMask(BoneMask mask)
	{
		var skeleton = mask.Skeleton;
		var changed = DrawAssetRef("Skeleton", ref skeleton);
		mask.Skeleton = skeleton;
		for (var i = 0; i < mask.Bones.Count; i++)
		{
			ImGui.PushID(i);
			var bone = mask.Bones[i]; var name = bone.Bone; var weight = bone.Weight; var children = bone.IncludeChildren;
			if (ImGui.InputText("Bone", ref name, 128)) { bone.Bone = name; changed = true; }
			if (ImGui.SliderFloat("Weight", ref weight, 0, 1)) { bone.Weight = weight; changed = true; }
			if (ImGui.Checkbox("Include descendants", ref children)) { bone.IncludeChildren = children; changed = true; }
			if (ImGui.Button("Remove bone")) { mask.Bones.RemoveAt(i); changed = true; }
			ImGui.PopID();
		}
		if (ImGui.Button("Add bone")) { mask.Bones.Add(new BoneMaskEntry()); changed = true; }
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

		var autoMaterialBlendDegrees = layerSet.AutoMaterialBlendDegrees;
		if (EditorUIUtility.InputFloat("Auto Material Blend (deg)", ref autoMaterialBlendDegrees))
		{
			layerSet.AutoMaterialBlendDegrees = Math.Max(autoMaterialBlendDegrees, 0.0f);
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

	private bool DrawAssetRef<T>(string label, ref AssetRef<T> value)
	{
		return _propertyDrawerRegistry.DrawAssetRef(label, ref value, CreateAssetLinkSelectionButton());
	}

	private PropertyDrawerContext CreatePropertyDrawerContext(string label, Type valueType, object? value, MemberInfo? member = null)
	{
		return new PropertyDrawerContext(
			label,
			valueType,
			value,
			Member: member,
			AssetLinkSelectionButton: CreateAssetLinkSelectionButton());
	}

	private AssetLinkSelectionButton CreateAssetLinkSelectionButton()
	{
		return new AssetLinkSelectionButton(
			_icons.Get("search"),
			assetId => _assetSelectionService.Select(assetId));
	}

	internal PropertyDrawerResult DrawValue(string label, Type valueType, object? value, MemberInfo? member = null)
	{
		if (valueType.IsArray)
		{
			return DrawArray(label, valueType, value as Array);
		}

		var drawResult = _propertyDrawerRegistry.Draw(CreatePropertyDrawerContext(label, valueType, value, member));
		if (drawResult.Handled)
		{
			return drawResult;
		}

		if (IsNestedObjectType(valueType) == false)
		{
			DrawUnsupportedProperty(label, valueType);
			return new PropertyDrawerResult(true, false, value);
		}

		var nextValue = value;
		var changed = DrawCollapsibleGroup(label, () =>
		{
			if (nextValue is null)
			{
				var canCreate = DataAssetArrayEditing.CanCreateElement(valueType);
				ImGui.TextDisabled("Null");
				ImGui.BeginDisabled(canCreate == false);
				try
				{
					if (ImGui.Button("Create"))
					{
						nextValue = DataAssetArrayEditing.CreateElement(valueType);
						return true;
					}
				}
				finally
				{
					ImGui.EndDisabled();
				}

				return false;
			}

			return DrawObjectProperties(nextValue, nextValue.GetType(), includeHeader: false);
		});

		return new PropertyDrawerResult(true, changed, nextValue);
	}

	private PropertyDrawerResult DrawArray(string label, Type arrayType, Array? value)
	{
		if (arrayType.IsSZArray == false)
		{
			DrawUnsupportedProperty(label, arrayType);
			return new PropertyDrawerResult(true, false, value);
		}

		var elementType = arrayType.GetElementType()!;
		var next = value;
		// Keep the ImGui ID stable when the element count changes.
		var changed = DrawCollapsibleGroup($"{label} ({value?.Length ?? 0})###Array", () =>
		{
			var removeIndex = -1;
			var elementsChanged = false;
			if (next is null || next.Length == 0)
			{
				ImGui.TextDisabled("Empty array");
			}

			for (var index = 0; index < (next?.Length ?? 0); index++)
			{
				ImGui.PushID(index);
				try
				{
					var elementResult = DrawValue($"[{index}]", elementType, next!.GetValue(index));
					if (elementResult.Changed)
					{
						next.SetValue(elementResult.Value, index);
						elementsChanged = true;
					}

					if (ImGui.Button("Remove element"))
					{
						removeIndex = index;
					}
				}
				finally
				{
					ImGui.PopID();
				}

				ImGui.Separator();
			}

			if (removeIndex >= 0)
			{
				next = DataAssetArrayEditing.RemoveElement(next!, removeIndex);
				elementsChanged = true;
			}

			var canAdd = DataAssetArrayEditing.CanCreateElement(elementType);
			ImGui.BeginDisabled(canAdd == false);
			try
			{
				if (ImGui.Button("Add element"))
				{
					next = DataAssetArrayEditing.AddElement(next, elementType);
					elementsChanged = true;
				}
			}
			finally
			{
				ImGui.EndDisabled();
			}

			if (canAdd == false)
			{
				ImGui.TextDisabled($"{elementType.Name} needs a public parameterless constructor to add elements.");
			}

			return elementsChanged;
		});

		return new PropertyDrawerResult(true, changed, next);
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

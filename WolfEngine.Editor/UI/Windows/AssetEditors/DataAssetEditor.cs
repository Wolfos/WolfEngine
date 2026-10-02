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
	private float _clipScrubTime;

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
		if (target is AnimationSequence sequence) return DrawAnimationSequence(sequence);
		if (target is BoneMask mask) return DrawBoneMask(mask);

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

	private bool DrawAnimationSet(AnimationSet set)
	{
		var skeleton = set.SkeletonId;
		var changed = AnimationWindow.AssetChoice(_projectService, "Skeleton", AssetType.Skeleton, ref skeleton);
		set.SkeletonId = skeleton;
		foreach (var slot in set.Clips.Keys.ToArray())
		{
			ImGui.PushID(slot);
			var name = slot;
			if (ImGui.InputText("Slot name", ref name, 128) && !string.IsNullOrWhiteSpace(name) &&
				!string.Equals(name, slot, StringComparison.Ordinal) && !set.Clips.ContainsKey(name))
			{
				var clip = set.Clips[slot];
				set.Clips.Remove(slot);
				set.Clips.Add(name, clip);
				changed = true;
			}
			var id = set.Clips.GetValueOrDefault(name, set.Clips.GetValueOrDefault(slot));
			if (AnimationWindow.DataAssetChoice<AnimationSequence>(_projectService, "Clip", ref id))
			{
				set.Clips[name] = id;
				changed = true;
			}
			if (ImGui.Button("Remove slot")) { set.Clips.Remove(name); changed = true; }
			ImGui.PopID();
		}
		if (ImGui.Button("Add clip slot"))
		{
			var name = "Clip" + set.Clips.Count;
			while (set.Clips.ContainsKey(name)) name += "_";
			set.Clips.Add(name, Guid.Empty);
			changed = true;
		}
		return changed;
	}

	private bool DrawAnimationSequence(AnimationSequence sequence)
	{
		var changed = false;
		var speed = sequence.PlaybackSpeed;
		if (ImGui.InputFloat("Playback speed", ref speed)) { sequence.PlaybackSpeed = speed; changed = true; }
		var clipId = sequence.ClipId;
		changed |= AnimationWindow.AssetChoice(_projectService, "Imported clip", AssetType.AnimationClip, ref clipId);
		sequence.ClipId = clipId;
		var duration = sequence.ClipId == Guid.Empty ? 1f : AssetDatabase.GetInstance<AnimationClip>(sequence.ClipId)?.Duration ?? 1f;
		ImGui.SliderFloat("Timeline seconds", ref _clipScrubTime, 0, Math.Max(duration, .01f));
		for (var i = 0; i < sequence.Curves.Count; i++)
		{
			ImGui.PushID(i);
			var curve = sequence.Curves[i];
			ImGui.SeparatorText("Curve " + (i + 1));
			var name = curve.Name;
			if (ImGui.InputText("Name", ref name, 128)) { curve.Name = name; changed = true; }
			var interpolation = (int)curve.Interpolation;
			if (ImGui.Combo("Interpolation", ref interpolation, "Step\0Linear\0Cubic\0"))
			{
				curve.Interpolation = (CurveInterpolation)interpolation;
				changed = true;
			}
			var origin = ImGui.GetCursorScreenPos();
			var size = new Vector2(Math.Max(100, ImGui.GetContentRegionAvail().X), 80);
			ImGui.InvisibleButton("Curve plot", size);
			var draw = ImGui.GetWindowDrawList();
			draw.AddRectFilled(origin, origin + size, 0xFF242424);
			try
			{
				var sampler = curve.Compile();
				var minimum = curve.Values.Length == 0 ? 0 : Math.Min(0, curve.Values.Min());
				var maximum = curve.Values.Length == 0 ? 1 : Math.Max(1, curve.Values.Max());
				var cursor = 0;
				var previous = origin;
				for (var sample = 0; sample <= 100; sample++)
				{
					var value = sampler.Evaluate(duration * sample / 100, ref cursor, 0);
					var position = origin + new Vector2(size.X * sample / 100, size.Y * (1 - (value - minimum) / (maximum - minimum)));
					if (sample > 0) draw.AddLine(previous, position, 0xFF60D8F0, 2);
					previous = position;
				}
			}
			catch (Exception exception) { ImGui.TextDisabled("Curve preview: " + exception.Message); }
			for (var key = 0; key < Math.Min(curve.Times.Length, curve.Values.Length); key++)
			{
				ImGui.PushID(key);
				var time = curve.Times[key]; var value = curve.Values[key];
				if (ImGui.InputFloat("Time", ref time)) { curve.Times[key] = time; changed = true; }
				if (ImGui.InputFloat("Value", ref value)) { curve.Values[key] = value; changed = true; }
				if (curve.Interpolation == CurveInterpolation.CubicHermite)
				{
					if (curve.InTangents?.Length != curve.Times.Length) curve.InTangents = new float[curve.Times.Length];
					if (curve.OutTangents?.Length != curve.Times.Length) curve.OutTangents = new float[curve.Times.Length];
					var input = curve.InTangents[key]; var output = curve.OutTangents[key];
					if (ImGui.InputFloat("In tangent", ref input)) { curve.InTangents[key] = input; changed = true; }
					if (ImGui.InputFloat("Out tangent", ref output)) { curve.OutTangents[key] = output; changed = true; }
				}
				if (ImGui.Button("Remove key"))
				{
					curve.Times = curve.Times.Where((_, index) => index != key).ToArray();
					curve.Values = curve.Values.Where((_, index) => index != key).ToArray();
					curve.InTangents = curve.InTangents?.Where((_, index) => index != key).ToArray();
					curve.OutTangents = curve.OutTangents?.Where((_, index) => index != key).ToArray();
					changed = true;
				}
				ImGui.PopID();
			}
			if (ImGui.Button("Add key at timeline time"))
			{
				var times = curve.Times.Append(_clipScrubTime).ToArray();
				var values = curve.Values.Append(0f).ToArray();
				var order = Enumerable.Range(0, times.Length).OrderBy(index => times[index]).ToArray();
				curve.Times = order.Select(index => times[index]).ToArray();
				curve.Values = order.Select(index => values[index]).ToArray();
				if (curve.Interpolation == CurveInterpolation.CubicHermite)
				{
					curve.InTangents = new float[times.Length]; curve.OutTangents = new float[times.Length];
				}
				changed = true;
			}
			if (ImGui.Button("Remove curve")) { sequence.Curves.RemoveAt(i); changed = true; }
			ImGui.PopID();
		}
		if (ImGui.Button("Add curve")) { sequence.Curves.Add(new AnimationCurve { Name = "Curve" + sequence.Curves.Count }); changed = true; }
		for (var i = 0; i < sequence.Markers.Count; i++)
		{
			ImGui.PushID("marker" + i);
			var marker = sequence.Markers[i]; var name = marker.Name; var time = marker.Time;
			if (ImGui.InputText("Marker", ref name, 128)) { marker.Name = name; changed = true; }
			if (ImGui.InputFloat("At seconds", ref time)) { marker.Time = time; changed = true; }
			if (ImGui.Button("Remove marker")) { sequence.Markers.RemoveAt(i); changed = true; }
			ImGui.PopID();
		}
		if (ImGui.Button("Add marker at timeline time"))
		{
			sequence.Markers.Add(new AnimationMarker { Name = "Marker", Time = _clipScrubTime }); changed = true;
		}
		return changed;
	}

	private bool DrawBoneMask(BoneMask mask)
	{
		var skeleton = mask.SkeletonId;
		var changed = AnimationWindow.AssetChoice(_projectService, "Skeleton", AssetType.Skeleton, ref skeleton);
		mask.SkeletonId = skeleton;
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

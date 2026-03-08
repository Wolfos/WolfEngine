using System.Numerics;
using System.Reflection;
using ImGuiNET;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;

namespace WolfEngine.Editor.UI;

public sealed class DataAssetEditor
{
	private readonly IEditorProjectService _projectService;
	private readonly IDataAssetStore _dataAssetStore;
	private readonly IPropertyDrawerRegistry _propertyDrawerRegistry;
	private Guid? _loadedAssetId;
	private IDataAsset? _loadedAsset;
	private DataAssetMetaFile? _loadedMeta;
	private Type? _loadedDataAssetType;

	public DataAssetEditor(
		IEditorProjectService projectService,
		IDataAssetStore dataAssetStore,
		IPropertyDrawerRegistry propertyDrawerRegistry)
	{
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
		_dataAssetStore = dataAssetStore ?? throw new ArgumentNullException(nameof(dataAssetStore));
		_propertyDrawerRegistry = propertyDrawerRegistry ?? throw new ArgumentNullException(nameof(propertyDrawerRegistry));
	}

	public void Draw(AssetDatabaseEntry asset)
	{
		var loadedAsset = EnsureAssetLoaded(asset);
		var meta = EnsureMetaLoaded(asset);
		if (loadedAsset is null || meta is null || _loadedDataAssetType is null)
		{
			ImGui.TextUnformatted("Failed to load data asset.");
			return;
		}

		if (DrawObjectProperties(loadedAsset, _loadedDataAssetType, includeHeader: false))
		{
			SaveAsset(asset, _loadedDataAssetType, loadedAsset, meta);
		}
	}

	private IDataAsset? EnsureAssetLoaded(AssetDatabaseEntry asset)
	{
		if (_loadedAssetId == asset.Id && _loadedAsset is not null && _loadedDataAssetType is not null)
		{
			return _loadedAsset;
		}

		try
		{
			var loadResult = _dataAssetStore.LoadAsset(_projectService.GetAbsolutePath(asset.RelativeAssetPath));
			_loadedAssetId = asset.Id;
			_loadedAsset = loadResult.Asset;
			_loadedDataAssetType = loadResult.DataAssetType;
			return _loadedAsset;
		}
		catch
		{
			_loadedAssetId = asset.Id;
			_loadedAsset = null;
			_loadedDataAssetType = null;
			return null;
		}
	}

	private DataAssetMetaFile? EnsureMetaLoaded(AssetDatabaseEntry asset)
	{
		if (_loadedAssetId == asset.Id && _loadedMeta is not null)
		{
			return _loadedMeta;
		}

		try
		{
			_loadedAssetId = asset.Id;
			_loadedMeta = _dataAssetStore.LoadMeta(_projectService.GetAbsolutePath(asset.RelativeMetaPath));
			return _loadedMeta;
		}
		catch
		{
			_loadedAssetId = asset.Id;
			_loadedMeta = null;
			return null;
		}
	}

	private void SaveAsset(AssetDatabaseEntry asset, Type dataAssetType, IDataAsset loadedAsset, DataAssetMetaFile meta)
	{
		_dataAssetStore.SaveAsset(_projectService.GetAbsolutePath(asset.RelativeAssetPath), dataAssetType, loadedAsset);
		_dataAssetStore.SaveMeta(_projectService.GetAbsolutePath(asset.RelativeMetaPath), meta);
		_loadedAsset = loadedAsset;
		_loadedMeta = meta;
		_loadedDataAssetType = dataAssetType;
	}

	private bool DrawObjectProperties(object target, Type targetType, bool includeHeader, string? headerLabel = null)
	{
		if (includeHeader && EditorUIUtility.CollapsingHeader(headerLabel ?? targetType.Name, true) == false)
		{
			return false;
		}

		var changed = false;
		foreach (var property in GetEditableProperties(targetType))
		{
			var value = property.GetValue(target);
			var drawResult = _propertyDrawerRegistry.Draw(new PropertyDrawerContext(property.Name, property.PropertyType, value));
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

		return changed;
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
		       type != typeof(ColorRgba);
	}

	private bool DrawEnumProperty(object target, PropertyInfo property, object? value)
	{
		var currentValue = value?.ToString() ?? string.Empty;
		var changed = false;
		EditorUIUtility.Combo(property.Name, currentValue, () =>
		{
			foreach (var candidate in Enum.GetValues(property.PropertyType))
			{
				var candidateName = candidate?.ToString() ?? string.Empty;
				var isSelected = Equals(candidate, value);
				if (ImGui.Selectable(candidateName, isSelected))
				{
					property.SetValue(target, candidate);
					changed = true;
				}

				if (isSelected)
				{
					ImGui.SetItemDefaultFocus();
				}
			}
		});

		return changed;
	}

	private static void DrawUnsupportedProperty(string label, Type propertyType)
	{
		EditorUIUtility.DrawLabeledField(label, () =>
		{
			ImGui.TextDisabled($"Unsupported ({propertyType.Name})");
			return false;
		});
	}
}

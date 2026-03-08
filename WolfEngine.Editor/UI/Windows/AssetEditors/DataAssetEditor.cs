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
	private Guid? _loadedAssetId;
	private IDataAsset? _loadedAsset;
	private DataAssetMetaFile? _loadedMeta;
	private Type? _loadedDataAssetType;

	public DataAssetEditor(IEditorProjectService projectService, IDataAssetStore dataAssetStore)
	{
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
		_dataAssetStore = dataAssetStore ?? throw new ArgumentNullException(nameof(dataAssetStore));
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
			if (TryDrawSimpleProperty(target, property, value, out var simplePropertyChanged))
			{
				changed |= simplePropertyChanged;
				continue;
			}

			if (TryDrawNestedProperty(target, property, value))
			{
				changed = true;
			}
		}

		return changed;
	}

	private bool TryDrawSimpleProperty(object target, PropertyInfo property, object? value, out bool changed)
	{
		changed = false;
		var propertyType = property.PropertyType;

		if (propertyType == typeof(string))
		{
			var stringValue = (string?)value ?? string.Empty;
			if (EditorUIUtility.InputText(property.Name, ref stringValue))
			{
				property.SetValue(target, stringValue);
				changed = true;
			}

			return true;
		}

		if (propertyType == typeof(int))
		{
			var intValue = value is int typedValue ? typedValue : 0;
			if (EditorUIUtility.InputInt(property.Name, ref intValue))
			{
				property.SetValue(target, intValue);
				changed = true;
			}

			return true;
		}

		if (propertyType == typeof(long) ||
		    propertyType == typeof(uint) ||
		    propertyType == typeof(ulong) ||
		    propertyType == typeof(short) ||
		    propertyType == typeof(ushort) ||
		    propertyType == typeof(byte) ||
		    propertyType == typeof(sbyte) ||
		    propertyType == typeof(decimal))
		{
			var textValue = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "0";
			if (EditorUIUtility.InputText(property.Name, ref textValue))
			{
				if (TryConvertNumericValue(textValue, propertyType, out var numericValue))
				{
					property.SetValue(target, numericValue);
					changed = true;
				}
			}

			return true;
		}

		if (propertyType == typeof(float))
		{
			var floatValue = value is float typedValue ? typedValue : 0.0f;
			if (EditorUIUtility.InputFloat(property.Name, ref floatValue))
			{
				property.SetValue(target, floatValue);
				changed = true;
			}

			return true;
		}

		if (propertyType == typeof(double))
		{
			var doubleValue = value is double typedValue ? typedValue : 0.0;
			if (EditorUIUtility.InputDouble(property.Name, ref doubleValue))
			{
				property.SetValue(target, doubleValue);
				changed = true;
			}

			return true;
		}

		if (propertyType == typeof(bool))
		{
			var boolValue = value is bool typedValue && typedValue;
			if (EditorUIUtility.Checkbox(property.Name, ref boolValue))
			{
				property.SetValue(target, boolValue);
				changed = true;
			}

			return true;
		}

		if (propertyType == typeof(Vector2))
		{
			var vectorValue = value is Vector2 typedValue ? typedValue : Vector2.Zero;
			if (EditorUIUtility.InputVector2(property.Name, ref vectorValue))
			{
				property.SetValue(target, vectorValue);
				changed = true;
			}

			return true;
		}

		if (propertyType == typeof(Vector3))
		{
			var vectorValue = value is Vector3 typedValue ? typedValue : Vector3.Zero;
			if (EditorUIUtility.InputVector3(property.Name, ref vectorValue))
			{
				property.SetValue(target, vectorValue);
				changed = true;
			}

			return true;
		}

		if (propertyType == typeof(Vector4))
		{
			var vectorValue = value is Vector4 typedValue ? typedValue : Vector4.Zero;
			if (EditorUIUtility.InputVector4(property.Name, ref vectorValue))
			{
				property.SetValue(target, vectorValue);
				changed = true;
			}

			return true;
		}

		if (propertyType == typeof(ColorRgba))
		{
			var colorValue = value as ColorRgba ?? new ColorRgba();
			var vectorValue = colorValue.ToVector4();
			if (EditorUIUtility.ColorEdit4(property.Name, ref vectorValue))
			{
				property.SetValue(target, ColorRgba.FromVector4(vectorValue));
				changed = true;
			}

			return true;
		}

		if (propertyType.IsEnum)
		{
			changed = DrawEnumProperty(target, property, value);
			return true;
		}

		return false;
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

	private static bool TryConvertNumericValue(string textValue, Type propertyType, out object? numericValue)
	{
		var culture = System.Globalization.CultureInfo.InvariantCulture;
		if (propertyType == typeof(long) && long.TryParse(textValue, culture, out var longValue))
		{
			numericValue = longValue;
			return true;
		}

		if (propertyType == typeof(uint) && uint.TryParse(textValue, culture, out var uintValue))
		{
			numericValue = uintValue;
			return true;
		}

		if (propertyType == typeof(ulong) && ulong.TryParse(textValue, culture, out var ulongValue))
		{
			numericValue = ulongValue;
			return true;
		}

		if (propertyType == typeof(short) && short.TryParse(textValue, culture, out var shortValue))
		{
			numericValue = shortValue;
			return true;
		}

		if (propertyType == typeof(ushort) && ushort.TryParse(textValue, culture, out var ushortValue))
		{
			numericValue = ushortValue;
			return true;
		}

		if (propertyType == typeof(byte) && byte.TryParse(textValue, culture, out var byteValue))
		{
			numericValue = byteValue;
			return true;
		}

		if (propertyType == typeof(sbyte) && sbyte.TryParse(textValue, culture, out var sbyteValue))
		{
			numericValue = sbyteValue;
			return true;
		}

		if (propertyType == typeof(decimal) && decimal.TryParse(textValue, culture, out var decimalValue))
		{
			numericValue = decimalValue;
			return true;
		}

		numericValue = null;
		return false;
	}
}

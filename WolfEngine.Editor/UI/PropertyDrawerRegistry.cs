using System.Globalization;
using System.Numerics;
using ImGuiNET;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.UI;


public readonly record struct PropertyDrawerContext(
	string Label,
	Type ValueType,
	object? Value);

public readonly record struct PropertyDrawerResult(bool Handled, bool Changed, object? Value);

public interface IPropertyDrawerRegistry
{
	PropertyDrawerResult Draw(PropertyDrawerContext context);
}

public sealed class PropertyDrawerRegistry : IPropertyDrawerRegistry
{
	private readonly IEditorProjectService _projectService;

	public PropertyDrawerRegistry(IEditorProjectService projectService)
	{
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
	}

	public PropertyDrawerResult Draw(PropertyDrawerContext context)
	{
		var valueType = context.ValueType;
		var value = context.Value;

		if (TryDrawAssetLink(context, out var assetLinkResult))
		{
			return assetLinkResult;
		}

		if (valueType == typeof(string))
		{
			var stringValue = (string?)value ?? string.Empty;
			var changed = EditorUIUtility.InputText(context.Label, ref stringValue);
			return new PropertyDrawerResult(true, changed, stringValue);
		}

		if (valueType == typeof(bool))
		{
			var boolValue = value is bool typedValue && typedValue;
			var changed = EditorUIUtility.Checkbox(context.Label, ref boolValue);
			return new PropertyDrawerResult(true, changed, boolValue);
		}

		if (valueType == typeof(int))
		{
			var intValue = value is int typedValue ? typedValue : 0;
			var changed = EditorUIUtility.InputInt(context.Label, ref intValue);
			return new PropertyDrawerResult(true, changed, intValue);
		}

		if (valueType == typeof(float))
		{
			var floatValue = value is float typedValue ? typedValue : 0.0f;
			var changed = EditorUIUtility.InputFloat(context.Label, ref floatValue);
			return new PropertyDrawerResult(true, changed, floatValue);
		}

		if (valueType == typeof(double))
		{
			var doubleValue = value is double typedValue ? typedValue : 0.0;
			var changed = EditorUIUtility.InputDouble(context.Label, ref doubleValue);
			return new PropertyDrawerResult(true, changed, doubleValue);
		}

		if (IsTextBackedNumericType(valueType))
		{
			var textValue = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "0";
			var changed = EditorUIUtility.InputText(context.Label, ref textValue);
			if (changed == false)
			{
				return new PropertyDrawerResult(true, false, value);
			}

			if (TryConvertNumericValue(textValue, valueType, out var numericValue))
			{
				return new PropertyDrawerResult(true, true, numericValue);
			}

			return new PropertyDrawerResult(true, false, value);
		}

		if (valueType == typeof(Vector2))
		{
			var vectorValue = value is Vector2 typedValue ? typedValue : Vector2.Zero;
			var changed = EditorUIUtility.InputVector2(context.Label, ref vectorValue);
			return new PropertyDrawerResult(true, changed, vectorValue);
		}

		if (valueType == typeof(Vector3))
		{
			var vectorValue = value is Vector3 typedValue ? typedValue : Vector3.Zero;
			var changed = EditorUIUtility.InputVector3(context.Label, ref vectorValue);
			return new PropertyDrawerResult(true, changed, vectorValue);
		}

		if (valueType == typeof(Vector4))
		{
			var vectorValue = value is Vector4 typedValue ? typedValue : Vector4.Zero;
			var changed = EditorUIUtility.InputVector4(context.Label, ref vectorValue);
			return new PropertyDrawerResult(true, changed, vectorValue);
		}

		if (valueType == typeof(Quaternion))
		{
			var quaternionValue = value is Quaternion typedValue ? typedValue : Quaternion.Identity;
			var vectorValue = new Vector4(quaternionValue.X, quaternionValue.Y, quaternionValue.Z, quaternionValue.W);
			var changed = EditorUIUtility.InputVector4(context.Label, ref vectorValue);
			return new PropertyDrawerResult(true, changed, new Quaternion(vectorValue.X, vectorValue.Y, vectorValue.Z, vectorValue.W));
		}

		if (valueType == typeof(ColorRGBA))
		{
			var colorValue = (ColorRGBA)(value ?? ColorRGBA.White);
			var vectorValue = colorValue.ToVector4();
			var changed = EditorUIUtility.ColorEdit4(context.Label, ref vectorValue);
			return new PropertyDrawerResult(true, changed, ColorRGBA.FromVector4(vectorValue));
		}

		if (valueType.IsEnum)
		{
			return DrawEnum(context.Label, valueType, value);
		}

		return new PropertyDrawerResult(false, false, value);
	}

	private bool TryDrawAssetLink(PropertyDrawerContext context, out PropertyDrawerResult result)
	{
		var valueType = context.ValueType;
		if (valueType.IsGenericType == false || valueType.GetGenericTypeDefinition() != typeof(AssetLink<>))
		{
			result = default;
			return false;
		}

		var assetType = valueType.GetGenericArguments()[0];
		if (typeof(IDataAsset).IsAssignableFrom(assetType) == false)
		{
			result = new PropertyDrawerResult(false, false, context.Value);
			return true;
		}

		var currentId = GetAssetLinkId(valueType, context.Value);
		var currentTypeName = assetType.AssemblyQualifiedName ?? string.Empty;
		var candidates = _projectService.HasOpenProject
			? _projectService.CurrentAssetDatabase.Assets
				.Where(asset => asset.Type == AssetType.DataAsset &&
				                string.Equals(asset.DataAssetSummary?.DataAssetType, currentTypeName, StringComparison.Ordinal))
				.OrderBy(asset => asset.Name, StringComparer.OrdinalIgnoreCase)
				.ToList()
			: [];

		var nextId = currentId;
		var changed = false;
		EditorUIUtility.Combo(context.Label, GetAssetLinkPreviewLabel(currentId, currentTypeName), () =>
		{
			var noneSelected = currentId == Guid.Empty;
			if (ImGui.Selectable("None", noneSelected))
			{
				nextId = Guid.Empty;
				changed = currentId != Guid.Empty;
			}

			if (noneSelected)
			{
				ImGui.SetItemDefaultFocus();
			}

			for (var i = 0; i < candidates.Count; i++)
			{
				var candidate = candidates[i];
				var isSelected = candidate.Id == currentId;
				if (ImGui.Selectable(candidate.Name, isSelected))
				{
					nextId = candidate.Id;
					changed = candidate.Id != currentId;
				}

				if (isSelected)
				{
					ImGui.SetItemDefaultFocus();
				}
			}
		});

		result = changed
			? new PropertyDrawerResult(true, true, CreateAssetLinkValue(valueType, nextId))
			: new PropertyDrawerResult(true, false, context.Value);
		return true;
	}

	private static PropertyDrawerResult DrawEnum(string label, Type enumType, object? value)
	{
		var changed = false;
		var nextValue = value;
		var preview = value?.ToString() ?? string.Empty;
		EditorUIUtility.Combo(label, preview, () =>
		{
			foreach (var candidate in Enum.GetValues(enumType))
			{
				var candidateName = candidate?.ToString() ?? string.Empty;
				var isSelected = Equals(candidate, value);
				if (ImGui.Selectable(candidateName, isSelected))
				{
					nextValue = candidate;
					changed = true;
				}

				if (isSelected)
				{
					ImGui.SetItemDefaultFocus();
				}
			}
		});

		return new PropertyDrawerResult(true, changed, nextValue);
	}

	private static bool IsTextBackedNumericType(Type valueType)
	{
		return valueType == typeof(long) ||
		       valueType == typeof(uint) ||
		       valueType == typeof(ulong) ||
		       valueType == typeof(short) ||
		       valueType == typeof(ushort) ||
		       valueType == typeof(byte) ||
		       valueType == typeof(sbyte) ||
		       valueType == typeof(decimal);
	}

	private static bool TryConvertNumericValue(string textValue, Type valueType, out object? numericValue)
	{
		var culture = CultureInfo.InvariantCulture;
		if (valueType == typeof(long) && long.TryParse(textValue, culture, out var longValue))
		{
			numericValue = longValue;
			return true;
		}

		if (valueType == typeof(uint) && uint.TryParse(textValue, culture, out var uintValue))
		{
			numericValue = uintValue;
			return true;
		}

		if (valueType == typeof(ulong) && ulong.TryParse(textValue, culture, out var ulongValue))
		{
			numericValue = ulongValue;
			return true;
		}

		if (valueType == typeof(short) && short.TryParse(textValue, culture, out var shortValue))
		{
			numericValue = shortValue;
			return true;
		}

		if (valueType == typeof(ushort) && ushort.TryParse(textValue, culture, out var ushortValue))
		{
			numericValue = ushortValue;
			return true;
		}

		if (valueType == typeof(byte) && byte.TryParse(textValue, culture, out var byteValue))
		{
			numericValue = byteValue;
			return true;
		}

		if (valueType == typeof(sbyte) && sbyte.TryParse(textValue, culture, out var sbyteValue))
		{
			numericValue = sbyteValue;
			return true;
		}

		if (valueType == typeof(decimal) && decimal.TryParse(textValue, culture, out var decimalValue))
		{
			numericValue = decimalValue;
			return true;
		}

		numericValue = null;
		return false;
	}

	private string GetAssetLinkPreviewLabel(Guid assetId, string expectedTypeName)
	{
		if (assetId == Guid.Empty)
		{
			return "None";
		}

		if (_projectService.HasOpenProject == false)
		{
			return "Missing";
		}

		if (_projectService.TryGetAsset(assetId, out var asset) == false)
		{
			return "Missing";
		}

		if (asset.Type != AssetType.DataAsset ||
		    string.Equals(asset.DataAssetSummary?.DataAssetType, expectedTypeName, StringComparison.Ordinal) == false)
		{
			return "Invalid";
		}

		return asset.Name;
	}

	private static Guid GetAssetLinkId(Type valueType, object? value)
	{
		if (value is null)
		{
			return Guid.Empty;
		}

		var idField = valueType.GetField(nameof(AssetLink<IDataAsset>.Id))
			?? throw new InvalidOperationException($"Asset link type '{valueType.FullName}' is missing its Id field.");
		return idField.GetValue(value) is Guid id ? id : Guid.Empty;
	}

	private static object CreateAssetLinkValue(Type valueType, Guid assetId)
	{
		var boxedValue = Activator.CreateInstance(valueType)
			?? throw new InvalidOperationException($"Failed to create asset link value for '{valueType.FullName}'.");
		var idField = valueType.GetField(nameof(AssetLink<IDataAsset>.Id))
			?? throw new InvalidOperationException($"Asset link type '{valueType.FullName}' is missing its Id field.");
		idField.SetValue(boxedValue, assetId);
		return boxedValue;
	}
}

using System.Collections.Concurrent;
using System.Reflection;
using System.Numerics;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.UI;

public static class RuntimeComponentFieldEditor
{
	private static readonly ConcurrentDictionary<Type, FieldInfo[]> EditableFields = new();

	public readonly record struct FieldEdit(IReadOnlyList<FieldInfo> Path, object? Value);

	public static bool ApplyPublicFields(
		Type componentType,
		IPropertyDrawerRegistry propertyDrawerRegistry,
		ref object componentValue,
		EditorScene? scene = null,
		Entity? ownerEntity = null,
		AssetLinkSelectionButton? assetLinkSelectionButton = null,
		ICollection<FieldEdit>? edits = null,
		Func<IReadOnlyList<FieldInfo>, bool>? isMixed = null)
	{
		ArgumentNullException.ThrowIfNull(componentType);
		ArgumentNullException.ThrowIfNull(propertyDrawerRegistry);
		ArgumentNullException.ThrowIfNull(componentValue);

		return ApplyEditableFields(
			componentType,
			propertyDrawerRegistry,
			componentValue,
			scene,
			ownerEntity,
			assetLinkSelectionButton,
			edits,
			isMixed,
			[]);
	}

	public static void ApplyFieldEdit(object value, FieldEdit edit)
	{
		ArgumentNullException.ThrowIfNull(value);
		ApplyFieldEdit(value, edit.Path, 0, edit.Value);
	}

	public static void ClearCachedFields()
	{
		EditableFields.Clear();
	}

	private static FieldInfo[] GetEditableFields(Type componentType)
	{
		return componentType
			.GetFields(BindingFlags.Instance | BindingFlags.Public)
			.Where(field => field.IsInitOnly == false &&
			                Attribute.IsDefined(field, typeof(NotSerializedAttribute)) == false &&
			                Attribute.IsDefined(field, typeof(HideFromEditorAttribute)) == false)
			.ToArray();
	}

	private static bool ApplyEditableFields(
		Type valueType,
		IPropertyDrawerRegistry propertyDrawerRegistry,
		object value,
		EditorScene? scene,
		Entity? ownerEntity,
		AssetLinkSelectionButton? assetLinkSelectionButton,
		ICollection<FieldEdit>? edits,
		Func<IReadOnlyList<FieldInfo>, bool>? isMixed,
		IReadOnlyList<FieldInfo> parentPath)
	{
		var changed = false;
		foreach (var field in EditableFields.GetOrAdd(valueType, GetEditableFields))
		{
			var path = parentPath.Concat([field]).ToArray();
			var fieldValue = field.GetValue(value);
			if (ShouldDrawAsStructGroup(field.FieldType) && HasDrawableFields(field.FieldType))
			{
				RenderStructGroup(field, () =>
				{
					fieldValue ??= Activator.CreateInstance(field.FieldType);
					var nestedValue = fieldValue;
					if (nestedValue is not null &&
					ApplyEditableFields(field.FieldType, propertyDrawerRegistry, nestedValue, scene, ownerEntity, assetLinkSelectionButton, edits, isMixed, path))
					{
						field.SetValue(value, nestedValue);
						changed = true;
					}
				});

				continue;
			}

			var drawResult = propertyDrawerRegistry.Draw(new PropertyDrawerContext(
				field.Name,
				field.FieldType,
				fieldValue,
				scene,
				ownerEntity,
				field,
				assetLinkSelectionButton,
				isMixed?.Invoke(path) ?? false));
			if (drawResult.Handled == false || drawResult.Changed == false)
			{
				continue;
			}

			field.SetValue(value, drawResult.Value);
			edits?.Add(new FieldEdit(path, drawResult.Value));
			changed = true;
		}

		return changed;
	}

	private static void ApplyFieldEdit(object value, IReadOnlyList<FieldInfo> path, int index, object? leafValue)
	{
		var field = path[index];
		if (index == path.Count - 1)
		{
			field.SetValue(value, leafValue);
			return;
		}

		var nestedValue = field.GetValue(value) ?? Activator.CreateInstance(field.FieldType)
			?? throw new InvalidOperationException($"Unable to create '{field.FieldType.FullName}'.");
		ApplyFieldEdit(nestedValue, path, index + 1, leafValue);
		field.SetValue(value, nestedValue);
	}

	private static bool HasDrawableFields(Type valueType)
	{
		foreach (var field in EditableFields.GetOrAdd(valueType, GetEditableFields))
		{
			if (ShouldDrawAsStructGroup(field.FieldType))
			{
				if (HasDrawableFields(field.FieldType))
				{
					return true;
				}

				continue;
			}

			if (CanDrawLeafType(field.FieldType))
			{
				return true;
			}
		}

		return false;
	}

	private static bool ShouldDrawAsStructGroup(Type fieldType)
	{
		return fieldType.IsValueType &&
		       CanDrawLeafType(fieldType) == false;
	}

	private static bool CanDrawLeafType(Type valueType)
	{
		if (valueType == typeof(string) ||
		    valueType == typeof(bool) ||
		    valueType == typeof(int) ||
		    valueType == typeof(float) ||
		    valueType == typeof(double) ||
		    valueType == typeof(long) ||
		    valueType == typeof(uint) ||
		    valueType == typeof(ulong) ||
		    valueType == typeof(short) ||
		    valueType == typeof(ushort) ||
		    valueType == typeof(byte) ||
		    valueType == typeof(sbyte) ||
		    valueType == typeof(decimal) ||
		    valueType == typeof(Entity) ||
		    valueType == typeof(Vector2) ||
		    valueType == typeof(Vector3) ||
		    valueType == typeof(Vector4) ||
		    valueType == typeof(Quaternion) ||
		    valueType == typeof(ColorRGBA) ||
		    valueType.IsEnum)
		{
			return true;
		}

		return valueType.IsGenericType &&
		       valueType.GetGenericTypeDefinition() == typeof(AssetRef<>);
	}

	private static void RenderStructGroup(FieldInfo field, Action draw)
	{
		if (ImGuiNET.ImGui.GetCurrentContext() == IntPtr.Zero)
		{
			draw();
			return;
		}

		ImGuiNET.ImGui.PushID(field.Name);
		try
		{
			if (EditorUIUtility.CollapsingHeader(field.Name) == false)
			{
				return;
			}

			EditorUIUtility.BeginIndentedGroup();
			try
			{
				draw();
			}
			finally
			{
				EditorUIUtility.EndIndentedGroup();
			}
		}
		finally
		{
			ImGuiNET.ImGui.PopID();
		}
	}
}

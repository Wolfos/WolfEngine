using System.Collections.Concurrent;
using System.Reflection;
using WolfEngine.ECS;

namespace WolfEngine.Editor.UI;

public static class RuntimeComponentFieldEditor
{
	private static readonly ConcurrentDictionary<Type, FieldInfo[]> EditableFields = new();

	public static bool ApplyPublicFields(Type componentType, IPropertyDrawerRegistry propertyDrawerRegistry, ref object componentValue)
	{
		ArgumentNullException.ThrowIfNull(componentType);
		ArgumentNullException.ThrowIfNull(propertyDrawerRegistry);
		ArgumentNullException.ThrowIfNull(componentValue);

		var changed = false;
		foreach (var field in EditableFields.GetOrAdd(componentType, GetEditableFields))
		{
			var drawResult = propertyDrawerRegistry.Draw(new PropertyDrawerContext(
				field.Name,
				field.FieldType,
				field.GetValue(componentValue)));
			if (drawResult.Handled == false || drawResult.Changed == false)
			{
				continue;
			}

			field.SetValue(componentValue, drawResult.Value);
			changed = true;
		}

		return changed;
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
}

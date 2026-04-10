using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Numerics;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.UI;

public static class RuntimeComponentFieldEditor
{
	private static readonly ConcurrentDictionary<Type, FieldInfo[]> EditableFields = new();

	public static bool ApplyPublicFields(Type componentType, IPropertyDrawerRegistry propertyDrawerRegistry, ref object componentValue, EditorScene? scene = null, Entity? ownerEntity = null)
	{
		ArgumentNullException.ThrowIfNull(componentType);
		ArgumentNullException.ThrowIfNull(propertyDrawerRegistry);
		ArgumentNullException.ThrowIfNull(componentValue);

		return ApplyEditableFields(
			componentType,
			propertyDrawerRegistry,
			componentValue,
			scene,
			ownerEntity);
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
		Entity? ownerEntity)
	{
		var changed = false;
		foreach (var field in EditableFields.GetOrAdd(valueType, GetEditableFields))
		{
			var fieldValue = field.GetValue(value);
			if (ShouldDrawAsStructGroup(field.FieldType) && HasDrawableFields(field.FieldType))
			{
				RenderStructGroup(field, () =>
				{
					fieldValue ??= Activator.CreateInstance(field.FieldType);
					var nestedValue = fieldValue;
					if (nestedValue is not null &&
					    ApplyEditableFields(field.FieldType, propertyDrawerRegistry, nestedValue, scene, ownerEntity))
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
				field));
			if (drawResult.Handled == false || drawResult.Changed == false)
			{
				continue;
			}

			field.SetValue(value, drawResult.Value);
			changed = true;
		}

		return changed;
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
		ImGuiNET.ImGui.PushID(field.Name);
		try
		{
			if (ImGuiNET.ImGui.GetCurrentContext() == IntPtr.Zero)
			{
				draw();
				return;
			}

			if (EditorUIUtility.CollapsingHeader(field.Name, isOpenByDefault: true) == false)
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

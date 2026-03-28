using System.Reflection;
using System.Text.Json;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.Projects;

internal static class ProjectTypeStateTransferUtility
{
	public static object DeserializeWithFieldMerge(JsonElement data, Type targetType)
	{
		ArgumentNullException.ThrowIfNull(targetType);

		if (data.ValueKind != JsonValueKind.Object)
		{
			return data.Deserialize(targetType, AssetJson.GetSerializerOptions(targetType))
			       ?? CreateDefaultValue(targetType);
		}

		var value = CreateDefaultValue(targetType);
		foreach (var field in targetType.GetFields(BindingFlags.Instance | BindingFlags.Public))
		{
			if (field.IsInitOnly || data.TryGetProperty(field.Name, out var fieldData) == false)
			{
				continue;
			}

			try
			{
				var fieldValue = fieldData.Deserialize(field.FieldType, AssetJson.GetSerializerOptions(field.FieldType));
				field.SetValue(value, fieldValue);
			}
			catch
			{
			}
		}

		foreach (var property in targetType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
		{
			if (property.CanWrite == false ||
			    property.GetIndexParameters().Length != 0 ||
			    property.SetMethod?.IsPublic != true ||
			    data.TryGetProperty(property.Name, out var propertyData) == false)
			{
				continue;
			}

			try
			{
				var propertyValue = propertyData.Deserialize(property.PropertyType, AssetJson.GetSerializerOptions(property.PropertyType));
				property.SetValue(value, propertyValue);
			}
			catch
			{
			}
		}

		return value;
	}

	public static object CreateDefaultValue(Type type)
	{
		ArgumentNullException.ThrowIfNull(type);
		return Activator.CreateInstance(type)
		       ?? throw new InvalidOperationException($"Failed to create a default instance for '{type.FullName}'.");
	}
}

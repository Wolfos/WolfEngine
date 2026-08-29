using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Editor.Projects;

namespace WolfEngine.Editor;

internal static class EditorEntityReferenceUtility
{
	private const string EntityReferenceIdPropertyName = "__entityRefId";
	private static readonly ConcurrentDictionary<Type, bool> ContainsEntityReferencesCache = new();

	public static JsonElement SerializeComponentData(EditorScene scene, Type componentType, object? componentValue)
	{
		ArgumentNullException.ThrowIfNull(scene);
		ArgumentNullException.ThrowIfNull(componentType);

		return SerializeValue(componentValue, componentType, entity =>
		{
			if (entity.IsValid == false || scene.World.IsAlive(entity) == false)
			{
				return null;
			}

			if (scene.EntityIds.TryGetValue(entity, out var entityId) && entityId != Guid.Empty)
			{
				return entityId;
			}

			entityId = Guid.NewGuid();
			scene.EntityIds[entity] = entityId;
			return entityId;
		});
	}

	public static JsonElement SerializeValue(object? value, Type valueType, Func<Entity, Guid?> entityIdResolver)
	{
		ArgumentNullException.ThrowIfNull(valueType);
		ArgumentNullException.ThrowIfNull(entityIdResolver);

		if (ContainsEntityReferences(valueType) == false)
		{
			return JsonSerializer.SerializeToElement(value, valueType, AssetJson.GetSerializerOptions(valueType));
		}

		var node = SerializeNode(value, valueType, entityIdResolver);
		return SerializeNodeToElement(node);
	}

	public static object? DeserializeComponentData(EditorScene scene, JsonElement data, Type componentType)
	{
		ArgumentNullException.ThrowIfNull(scene);
		ArgumentNullException.ThrowIfNull(componentType);

		return DeserializeValue(data, componentType, entityId =>
		{
			foreach (var entry in scene.EntityIds)
			{
				if (entry.Value == entityId && scene.World.IsAlive(entry.Key))
				{
					return entry.Key;
				}
			}

			return null;
		});
	}

	public static object? DeserializeValue(JsonElement data, Type targetType, Func<Guid, Entity?> entityResolver)
	{
		ArgumentNullException.ThrowIfNull(targetType);
		ArgumentNullException.ThrowIfNull(entityResolver);

		if (ContainsEntityReferences(targetType) == false)
		{
			return data.Deserialize(targetType, AssetJson.GetSerializerOptions(targetType));
		}

		return DeserializeEntityAwareValue(data, targetType, entityResolver);
	}

	/// <summary>
	/// Rewrites every serialized entity reference whose persistent id appears in <paramref name="entityIdMap"/>.
	/// References to entities outside the map are left untouched so they keep pointing at their original target.
	/// </summary>
	public static JsonElement RemapEntityReferences(JsonElement data, IReadOnlyDictionary<Guid, Guid> entityIdMap)
	{
		ArgumentNullException.ThrowIfNull(entityIdMap);
		if (entityIdMap.Count == 0 || data.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
		{
			return data;
		}

		var node = JsonNode.Parse(data.GetRawText());
		if (RemapEntityReferenceNodes(node, entityIdMap) == false)
		{
			return data;
		}

		return SerializeNodeToElement(node);
	}

	public static bool ContainsEntityReferences(Type type)
	{
		ArgumentNullException.ThrowIfNull(type);
		return ContainsEntityReferencesCache.GetOrAdd(type, static candidate => ContainsEntityReferences(candidate, new HashSet<Type>()));
	}

	private static bool ContainsEntityReferences(Type type, HashSet<Type> stack)
	{
		if (type == typeof(Entity))
		{
			return true;
		}

		var nullableType = Nullable.GetUnderlyingType(type);
		if (nullableType is not null)
		{
			return ContainsEntityReferences(nullableType, stack);
		}

		if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type == typeof(Guid))
		{
			return false;
		}

		if (type.IsArray)
		{
			return ContainsEntityReferences(type.GetElementType()!, stack);
		}

		if (TryGetCollectionElementType(type, out var elementType))
		{
			return ContainsEntityReferences(elementType, stack);
		}

		if (stack.Add(type) == false)
		{
			return false;
		}

		try
		{
			foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
			{
				if (IsAlwaysIgnored(field))
				{
					continue;
				}

				if (ContainsEntityReferences(field.FieldType, stack))
				{
					return true;
				}
			}

			foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
			{
				if (property.GetIndexParameters().Length != 0 ||
				    property.CanRead == false ||
				    IsAlwaysIgnored(property))
				{
					continue;
				}

				if (ContainsEntityReferences(property.PropertyType, stack))
				{
					return true;
				}
			}

			return false;
		}
		finally
		{
			stack.Remove(type);
		}
	}

	private static JsonNode? SerializeNode(object? value, Type valueType, Func<Entity, Guid?> entityIdResolver)
	{
		if (value is null)
		{
			return null;
		}

		var nullableType = Nullable.GetUnderlyingType(valueType);
		if (nullableType is not null)
		{
			return SerializeNode(value, nullableType, entityIdResolver);
		}

		if (valueType == typeof(Entity))
		{
			var entity = (Entity)value;
			var entityId = entity.IsValid ? entityIdResolver(entity) : null;
			if (entityId is not { } resolvedEntityId || resolvedEntityId == Guid.Empty)
			{
				return null;
			}

			return new JsonObject
			{
				[EntityReferenceIdPropertyName] = resolvedEntityId.ToString()
			};
		}

		if (ContainsEntityReferences(valueType) == false)
		{
			return JsonSerializer.SerializeToNode(value, valueType, AssetJson.GetSerializerOptions(valueType));
		}

		if (valueType.IsArray)
		{
			var elementType = valueType.GetElementType()!;
			var array = new JsonArray();
			foreach (var item in (IEnumerable)value)
			{
				array.Add(SerializeNode(item, elementType, entityIdResolver));
			}

			return array;
		}

		if (TryGetCollectionElementType(valueType, out var collectionElementType) && value is IEnumerable enumerable)
		{
			var array = new JsonArray();
			foreach (var item in enumerable)
			{
				array.Add(SerializeNode(item, collectionElementType, entityIdResolver));
			}

			return array;
		}

		var result = new JsonObject();
		foreach (var field in valueType.GetFields(BindingFlags.Instance | BindingFlags.Public))
		{
			var fieldValue = field.GetValue(value);
			if (ShouldSkipWriting(field, fieldValue, field.FieldType))
			{
				continue;
			}

			result[field.Name] = SerializeNode(fieldValue, field.FieldType, entityIdResolver);
		}

		foreach (var property in valueType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
		{
			if (property.GetIndexParameters().Length != 0 || property.CanRead == false)
			{
				continue;
			}

			var propertyValue = property.GetValue(value);
			if (ShouldSkipWriting(property, propertyValue, property.PropertyType))
			{
				continue;
			}

			result[property.Name] = SerializeNode(propertyValue, property.PropertyType, entityIdResolver);
		}

		return result;
	}

	private static bool RemapEntityReferenceNodes(JsonNode? node, IReadOnlyDictionary<Guid, Guid> entityIdMap)
	{
		switch (node)
		{
			case JsonObject entityReference when TryGetEntityReferenceId(entityReference, out var entityId):
			{
				if (entityIdMap.TryGetValue(entityId, out var remappedEntityId) == false)
				{
					return false;
				}

				entityReference[EntityReferenceIdPropertyName] = remappedEntityId.ToString();
				return true;
			}
			case JsonObject jsonObject:
			{
				var changed = false;
				foreach (var property in jsonObject)
				{
					changed |= RemapEntityReferenceNodes(property.Value, entityIdMap);
				}

				return changed;
			}
			case JsonArray jsonArray:
			{
				var changed = false;
				for (var i = 0; i < jsonArray.Count; i++)
				{
					changed |= RemapEntityReferenceNodes(jsonArray[i], entityIdMap);
				}

				return changed;
			}
			default:
				return false;
		}
	}

	private static bool TryGetEntityReferenceId(JsonObject jsonObject, out Guid entityId)
	{
		entityId = Guid.Empty;
		return jsonObject.Count == 1
		       && jsonObject.TryGetPropertyValue(EntityReferenceIdPropertyName, out var entityIdNode)
		       && entityIdNode is JsonValue entityIdValue
		       && entityIdValue.TryGetValue<string>(out var entityIdText)
		       && Guid.TryParse(entityIdText, out entityId);
	}

	private static JsonElement SerializeNodeToElement(JsonNode? node)
	{
		using var document = JsonDocument.Parse(node?.ToJsonString() ?? "null");
		return document.RootElement.Clone();
	}

	private static object? DeserializeEntityAwareValue(JsonElement data, Type targetType, Func<Guid, Entity?> entityResolver)
	{
		if (data.ValueKind == JsonValueKind.Null)
		{
			if (Nullable.GetUnderlyingType(targetType) is not null || targetType.IsValueType == false)
			{
				return null;
			}

			return Activator.CreateInstance(targetType);
		}

		var nullableType = Nullable.GetUnderlyingType(targetType);
		if (nullableType is not null)
		{
			return DeserializeEntityAwareValue(data, nullableType, entityResolver);
		}

		if (targetType == typeof(Entity))
		{
			if (data.ValueKind == JsonValueKind.Object &&
			    data.TryGetProperty(EntityReferenceIdPropertyName, out var entityIdData) &&
			    entityIdData.ValueKind == JsonValueKind.String &&
			    Guid.TryParse(entityIdData.GetString(), out var entityId))
			{
				return entityResolver(entityId) ?? default(Entity);
			}

			return default(Entity);
		}

		if (ContainsEntityReferences(targetType) == false)
		{
			return data.Deserialize(targetType, AssetJson.GetSerializerOptions(targetType));
		}

		if (targetType.IsArray && data.ValueKind == JsonValueKind.Array)
		{
			var elementType = targetType.GetElementType()!;
			var items = new List<object?>();
			foreach (var item in data.EnumerateArray())
			{
				items.Add(DeserializeEntityAwareValue(item, elementType, entityResolver));
			}

			var array = Array.CreateInstance(elementType, items.Count);
			for (var index = 0; index < items.Count; index++)
			{
				array.SetValue(items[index], index);
			}

			return array;
		}

		if (TryGetCollectionElementType(targetType, out var collectionElementType) && data.ValueKind == JsonValueKind.Array)
		{
			var collection = CreateCollectionInstance(targetType, collectionElementType);
			foreach (var item in data.EnumerateArray())
			{
				collection.Add(DeserializeEntityAwareValue(item, collectionElementType, entityResolver));
			}

			return collection.Collection;
		}

		if (data.ValueKind != JsonValueKind.Object)
		{
			return data.Deserialize(targetType, AssetJson.GetSerializerOptions(targetType));
		}

		var value = ProjectTypeStateTransferUtility.CreateDefaultValue(targetType);
		foreach (var field in targetType.GetFields(BindingFlags.Instance | BindingFlags.Public))
		{
			if (field.IsInitOnly ||
			    IsAlwaysIgnored(field) ||
			    data.TryGetProperty(field.Name, out var fieldData) == false)
			{
				continue;
			}

			try
			{
				field.SetValue(value, DeserializeEntityAwareValue(fieldData, field.FieldType, entityResolver));
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
			    IsAlwaysIgnored(property) ||
			    data.TryGetProperty(property.Name, out var propertyData) == false)
			{
				continue;
			}

			try
			{
				property.SetValue(value, DeserializeEntityAwareValue(propertyData, property.PropertyType, entityResolver));
			}
			catch
			{
			}
		}

		if (value is IJsonOnDeserialized callback)
		{
			callback.OnDeserialized();
		}

		return value;
	}

	private static bool IsAlwaysIgnored(MemberInfo member)
	{
		if (Attribute.IsDefined(member, typeof(NotSerializedAttribute)))
		{
			return true;
		}

		return member.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition == JsonIgnoreCondition.Always;
	}

	private static bool ShouldSkipWriting(MemberInfo member, object? value, Type valueType)
	{
		if (Attribute.IsDefined(member, typeof(NotSerializedAttribute)))
		{
			return true;
		}

		return member.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition switch
		{
			JsonIgnoreCondition.Always => true,
			JsonIgnoreCondition.WhenWritingNull => value is null,
			JsonIgnoreCondition.WhenWritingDefault => IsDefaultValue(value, valueType),
			_ => false
		};
	}

	private static bool IsDefaultValue(object? value, Type valueType)
	{
		if (value is null)
		{
			return true;
		}

		return valueType.IsValueType && value.Equals(Activator.CreateInstance(valueType));
	}

	private static bool TryGetCollectionElementType(Type type, out Type elementType)
	{
		elementType = null!;
		if (type == typeof(string))
		{
			return false;
		}

		if (type.IsGenericType == false)
		{
			return false;
		}

		var genericTypeDefinition = type.GetGenericTypeDefinition();
		if (genericTypeDefinition != typeof(List<>) &&
		    genericTypeDefinition != typeof(IList<>) &&
		    genericTypeDefinition != typeof(IReadOnlyList<>) &&
		    genericTypeDefinition != typeof(IEnumerable<>))
		{
			return false;
		}

		elementType = type.GetGenericArguments()[0];
		return true;
	}

	private static CollectionInstance CreateCollectionInstance(Type collectionType, Type elementType)
	{
		var backingType = collectionType.IsInterface || collectionType.IsAbstract
			? typeof(List<>).MakeGenericType(elementType)
			: collectionType;
		var collection = Activator.CreateInstance(backingType)
			?? throw new InvalidOperationException($"Failed to create collection instance for '{collectionType.FullName}'.");
		var addMethod = backingType.GetMethod("Add", [elementType])
			?? throw new InvalidOperationException($"Collection type '{backingType.FullName}' does not expose an Add method.");
		return new CollectionInstance(collection, addMethod);
	}

	private readonly record struct CollectionInstance(object Collection, MethodInfo AddMethod)
	{
		public void Add(object? value)
		{
			AddMethod.Invoke(Collection, [value]);
		}
	}
}

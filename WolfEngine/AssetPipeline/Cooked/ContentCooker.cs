using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using WolfEngine.ECS;
using WolfEngine.Serialization;

namespace WolfEngine.AssetPipeline;

/// <summary>
/// Cooks authored JSON content to the binary formats the runtime reads. Every value is read with the editor's own
/// JSON options, so cooked content holds exactly what the editor would load, then written with the generated
/// serializer for its type.
/// </summary>
/// <remarks>
/// Cooking reads JSON with reflection. It runs in the build tool, never in a shipped game, which only reads the
/// binary output.
/// </remarks>
[RequiresUnreferencedCode("Cooking reads authored JSON with reflection-based System.Text.Json.")]
[RequiresDynamicCode("Cooking reads authored JSON with reflection-based System.Text.Json.")]
public sealed class ContentCooker
{
	private readonly Func<string, string, Type?> _resolveType;
	private readonly Dictionary<JsonSerializerOptions, JsonSerializerOptions> _componentOptions = new();
	private readonly Dictionary<JsonSerializerOptions, JsonSerializerOptions> _dataAssetOptions = new();
	private readonly CookingEntityReferences _entityReferences = new();

	/// <param name="resolveType">
	/// Maps a saved stable type id and type name to a loaded type, or null when it cannot be found. Gameplay types
	/// must resolve to the gameplay assembly whose serializers are registered.
	/// </param>
	public ContentCooker(Func<string, string, Type?> resolveType)
	{
		_resolveType = resolveType ?? throw new ArgumentNullException(nameof(resolveType));
	}

	public byte[] CookSceneCell(Guid cellId, SceneCellDocument document)
	{
		ArgumentNullException.ThrowIfNull(document);

		if (document.Version != SceneCellDocument.CurrentVersion)
		{
			throw new InvalidDataException(
				$"Scene cell '{cellId}' has unsupported version {document.Version}. Expected {SceneCellDocument.CurrentVersion}.");
		}

		var entities = new List<CookedEntity>(document.Entities.Count);
		foreach (var source in document.Entities)
		{
			var components = new List<CookedComponent>(source.Components.Count);
			foreach (var component in source.Components)
			{
				var componentType = ResolveComponentType(component, $"scene cell '{cellId}' entity '{source.EntityId}'");
				if (componentType is null)
				{
					continue;
				}

				var serializedType = SerializedTypeRegistry.GetComponent(componentType);
				components.Add(new CookedComponent(serializedType, CookComponent(component, serializedType)));
			}

			entities.Add(new CookedEntity
			{
				EntityId = source.EntityId,
				ParentEntityId = source.ParentEntityId,
				Name = source.HasName ? source.Name : null,
				Enabled = source.Enabled,
				LocalTransform = source.LocalTransform,
				Components = components
			});
		}

		return CookedSceneCell.Write(entities);
	}

	public byte[] CookPrefab(Guid prefabId, PrefabDocument document)
	{
		return CookedPrefab.Write(
			prefabId,
			document,
			component => ResolveComponentType(component, $"prefab '{prefabId}'"),
			CookComponent);
	}

	public byte[] CookDataAsset(Guid assetId, DataAssetFile file)
	{
		ArgumentNullException.ThrowIfNull(file);

		if (file.Version != DataAssetFile.CurrentVersion)
		{
			throw new InvalidDataException(
				$"Data asset '{assetId}' has unsupported version {file.Version}. Expected {DataAssetFile.CurrentVersion}.");
		}

		var type = _resolveType(file.DataAssetTypeId, file.DataAssetType)
		           ?? throw new InvalidDataException(
			           $"Data asset '{assetId}' has type '{file.DataAssetTypeId}', which is not available to the build.");
		var value = file.Data.Deserialize(type, GetDataAssetOptions(type))
		            ?? throw new InvalidDataException($"Data asset '{assetId}' has no data.");

		return CookedDataAsset.Write(value);
	}

	/// <summary>
	/// Resolves a saved component's type. Component types marked <see cref="NotSerializedAttribute"/> are never
	/// cooked; any other component that cannot be resolved fails the build, because a missing component is far
	/// harder to track down in a running game.
	/// </summary>
	private Type? ResolveComponentType(ComponentDocument component, string owner)
	{
		var type = _resolveType(component.TypeId, component.Type)
		           ?? throw new InvalidDataException(
			           $"{owner} has a '{component.TypeId}' component, which is not available to the build.");
		if (type.IsValueType == false || typeof(IEntityComponent).IsAssignableFrom(type) == false)
		{
			throw new InvalidDataException($"{owner} component '{type.FullName}' is not an entity component struct.");
		}

		if (Attribute.IsDefined(type, typeof(NotSerializedAttribute)))
		{
			return null;
		}

		return type;
	}

	private byte[] CookComponent(ComponentDocument component, SerializedComponentType serializedType)
	{
		var type = serializedType.Type;
		object value;
		if (component.Data.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
		{
			value = Activator.CreateInstance(type)!;
		}
		else
		{
			value = component.Data.Deserialize(type, GetComponentOptions(type))
			        ?? throw new InvalidDataException($"Component '{type.FullName}' has null data.");
		}

		var writer = new BinaryAssetWriter
		{
			Entities = _entityReferences
		};
		serializedType.WriteBoxed(writer, value);

		return writer.ToArray();
	}

	private JsonSerializerOptions GetComponentOptions(Type type)
	{
		var baseOptions = AssetJson.GetSerializerOptions(type);
		if (_componentOptions.TryGetValue(baseOptions, out var options))
		{
			return options;
		}

		options = CreateCookingOptions(baseOptions);
		options.Converters.Insert(0, _entityReferences);
		_componentOptions[baseOptions] = options;

		return options;
	}

	private JsonSerializerOptions GetDataAssetOptions(Type type)
	{
		var baseOptions = AssetJson.GetSerializerOptions(type);
		if (_dataAssetOptions.TryGetValue(baseOptions, out var options))
		{
			return options;
		}

		options = CreateCookingOptions(baseOptions);
		_dataAssetOptions[baseOptions] = options;

		return options;
	}

	/// <summary>
	/// The editor's options without deserialization callbacks. Those callbacks build runtime state from the data,
	/// such as a mesh renderer loading its mesh, which cooking has no use for and must not trigger. The game runs
	/// them when it reads the cooked value, so it ends up with the same state it would get from the JSON.
	/// </summary>
	private static JsonSerializerOptions CreateCookingOptions(JsonSerializerOptions baseOptions)
	{
		var options = new JsonSerializerOptions(baseOptions);
		var resolver = baseOptions.TypeInfoResolver
		               ?? throw new InvalidOperationException("The editor's JSON options have no type info resolver.");
		options.TypeInfoResolver = resolver.WithAddedModifier(static typeInfo =>
		{
			// Only objects have callbacks; setting them on any other kind throws.
			if (typeInfo.Kind != JsonTypeInfoKind.Object)
			{
				return;
			}

			typeInfo.OnDeserializing = null;
			typeInfo.OnDeserialized = null;
		});

		return options;
	}

	/// <summary>
	/// Turns saved entity references into placeholder entities while JSON is read, and back into their ids when
	/// the value is written to binary. Placeholders never reach a world.
	/// </summary>
	private sealed class CookingEntityReferences : JsonConverter<Entity>, IEntityReferenceEncoder
	{
		// Real entity generations count up from one, so a negative generation can never be mistaken for a live entity.
		private const int PlaceholderGeneration = unchecked((int)0xC00CED1D);

		private readonly List<Guid> _ids = [];
		private readonly Dictionary<Guid, int> _indices = new();

		public override Entity Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
		{
			if (Prefab.TryReadEntityReferenceId(ref reader, out var id) == false)
			{
				return default;
			}

			if (_indices.TryGetValue(id, out var index) == false)
			{
				index = _ids.Count;
				_ids.Add(id);
				_indices[id] = index;
			}

			return new Entity(index, PlaceholderGeneration);
		}

		public override void Write(Utf8JsonWriter writer, Entity value, JsonSerializerOptions options)
		{
			throw new NotSupportedException();
		}

		public Guid Encode(Entity entity)
		{
			if (entity.Generation != PlaceholderGeneration || (uint)entity.Index >= (uint)_ids.Count)
			{
				return Guid.Empty;
			}

			return _ids[entity.Index];
		}
	}
}

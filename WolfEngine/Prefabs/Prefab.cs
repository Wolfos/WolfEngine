using System.Buffers;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;

namespace WolfEngine;

/// <summary>
/// An instantiation template for a prefab asset. All parsing, type resolution and component deserialization
/// happens once when the asset is loaded; <see cref="Instantiate(World)"/> only creates entities and copies
/// component values, so spawning is cheap enough to do every frame.
/// </summary>
/// <remarks>
/// Instances are immutable and may be shared between worlds. A component is spawned in one of three ways,
/// decided per component when the prefab is loaded:
/// <list type="bullet">
/// <item>Values holding nothing mutable and no references into the prefab are copied as is.</item>
/// <item>Values without managed references that point at prefab entities are copied and their entity fields
/// patched to the new instance in place.</item>
/// <item>Anything else, such as a component owning a list or array, is deserialized again for every instance
/// so instances never share mutable state. That path allocates, so keep hot spawned components free of
/// collections where possible.</item>
/// </list>
/// </remarks>
[RuntimeAsset(AssetType.Prefab, typeof(PrefabDocument), typeof(IPrefabRuntimeAssetResolver))]
public sealed class Prefab
{
	/// <summary>The property serialized entity references are written under, by both scenes and prefabs.</summary>
	public const string EntityReferenceIdPropertyName = "__entityRefId";

	// Real entity generations count up from one, so a negative generation can never be mistaken for a live entity.
	private const int PlaceholderGeneration = unchecked((int)0xB5E17A3D);

	private static readonly MethodInfo CreateComponentTemplateMethod = typeof(Prefab).GetMethod(
		nameof(CreateComponentTemplate), BindingFlags.NonPublic | BindingFlags.Static)!;

	// Entities in parent-before-child order; index 0 is the root.
	private readonly EntityTemplate[] _entities;

	private Prefab(Guid assetId, EntityTemplate[] entities)
	{
		AssetId = assetId;
		_entities = entities;
	}

	public Guid AssetId { get; }

	/// <summary>Number of entities every instance creates, the root included.</summary>
	public int EntityCount => _entities.Length;

	/// <summary>Reads a cooked prefab document and builds its template.</summary>
	/// <param name="resolveComponentType">
	/// Maps a serialized component to its runtime type. Returning null leaves that component out of the template.
	/// </param>
	public static Prefab Load(Guid assetId, ReadOnlySpan<byte> utf8Json, Func<PrefabDocumentComponent, Type?> resolveComponentType)
	{
		var document = JsonSerializer.Deserialize<PrefabDocument>(utf8Json, AssetJson.SerializerOptions)
			?? throw new InvalidDataException($"Prefab '{assetId}' is not a valid prefab document.");
		return Create(assetId, document, resolveComponentType);
	}

	/// <summary>Builds a template from an already resolved prefab document.</summary>
	/// <param name="resolveComponentType">
	/// Maps a serialized component to its runtime type. Returning null leaves that component out of the template.
	/// </param>
	public static Prefab Create(Guid assetId, PrefabDocument document, Func<PrefabDocumentComponent, Type?> resolveComponentType)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(resolveComponentType);
		if (document.Version != PrefabDocument.CurrentVersion)
		{
			throw new InvalidDataException(
				$"Prefab '{assetId}' has unsupported version {document.Version}. Expected {PrefabDocument.CurrentVersion}.");
		}

		var ordered = OrderFromRoot(assetId, document);
		var localIndices = new Dictionary<Guid, int>(ordered.Count);
		for (var i = 0; i < ordered.Count; i++)
		{
			localIndices[ordered[i].EntityId] = i;
		}

		var entities = new EntityTemplate[ordered.Count];
		for (var i = 0; i < ordered.Count; i++)
		{
			var source = ordered[i];
			var parentIndex = i == 0 ? -1 : localIndices[source.ParentEntityId!.Value];
			var position = Vector3.Zero;
			var rotation = Quaternion.Identity;
			var scale = Vector3.One;
			if (source.LocalTransform is { } transform)
			{
				Matrix4x4.Decompose(transform, out scale, out rotation, out position);
			}

			entities[i] = new EntityTemplate(
				source.HasName ? source.Name : null,
				source.Enabled,
				parentIndex,
				source.LocalTransform.HasValue,
				position,
				rotation,
				scale,
				CreateComponentTemplates(assetId, source, localIndices, resolveComponentType));
		}

		return new Prefab(assetId, entities);
	}

	/// <summary>Spawns an instance at the pose the prefab root was authored with.</summary>
	/// <returns>The instance's root entity.</returns>
	public Entity Instantiate(World world)
	{
		return InstantiateCore(world, false, default, default, default);
	}

	/// <summary>
	/// Spawns an instance with its root at <paramref name="position"/> and <paramref name="rotation"/>, keeping the
	/// root's authored scale. With a <paramref name="parent"/>, the pose is relative to that parent.
	/// </summary>
	/// <returns>The instance's root entity.</returns>
	public Entity Instantiate(World world, Vector3 position, Quaternion rotation, Entity parent = default)
	{
		return InstantiateCore(world, true, position, rotation, parent);
	}

	private Entity InstantiateCore(World world, bool overrideRootPose, Vector3 position, Quaternion rotation, Entity parent)
	{
		ArgumentNullException.ThrowIfNull(world);
		if (parent.IsValid && world.IsAlive(parent) == false)
		{
			throw new ArgumentException("The parent entity is not alive in this world.", nameof(parent));
		}

		var instance = ArrayPool<Entity>.Shared.Rent(_entities.Length);
		var root = default(Entity);
		try
		{
			// Build the whole hierarchy first, so a component that fails to apply can be unwound by destroying
			// the root and so every entity reference has a target before any component is written.
			for (var i = 0; i < _entities.Length; i++)
			{
				ref readonly var template = ref _entities[i];
				var entity = template.Name is null ? world.CreateEntity() : world.CreateEntity(template.Name);
				instance[i] = entity;
				if (i == 0)
				{
					root = entity;
				}

				if (i == 0 && overrideRootPose)
				{
					world.AddTransform(entity, position, rotation, template.HasTransform ? template.Scale : Vector3.One);
				}
				else if (template.HasTransform)
				{
					world.AddTransform(entity, template.Position, template.Rotation, template.Scale);
				}

				var entityParent = i == 0 ? parent : instance[template.ParentIndex];
				if (entityParent.IsValid)
				{
					world.SetParent(entity, entityParent);
				}

				if (template.Enabled == false)
				{
					world.SetEnabled(entity, false);
				}
			}

			for (var i = 0; i < _entities.Length; i++)
			{
				var components = _entities[i].Components;
				for (var componentIndex = 0; componentIndex < components.Length; componentIndex++)
				{
					components[componentIndex].Add(world, instance[i], instance);
				}
			}

			return root;
		}
		catch
		{
			if (root.IsValid)
			{
				world.DestroyEntity(root);
			}

			throw;
		}
		finally
		{
			ArrayPool<Entity>.Shared.Return(instance);
		}
	}

	/// <summary>
	/// Orders the entities reachable from the root depth first, parents before children and siblings in file
	/// order, which is the order the editor places a prefab in. Entities not under the root are not part of it.
	/// </summary>
	private static List<PrefabDocumentEntity> OrderFromRoot(Guid assetId, PrefabDocument document)
	{
		var entities = document.Entities ?? [];
		var entitiesById = new Dictionary<Guid, PrefabDocumentEntity>(entities.Count);
		var childrenByParent = new Dictionary<Guid, List<PrefabDocumentEntity>>();
		for (var i = 0; i < entities.Count; i++)
		{
			var entity = entities[i];
			if (entitiesById.TryAdd(entity.EntityId, entity) == false)
			{
				throw new InvalidDataException($"Prefab '{assetId}' contains duplicate entity id '{entity.EntityId}'.");
			}

			if (entity.ParentEntityId is not { } parentId)
			{
				continue;
			}

			if (childrenByParent.TryGetValue(parentId, out var children) == false)
			{
				children = [];
				childrenByParent[parentId] = children;
			}

			children.Add(entity);
		}

		if (document.RootEntityId == Guid.Empty || entitiesById.TryGetValue(document.RootEntityId, out var root) == false)
		{
			throw new InvalidDataException($"Prefab '{assetId}' does not contain its root entity '{document.RootEntityId}'.");
		}

		var ordered = new List<PrefabDocumentEntity>(entities.Count);
		var visited = new HashSet<Guid>();
		var pending = new Stack<PrefabDocumentEntity>();
		pending.Push(root);
		while (pending.TryPop(out var entity))
		{
			if (visited.Add(entity.EntityId) == false)
			{
				continue;
			}

			ordered.Add(entity);
			if (childrenByParent.TryGetValue(entity.EntityId, out var children))
			{
				for (var i = children.Count - 1; i >= 0; i--)
				{
					pending.Push(children[i]);
				}
			}
		}

		return ordered;
	}

	private static ComponentTemplate[] CreateComponentTemplates(
		Guid assetId,
		PrefabDocumentEntity entity,
		IReadOnlyDictionary<Guid, int> localIndices,
		Func<PrefabDocumentComponent, Type?> resolveComponentType)
	{
		var components = entity.Components ?? [];
		var templates = new List<ComponentTemplate>(components.Count);
		for (var i = 0; i < components.Count; i++)
		{
			var component = components[i];
			var componentType = resolveComponentType(component);
			if (componentType is null || IsInstantiatedComponentType(componentType) == false)
			{
				continue;
			}

			if (componentType.IsValueType == false || typeof(IEntityComponent).IsAssignableFrom(componentType) == false)
			{
				throw new InvalidDataException(
					$"Prefab '{assetId}' component '{componentType.FullName}' is not an entity component struct.");
			}

			try
			{
				templates.Add((ComponentTemplate)CreateComponentTemplateMethod
					.MakeGenericMethod(componentType)
					.Invoke(null, [component.Data, localIndices])!);
			}
			catch (TargetInvocationException exception) when (exception.InnerException is not null)
			{
				throw new InvalidDataException(
					$"Prefab '{assetId}' entity '{entity.EntityId}' has an unreadable '{componentType.FullName}' component.",
					exception.InnerException);
			}
		}

		return templates.ToArray();
	}

	// Mirrors what scenes persist: names and hierarchy are carried by the entity record, and transient or
	// editor-side state is never part of a spawned instance.
	private static bool IsInstantiatedComponentType(Type componentType)
	{
		return componentType != typeof(NameComponent)
		       && Attribute.IsDefined(componentType, typeof(NotSerializedAttribute)) == false
		       && Attribute.IsDefined(componentType, typeof(ExcludeFromEditorAttribute)) == false
		       && Attribute.IsDefined(componentType, typeof(EditorOnlyAttribute)) == false;
	}

	private static ComponentTemplate CreateComponentTemplate<T>(JsonElement data, IReadOnlyDictionary<Guid, int> localIndices)
		where T : struct, IEntityComponent
	{
		var hasData = data.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null);
		var placeholders = new PlaceholderEntityReferenceConverter(localIndices);
		var value = Activator.CreateInstance<T>();
		if (hasData)
		{
			var options = new JsonSerializerOptions(AssetJson.GetSerializerOptions(typeof(T)));
			options.Converters.Insert(0, placeholders);
			value = data.Deserialize<T>(options);
		}

		if (placeholders.Count == 0 && HasOnlyImmutableState(typeof(T)))
		{
			return new ComponentTemplate<T>(value);
		}

		if (RuntimeHelpers.IsReferenceOrContainsReferences<T>() == false &&
		    TryFindEntityReferencePatches(ref value, localIndices.Count, placeholders.Count, out var patches))
		{
			return new ComponentTemplate<T>(value, patches);
		}

		if (hasData == false)
		{
			return ComponentTemplate<T>.CreateFreshDefault();
		}

		var freshOptions = new JsonSerializerOptions(AssetJson.GetSerializerOptions(typeof(T)));
		freshOptions.Converters.Insert(0, new InstanceEntityReferenceConverter(localIndices));
		return new ComponentTemplate<T>(Encoding.UTF8.GetBytes(data.GetRawText()), freshOptions);
	}

	/// <summary>
	/// Locates every placeholder the deserializer wrote into an unmanaged value. The scan must account for each
	/// placeholder written, or the layout is not one we can patch safely and the caller falls back.
	/// </summary>
	private static bool TryFindEntityReferencePatches<T>(
		ref T value,
		int entityCount,
		int expectedCount,
		out EntityReferencePatch[] patches)
		where T : struct
	{
		var size = Unsafe.SizeOf<T>();
		var entitySize = Unsafe.SizeOf<Entity>();
		var found = new List<EntityReferencePatch>(expectedCount);
		ref var bytes = ref Unsafe.As<T, byte>(ref value);
		for (var offset = 0; offset + entitySize <= size; offset += sizeof(int))
		{
			var candidate = Unsafe.ReadUnaligned<Entity>(ref Unsafe.Add(ref bytes, offset));
			if (candidate.Generation == PlaceholderGeneration && (uint)candidate.Index < (uint)entityCount)
			{
				found.Add(new EntityReferencePatch(offset, candidate.Index));
			}
		}

		patches = found.ToArray();
		return patches.Length == expectedCount;
	}

	/// <summary>True when copying a value of this type can never share mutable state between instances.</summary>
	private static bool HasOnlyImmutableState(Type type)
	{
		if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal))
		{
			return true;
		}

		if (type.IsValueType == false || type.IsPointer)
		{
			return false;
		}

		var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		for (var i = 0; i < fields.Length; i++)
		{
			if (HasOnlyImmutableState(fields[i].FieldType) == false)
			{
				return false;
			}
		}

		return true;
	}

	/// <summary>
	/// Reads a serialized entity reference: an object carrying <see cref="EntityReferenceIdPropertyName"/>.
	/// Anything else, including null, is not a reference to resolve.
	/// </summary>
	private static bool TryReadEntityReferenceId(ref Utf8JsonReader reader, out Guid entityId)
	{
		entityId = Guid.Empty;
		if (reader.TokenType != JsonTokenType.StartObject)
		{
			reader.Skip();
			return false;
		}

		var found = false;
		while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
		{
			var isReferenceId = reader.ValueTextEquals(EntityReferenceIdPropertyName);
			reader.Read();
			if (isReferenceId && reader.TokenType == JsonTokenType.String && reader.TryGetGuid(out var id))
			{
				entityId = id;
				found = true;
			}
			else
			{
				reader.Skip();
			}
		}

		return found;
	}

	private readonly record struct EntityTemplate(
		string? Name,
		bool Enabled,
		int ParentIndex,
		bool HasTransform,
		Vector3 Position,
		Quaternion Rotation,
		Vector3 Scale,
		ComponentTemplate[] Components);

	private readonly record struct EntityReferencePatch(int ByteOffset, int LocalIndex);

	private abstract class ComponentTemplate
	{
		/// <param name="instance">The new instance's entities by local index; may be longer than the prefab.</param>
		public abstract void Add(World world, Entity entity, Entity[] instance);
	}

	private sealed class ComponentTemplate<T> : ComponentTemplate where T : struct, IEntityComponent
	{
		private readonly T _value;
		private readonly EntityReferencePatch[]? _patches;
		private readonly bool _fresh;
		private readonly byte[]? _json;
		private readonly JsonSerializerOptions? _options;

		public ComponentTemplate(T value, EntityReferencePatch[]? patches = null)
		{
			_value = value;
			_patches = patches is { Length: > 0 } ? patches : null;
		}

		public ComponentTemplate(byte[] json, JsonSerializerOptions options)
		{
			_fresh = true;
			_json = json;
			_options = options;
		}

		private ComponentTemplate()
		{
			_fresh = true;
		}

		public static ComponentTemplate<T> CreateFreshDefault() => new();

		public override void Add(World world, Entity entity, Entity[] instance)
		{
			if (_fresh)
			{
				var fresh = _json is null
					? Activator.CreateInstance<T>()
					: InstanceEntityReferenceConverter.Deserialize<T>(_json, _options!, instance);
				world.AddComponent(entity, in fresh);
				return;
			}

			if (_patches is null)
			{
				world.AddComponent(entity, in _value);
				return;
			}

			var copy = _value;
			ref var bytes = ref Unsafe.As<T, byte>(ref copy);
			for (var i = 0; i < _patches.Length; i++)
			{
				var patch = _patches[i];
				Unsafe.WriteUnaligned(ref Unsafe.Add(ref bytes, patch.ByteOffset), instance[patch.LocalIndex]);
			}

			world.AddComponent(entity, in copy);
		}
	}

	/// <summary>
	/// Load-time converter: writes a recognisable placeholder carrying the prefab-local index of the referenced
	/// entity, so the reference can be located in the value and patched per instance. References that leave the
	/// prefab cannot be resolved by an instance and become the invalid entity, as they do for scenes.
	/// </summary>
	private sealed class PlaceholderEntityReferenceConverter(IReadOnlyDictionary<Guid, int> localIndices) : JsonConverter<Entity>
	{
		public int Count { get; private set; }

		public override Entity Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
		{
			if (TryReadEntityReferenceId(ref reader, out var entityId) && localIndices.TryGetValue(entityId, out var localIndex))
			{
				Count++;
				return new Entity(localIndex, PlaceholderGeneration);
			}

			return default;
		}

		public override void Write(Utf8JsonWriter writer, Entity value, JsonSerializerOptions options) =>
			throw new NotSupportedException();
	}

	/// <summary>Spawn-time converter for components that are deserialized again for every instance.</summary>
	private sealed class InstanceEntityReferenceConverter(IReadOnlyDictionary<Guid, int> localIndices) : JsonConverter<Entity>
	{
		[ThreadStatic] private static Entity[]? _currentInstance;

		public static T Deserialize<T>(byte[] json, JsonSerializerOptions options, Entity[] instance)
		{
			var previous = _currentInstance;
			_currentInstance = instance;
			try
			{
				return JsonSerializer.Deserialize<T>(json, options)!;
			}
			finally
			{
				_currentInstance = previous;
			}
		}

		public override Entity Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
		{
			if (TryReadEntityReferenceId(ref reader, out var entityId) &&
			    localIndices.TryGetValue(entityId, out var localIndex) &&
			    _currentInstance is { } instance)
			{
				return instance[localIndex];
			}

			return default;
		}

		public override void Write(Utf8JsonWriter writer, Entity value, JsonSerializerOptions options) =>
			throw new NotSupportedException();
	}
}

public static class PrefabWorldExtensions
{
	/// <summary>Spawns an instance of <paramref name="prefab"/> at the pose its root was authored with.</summary>
	/// <returns>The instance's root entity.</returns>
	public static Entity Instantiate(this World world, AssetRef<Prefab> prefab)
	{
		return Resolve(prefab).Instantiate(world);
	}

	/// <summary>
	/// Spawns an instance of <paramref name="prefab"/> with its root at <paramref name="position"/> and
	/// <paramref name="rotation"/>, relative to <paramref name="parent"/> when one is given.
	/// </summary>
	/// <returns>The instance's root entity.</returns>
	public static Entity Instantiate(
		this World world,
		AssetRef<Prefab> prefab,
		Vector3 position,
		Quaternion rotation,
		Entity parent = default)
	{
		return Resolve(prefab).Instantiate(world, position, rotation, parent);
	}

	private static Prefab Resolve(AssetRef<Prefab> prefab)
	{
		if (prefab.IsValid == false)
		{
			throw new ArgumentException("The prefab reference is empty.", nameof(prefab));
		}

		return prefab.Asset
		       ?? throw new InvalidOperationException($"Prefab asset '{prefab.NodeId}' could not be resolved.");
	}
}

using System.Numerics;
using WolfEngine.Serialization;

namespace WolfEngine.AssetPipeline;

/// <summary>A component cooked to binary: its serializer and the bytes that serializer reads.</summary>
public readonly record struct CookedComponent(SerializedComponentType Type, ReadOnlyMemory<byte> Data);

/// <summary>An entity as cooked scene cells and prefabs store it.</summary>
public sealed class CookedEntity
{
	public Guid EntityId { get; init; }
	public Guid? ParentEntityId { get; init; }
	public string? Name { get; init; }
	public bool Enabled { get; init; } = true;
	public Matrix4x4? LocalTransform { get; init; }
	public IReadOnlyList<CookedComponent> Components { get; init; } = [];
}

/// <summary>
/// The entity list shared by cooked scene cells and prefabs. A type table comes first, naming each component type
/// once with the layout hash it was cooked with, so loading checks every type a single time per file and each
/// component only stores an index into the table.
/// </summary>
internal static class CookedEntityTable
{
	[Flags]
	private enum EntityFlags : byte
	{
		None = 0,
		HasParent = 1 << 0,
		HasName = 1 << 1,
		Enabled = 1 << 2,
		HasTransform = 1 << 3,
	}

	public static void Write(BinaryAssetWriter writer, IReadOnlyList<CookedEntity> entities)
	{
		var typeIndices = new Dictionary<SerializedComponentType, int>();
		var types = new List<SerializedComponentType>();
		for (var i = 0; i < entities.Count; i++)
		{
			var components = entities[i].Components;
			for (var componentIndex = 0; componentIndex < components.Count; componentIndex++)
			{
				var type = components[componentIndex].Type;
				if (typeIndices.TryAdd(type, types.Count))
				{
					types.Add(type);
				}
			}
		}

		writer.WriteCount(types.Count);
		for (var i = 0; i < types.Count; i++)
		{
			writer.WriteString(types[i].Key);
			writer.WriteUInt64(types[i].LayoutHash);
		}

		writer.WriteCount(entities.Count);
		for (var i = 0; i < entities.Count; i++)
		{
			WriteEntity(writer, entities[i], typeIndices);
		}
	}

	/// <summary>
	/// Reads an entity table. Component data is sliced out of <paramref name="source"/> rather than copied, so it
	/// stays valid for as long as the caller keeps the source alive.
	/// </summary>
	public static List<CookedEntity> Read(ref BinaryAssetReader reader, ReadOnlyMemory<byte> source)
	{
		var typeCount = reader.ReadBoundedCount();
		var types = new SerializedComponentType[Math.Max(typeCount, 0)];
		for (var i = 0; i < types.Length; i++)
		{
			var key = reader.ReadString() ?? throw new InvalidDataException("Cooked type table has a null type key.");
			var layoutHash = reader.ReadUInt64();
			var type = SerializedTypeRegistry.GetComponent(key);
			type.EnsureLayout(layoutHash);
			types[i] = type;
		}

		var entityCount = reader.ReadBoundedCount();
		var entities = new List<CookedEntity>(Math.Max(entityCount, 0));
		for (var i = 0; i < entityCount; i++)
		{
			entities.Add(ReadEntity(ref reader, source, types));
		}

		return entities;
	}

	private static void WriteEntity(
		BinaryAssetWriter writer,
		CookedEntity entity,
		IReadOnlyDictionary<SerializedComponentType, int> typeIndices)
	{
		var flags = EntityFlags.None;
		if (entity.ParentEntityId.HasValue)
		{
			flags |= EntityFlags.HasParent;
		}

		if (entity.Name is not null)
		{
			flags |= EntityFlags.HasName;
		}

		if (entity.Enabled)
		{
			flags |= EntityFlags.Enabled;
		}

		if (entity.LocalTransform.HasValue)
		{
			flags |= EntityFlags.HasTransform;
		}

		writer.WriteGuid(entity.EntityId);
		writer.WriteByte((byte)flags);
		if (entity.ParentEntityId is { } parent)
		{
			writer.WriteGuid(parent);
		}

		if (entity.Name is not null)
		{
			writer.WriteString(entity.Name);
		}

		if (entity.LocalTransform is { } transform)
		{
			writer.WriteMatrix4x4(transform);
		}

		writer.WriteCount(entity.Components.Count);
		for (var i = 0; i < entity.Components.Count; i++)
		{
			var component = entity.Components[i];
			writer.WriteVarUInt32((uint)typeIndices[component.Type]);
			writer.WriteBlock(component.Data.Span);
		}
	}

	private static CookedEntity ReadEntity(
		ref BinaryAssetReader reader,
		ReadOnlyMemory<byte> source,
		SerializedComponentType[] types)
	{
		var entityId = reader.ReadGuid();
		var flags = (EntityFlags)reader.ReadByte();
		Guid? parent = (flags & EntityFlags.HasParent) != 0 ? reader.ReadGuid() : null;
		var name = (flags & EntityFlags.HasName) != 0 ? reader.ReadString() : null;
		Matrix4x4? transform = (flags & EntityFlags.HasTransform) != 0 ? reader.ReadMatrix4x4() : null;

		var componentCount = reader.ReadBoundedCount();
		var components = new CookedComponent[Math.Max(componentCount, 0)];
		for (var i = 0; i < components.Length; i++)
		{
			var typeIndex = reader.ReadVarUInt32();
			if (typeIndex >= (uint)types.Length)
			{
				throw new InvalidDataException(
					$"Cooked entity '{entityId}' refers to component type {typeIndex}, but the type table has {types.Length}.");
			}

			var length = reader.ReadBoundedCount();
			var offset = reader.Position;
			reader.ReadBytes(length);
			components[i] = new CookedComponent(types[typeIndex], source.Slice(offset, length));
		}

		return new CookedEntity
		{
			EntityId = entityId,
			ParentEntityId = parent,
			Name = name,
			Enabled = (flags & EntityFlags.Enabled) != 0,
			LocalTransform = transform,
			Components = components
		};
	}
}

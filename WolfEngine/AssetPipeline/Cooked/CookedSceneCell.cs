using WolfEngine.ECS;
using WolfEngine.Serialization;

namespace WolfEngine.AssetPipeline;

/// <summary>A scene cell cooked to binary: the entities of one cell and their components.</summary>
public sealed class CookedSceneCell
{
	public const int CurrentVersion = 1;

	private static readonly uint Magic = CookedFileHeader.Tag('W', 'C', 'E', 'L');

	private CookedSceneCell(IReadOnlyList<CookedEntity> entities)
	{
		Entities = entities;
	}

	public IReadOnlyList<CookedEntity> Entities { get; }

	public static byte[] Write(IReadOnlyList<CookedEntity> entities)
	{
		ArgumentNullException.ThrowIfNull(entities);

		var writer = new BinaryAssetWriter(4096);
		CookedFileHeader.Write(writer, Magic, CurrentVersion);
		CookedEntityTable.Write(writer, entities);

		return writer.ToArray();
	}

	/// <summary>Reads a cooked cell. Component data refers into <paramref name="data"/>, which must not be modified.</summary>
	public static CookedSceneCell Read(byte[] data)
	{
		ArgumentNullException.ThrowIfNull(data);

		var reader = new BinaryAssetReader(data);
		CookedFileHeader.Read(ref reader, Magic, CurrentVersion, "scene cell");
		var entities = CookedEntityTable.Read(ref reader, data);
		if (reader.IsAtEnd == false)
		{
			throw new InvalidDataException($"The cooked scene cell has {reader.Remaining} unexpected bytes after its entities.");
		}

		return new CookedSceneCell(entities);
	}

	/// <summary>
	/// Creates every entity of <paramref name="cells"/> in <paramref name="world"/>, then adds their components and
	/// parents. Entity references resolve across all the cells, so a reference into another cell of the same scene
	/// finds its target; a reference to an entity that is not loaded becomes the invalid entity.
	/// </summary>
	public static void Instantiate(World world, IReadOnlyList<CookedSceneCell> cells)
	{
		ArgumentNullException.ThrowIfNull(world);
		ArgumentNullException.ThrowIfNull(cells);

		var entities = new SceneEntityReferences();
		for (var cellIndex = 0; cellIndex < cells.Count; cellIndex++)
		{
			var cellEntities = cells[cellIndex].Entities;
			for (var i = 0; i < cellEntities.Count; i++)
			{
				var saved = cellEntities[i];
				var entity = saved.Name is null ? world.CreateEntity() : world.CreateEntity(saved.Name);
				if (saved.LocalTransform is { } transform)
				{
					world.AddTransform(entity, transform);
				}

				world.SetEnabled(entity, saved.Enabled);
				entities.Add(saved.EntityId, entity);
			}
		}

		for (var cellIndex = 0; cellIndex < cells.Count; cellIndex++)
		{
			var cellEntities = cells[cellIndex].Entities;
			for (var i = 0; i < cellEntities.Count; i++)
			{
				var saved = cellEntities[i];
				var entity = entities.Decode(saved.EntityId);
				for (var componentIndex = 0; componentIndex < saved.Components.Count; componentIndex++)
				{
					var component = saved.Components[componentIndex];
					var reader = new BinaryAssetReader(component.Data.Span, entities);
					component.Type.ReadAndAdd(ref reader, world, entity);
					EnsureFullyRead(ref reader, component, saved.EntityId);
				}

				if (saved.ParentEntityId is { } parentId)
				{
					var parent = entities.Decode(parentId);
					if (parent.IsValid)
					{
						world.SetParent(entity, parent);
					}
				}
			}
		}
	}

	internal static void EnsureFullyRead(ref BinaryAssetReader reader, CookedComponent component, Guid entityId)
	{
		if (reader.IsAtEnd)
		{
			return;
		}

		throw new InvalidDataException(
			$"Component '{component.Type.Key}' on entity '{entityId}' left {reader.Remaining} bytes unread; " +
			"the cooked data does not match this build's serializer.");
	}

	private sealed class SceneEntityReferences : IEntityReferenceDecoder
	{
		private readonly Dictionary<Guid, Entity> _entities = new();

		public void Add(Guid id, Entity entity)
		{
			if (_entities.TryAdd(id, entity) == false)
			{
				throw new InvalidDataException($"The cooked scene contains entity '{id}' more than once.");
			}
		}

		public Entity Decode(Guid id)
		{
			return _entities.TryGetValue(id, out var entity) ? entity : default;
		}
	}
}

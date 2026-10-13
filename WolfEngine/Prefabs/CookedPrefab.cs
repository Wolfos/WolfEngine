using WolfEngine.AssetPipeline;
using WolfEngine.Serialization;

namespace WolfEngine;

/// <summary>
/// A prefab cooked to binary. Entities are stored in the order <see cref="Prefab"/> instantiates them, root first
/// and every parent before its children, and only components that are part of a spawned instance are kept.
/// </summary>
public static class CookedPrefab
{
	public const int CurrentVersion = 1;

	private static readonly uint Magic = CookedFileHeader.Tag('W', 'P', 'F', 'B');

	/// <summary>Cooks an authored prefab.</summary>
	/// <param name="resolveComponentType">Maps a saved component to its type. Null leaves that component out.</param>
	/// <param name="cookComponent">Writes a saved component's value with the generated serializer and returns the bytes.</param>
	public static byte[] Write(
		Guid assetId,
		PrefabDocument document,
		Func<ComponentDocument, Type?> resolveComponentType,
		Func<ComponentDocument, SerializedComponentType, byte[]> cookComponent)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(resolveComponentType);
		ArgumentNullException.ThrowIfNull(cookComponent);

		if (document.Version != PrefabDocument.CurrentVersion)
		{
			throw new InvalidDataException(
				$"Prefab '{assetId}' has unsupported version {document.Version}. Expected {PrefabDocument.CurrentVersion}.");
		}

		var ordered = Prefab.OrderFromRoot(assetId, document);
		var entities = new List<CookedEntity>(ordered.Count);
		for (var i = 0; i < ordered.Count; i++)
		{
			var source = ordered[i];
			var components = new List<CookedComponent>(source.Components.Count);
			foreach (var component in source.Components)
			{
				var componentType = resolveComponentType(component);
				if (componentType is null || Prefab.IsInstantiatedComponentType(componentType) == false)
				{
					continue;
				}

				var serializedType = SerializedTypeRegistry.GetComponent(componentType);
				components.Add(new CookedComponent(serializedType, cookComponent(component, serializedType)));
			}

			entities.Add(new CookedEntity
			{
				EntityId = source.EntityId,
				// The root's parent is outside the prefab; an instance gets its parent when it is spawned.
				ParentEntityId = i == 0 ? null : source.ParentEntityId,
				Name = source.HasName ? source.Name : null,
				Enabled = source.Enabled,
				LocalTransform = source.LocalTransform,
				Components = components
			});
		}

		var writer = new BinaryAssetWriter(1024);
		CookedFileHeader.Write(writer, Magic, CurrentVersion);
		CookedEntityTable.Write(writer, entities);

		return writer.ToArray();
	}

	internal static List<CookedEntity> Read(Guid assetId, byte[] data)
	{
		ArgumentNullException.ThrowIfNull(data);

		var reader = new BinaryAssetReader(data);
		CookedFileHeader.Read(ref reader, Magic, CurrentVersion, "prefab");
		var entities = CookedEntityTable.Read(ref reader, data);
		if (entities.Count == 0)
		{
			throw new InvalidDataException($"Cooked prefab '{assetId}' has no root entity.");
		}

		if (reader.IsAtEnd == false)
		{
			throw new InvalidDataException($"Cooked prefab '{assetId}' has {reader.Remaining} unexpected bytes after its entities.");
		}

		return entities;
	}
}

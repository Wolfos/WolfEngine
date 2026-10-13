using WolfEngine.ECS;

namespace WolfEngine.Serialization;

/// <summary>Gives an entity reference the stable id it is cooked as.</summary>
public interface IEntityReferenceEncoder
{
	/// <summary>Returns the id for <paramref name="entity"/>, or <see cref="Guid.Empty"/> when it has none.</summary>
	Guid Encode(Entity entity);
}

/// <summary>Resolves a cooked entity reference to an entity in the world being loaded.</summary>
public interface IEntityReferenceDecoder
{
	/// <summary>Returns the entity cooked as <paramref name="id"/>, or the invalid entity when it is not loaded.</summary>
	Entity Decode(Guid id);
}

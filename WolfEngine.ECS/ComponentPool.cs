using System;

namespace WolfEngine.ECS;

internal interface IComponentPool
{
	bool Has(Entity entity);
	bool TryGetComponent(Entity entity, out IEntityComponent component);
	void Remove(Entity entity);
}

public class ComponentPool<T> : IComponentPool where T:struct, IEntityComponent
{
	// SparseSet: maps entity index -> dense slot (or -1 if absent)
	private int[] _sparse = Array.Empty<int>();
	
	private Entity[] _entities = Array.Empty<Entity>();
	private T[] _data = Array.Empty<T>();

	public int Count { get; private set; }

	internal ReadOnlySpan<Entity> EntitiesSpan => _entities.AsSpan(0, Count);

	bool IComponentPool.Has(Entity entity) => Has(entity);

	bool IComponentPool.TryGetComponent(Entity entity, out IEntityComponent component)
	{
		if (!Has(entity))
		{
			component = default;
			return false;
		}

		component = Get(entity);
		return true;
	}

	public void Add(Entity e, in T value)
	{
		EnsureSparseSize(e.Index + 1);
		var slot = _sparse[e.Index] - 1;
		if (slot >= 0)
		{
			if (_entities[slot] == e)
			{
				_data[slot] = value;
				return;
			}

			_entities[slot] = e;
			_data[slot] = value;
			return;
		}

		EnsureCapacity(Count + 1);

		_entities[Count] = e;
		_data[Count] = value;
		_sparse[e.Index] = Count + 1;
		Count++;
	}

	public bool Has(Entity e) => TryGetSlot(e, out _);

	public ref T Get(Entity e)
	{
		if (TryGetSlot(e, out var slot) == false)
		{
			throw new InvalidOperationException($"Component {typeof(T).Name} not found for entity {e.Index}:{e.Generation}.");
		}

		return ref _data[slot];
	}

	public void Remove(Entity e)
	{
		if (TryGetSlot(e, out var slot) == false) return;
		var last = Count - 1;

		_data[slot] = _data[last];
		var movedEntity = _entities[last];
		_entities[slot] = movedEntity;
		_sparse[movedEntity.Index] = slot + 1;

		_sparse[e.Index] = 0;
		Count--;
	}

	private bool TryGetSlot(Entity entity, out int slot)
	{
		slot = -1;
		if (entity.Index >= _sparse.Length)
		{
			return false;
		}

		slot = _sparse[entity.Index] - 1;
		if (slot < 0 || slot >= Count)
		{
			slot = -1;
			return false;
		}

		if (_entities[slot] != entity)
		{
			slot = -1;
			return false;
		}

		return true;
	}

	private void EnsureSparseSize(int size)
	{
		if (_sparse.Length >= size) return;

		var newSize = _sparse.Length > 0 ? _sparse.Length : 4;
		while (newSize < size) newSize *= 2;
		Array.Resize(ref _sparse, newSize);
	}

	private void EnsureCapacity(int size)
	{
		if (_entities.Length >= size) return;

		var newSize = _entities.Length > 0 ? _entities.Length : 4;
		while (newSize < size) newSize *= 2;

		Array.Resize(ref _entities, newSize);
		Array.Resize(ref _data, newSize);
	}
}

namespace WolfEngine.ECS;

internal interface IComponentPool
{
	bool Has(Entity entity);
	bool TryGetComponent(Entity entity, out IEntityComponent component);
}

public class ComponentPool<T> : IComponentPool where T:struct, IEntityComponent
{
	// SparseSet: maps entity index -> dense slot (or -1 if absent)
	private int[] _sparse = Array.Empty<int>();
	
	private int[] _entities = Array.Empty<int>();
	private T[] _data = Array.Empty<T>();

	public int Count { get; private set; }

	internal ReadOnlySpan<int> EntitiesSpan => _entities.AsSpan(0, Count);

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
		if (_sparse[e.Index] != 0) return; 
		EnsureCapacity(Count + 1);

		_entities[Count] = e.Index;
		_data[Count] = value;
		_sparse[e.Index] = Count + 1;
		Count++;
	}

	public bool Has(Entity e) => e.Index < _sparse.Length && _sparse[e.Index] != 0;

	public ref T Get(Entity e)
	{
		var slot = _sparse[e.Index] - 1;
		return ref _data[slot];
	}

	public void Remove(Entity e)
	{
		var slot = _sparse[e.Index] - 1;
		if (slot < 0) return;
		var last = Count - 1;

		_data[slot] = _data[last];
		var movedEntity = _entities[last];
		_entities[slot] = movedEntity;
		_sparse[movedEntity] = slot + 1;

		_sparse[e.Index] = 0;
		Count--;
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

namespace WolfEngine.ECS;

internal interface IComponentPool { }
public class ComponentPool<T> : IComponentPool where T:struct
{
	// SparseSet: maps entity index -> dense slot (or -1 if absent)
	private int[] _sparse = Array.Empty<int>();
	
	private int[] _entities = Array.Empty<int>();
	private T[] _data = Array.Empty<T>();
	private int _count;

	public void Add(Entity e, in T value)
	{
		EnsureSparseSize(e.Index + 1);
		if (_sparse[e.Index] != 0) return; 
		EnsureCapacity(_count + 1);

		_entities[_count] = e.Index;
		_data[_count] = value;
		_sparse[e.Index] = _count + 1;
		_count++;
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
		var last = _count - 1;

		_data[slot] = _data[last];
		var movedEntity = _entities[last];
		_entities[slot] = movedEntity;
		_sparse[movedEntity] = slot + 1;

		_sparse[e.Index] = 0;
		_count--;
	}

	private void EnsureSparseSize(int size)
	{
		// TODO: Implement
	}

	private void EnsureCapacity(int size)
	{
		// TODO: Implement
	}
}
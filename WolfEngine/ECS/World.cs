namespace WolfEngine.ECS;

public class World
{
	private readonly FreeList _entities = new();
	private readonly Dictionary<Type, IComponentPool> _pools = new();

	public Entity CreateEntity() => _entities.Create();
	public void DestroyEntity(Entity e) => _entities.Destroy(e);

	public void AddComponent<T>(Entity e, in T value = default) where T : struct, IEntityComponent
		=> Pool<T>().Add(e, value);

	public ref T GetComponent<T>(Entity e) where T : struct, IEntityComponent
		=> ref Pool<T>().Get(e);

	public bool HasComponent<T>(Entity e) where T : struct, IEntityComponent
		=> Pool<T>().Has(e);

	public void RemoveComponent<T>(Entity e) where T : struct, IEntityComponent
		=> Pool<T>().Remove(e);

	public View<T1,T2> View<T1,T2>()
		where T1:struct, IEntityComponent where T2:struct, IEntityComponent
		=> new(Pool<T1>(), Pool<T2>());

	private ComponentPool<T> Pool<T>() where T:struct, IEntityComponent
		=> (ComponentPool<T>) (_pools.TryGetValue(typeof(T), out var p)
			? p : _pools[typeof(T)] = new ComponentPool<T>());
}
namespace WolfEngine.ECS;

public class World
{
	private readonly FreeList _entities = new();
	private readonly Dictionary<Type, IComponentPool> _pools = new();

	public Entity Create() => _entities.Create();
	public void Destroy(Entity e) => _entities.Destroy(e);

	public void Add<T>(Entity e, in T value = default) where T : struct
		=> Pool<T>().Add(e, value);

	public ref T Get<T>(Entity e) where T : struct
		=> ref Pool<T>().Get(e);

	public bool Has<T>(Entity e) where T : struct
		=> Pool<T>().Has(e);

	public void Remove<T>(Entity e) where T : struct
		=> Pool<T>().Remove(e);

	public View<T1,T2> View<T1,T2>()
		where T1:struct where T2:struct
		=> new(Pool<T1>(), Pool<T2>());

	private ComponentPool<T> Pool<T>() where T:struct
		=> (ComponentPool<T>) (_pools.TryGetValue(typeof(T), out var p)
			? p : (_pools[typeof(T)] = new ComponentPool<T>()));
}
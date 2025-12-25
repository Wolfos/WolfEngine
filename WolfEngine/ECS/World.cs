using WolfEngine.Editor;

namespace WolfEngine.ECS;

public class World
{
    private readonly FreeList _entities = new();
    private readonly Dictionary<Type, IComponentPool> _pools = new();

    public Entity CreateEntity() => _entities.Create();

    public Entity CreateEntity(string name)
    {
        var entity = _entities.Create();
        AddComponent(entity, new NameComponent{Name = name});
        return entity;
    }
    
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

    public void GetAllEntities(List<Entity> entities)
    {
        entities.Clear();
        _entities.GetAllEntities(entities);
    }

    public void GetAllComponents(Entity entity, List<IEntityComponent> components)
    {
        components.Clear();
        if (!_entities.IsAlive(entity)) return;

        foreach (var pool in _pools.Values)
        {
            if (pool.TryGetComponent(entity, out var component))
                components.Add(component);
        }
    }

    public void GetComponentTypes(Entity entity, List<Type> componentTypes)
    {
        componentTypes.Clear();
        if (!_entities.IsAlive(entity)) return;

        foreach (var kvp in _pools)
        {
            if (kvp.Value.Has(entity))
                componentTypes.Add(kvp.Key);
        }
    }

    private ComponentPool<T> Pool<T>() where T:struct, IEntityComponent
        => (ComponentPool<T>) (_pools.TryGetValue(typeof(T), out var p)
            ? p : _pools[typeof(T)] = new ComponentPool<T>());
}
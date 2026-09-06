using System.Numerics;

namespace WolfEngine.ECS;

public class World
{
    private readonly FreeList _entities = new();
    private readonly Dictionary<Type, IComponentPool> _pools = new();
    
    public WorldTag Tag { get; }
    
    public World(WorldTag tag)
    {
        Tag = tag;
    }

    public Entity CreateEntity() => _entities.Create();

    public Entity CreateEntity(string name)
    {
        var entity = _entities.Create();
        AddComponent(entity, new NameComponent{Name = name});
        return entity;
    }

    public Entity CreateEntity(string name, Vector3 localPosition, Quaternion localRotation, Vector3 localScale)
    {
        var entity = CreateEntity(name);
        AddTransform(entity, new LocalTransform(localPosition, localRotation, localScale));
        return entity;
    }
    
    public Entity CreateEntity(string name, Matrix4x4 fromTransform)
    {
        var entity = CreateEntity(name);
        AddTransform(entity, new LocalTransform(fromTransform));
        return entity;
    }

    
    public void DestroyEntity(Entity e)
    {
        if (!IsAlive(e))
        {
            return;
        }

        DestroyEntityRecursive(e);
    }

    public void AddComponent<T>(Entity e, in T value = default) where T : struct, IEntityComponent
        => Pool<T>().Add(e, value);

    public ref T GetComponent<T>(Entity e) where T : struct, IEntityComponent
        => ref Pool<T>().Get(e);

    public bool HasComponent<T>(Entity e) where T : struct, IEntityComponent
        => Pool<T>().Has(e);

    public bool HasComponent(Entity e, Type componentType)
    {
        ValidateComponentType(componentType);
        return _pools.TryGetValue(componentType, out var pool) && pool.Has(e);
    }

    public void RemoveComponent<T>(Entity e) where T : struct, IEntityComponent
        => Pool<T>().Remove(e);

    public void RemoveComponent(Entity e, Type componentType)
    {
        ValidateComponentType(componentType);
        if (_pools.TryGetValue(componentType, out var pool) == false)
        {
            return;
        }

        pool.Remove(e);
    }

    public void RemoveComponentPool(Type componentType)
    {
        ValidateComponentType(componentType);
        _pools.Remove(componentType);
    }

    public View<T1> View<T1>()
        where T1:struct, IEntityComponent
        => new(Pool<T1>());

    public View<T1,T2> View<T1,T2>()
        where T1:struct, IEntityComponent where T2:struct, IEntityComponent
        => new(Pool<T1>(), Pool<T2>());
    
    public View<T1,T2,T3> View<T1,T2,T3>()
        where T1:struct, IEntityComponent where T2:struct, IEntityComponent where T3:struct, IEntityComponent
        => new(Pool<T1>(), Pool<T2>(), Pool<T3>());

    internal int GetComponentCount<T>() where T : struct, IEntityComponent
        => Pool<T>().Count;

    public void GetAllEntities(List<Entity> entities)
    {
        entities.Clear();
        _entities.GetAllEntities(entities);
    }

    public bool IsAlive(Entity entity)
    {
        return _entities.IsAlive(entity);
    }

    public bool IsEnabled(Entity e)
    {
        return _entities.IsEnabled(e);
    }

    public void SetEnabled(Entity e, bool enabled)
    {
		var wasEnabled = IsEnabled(e);
        _entities.SetEnabled(e, enabled);
		if (wasEnabled != IsEnabled(e))
		{
			MarkWorldTransformChanged(e);
		}
    }

	public void MarkWorldTransformChanged(Entity entity)
	{
		if (IsAlive(entity) == false || HasComponent<WorldTransform>(entity) == false)
		{
			return;
		}

		var pool = Pool<DirtyWorldTransform>();
		if (pool.Has(entity))
		{
			pool.Get(entity).Consumed = 0;
		}
		else
		{
			pool.Add(entity, default);
		}
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

    private void DestroyEntityRecursive(Entity entity)
    {
        if (!IsAlive(entity))
        {
            return;
        }

        if (HasComponent<Children>(entity))
        {
            var child = GetComponent<Children>(entity).First;
            while (child.IsValid)
            {
                var next = HasComponent<Sibling>(child)
                    ? GetComponent<Sibling>(child).Next
                    : default;
                DestroyEntityRecursive(child);
                child = next;
            }
        }

        if (HasComponent<Parent>(entity))
        {
            RemoveParent(entity);
        }

        foreach (var pool in _pools.Values)
        {
            pool.Remove(entity);
        }

        _entities.Destroy(entity);
    }

    private ComponentPool<T> Pool<T>() where T:struct, IEntityComponent
        => (ComponentPool<T>) (_pools.TryGetValue(typeof(T), out var p)
            ? p : _pools[typeof(T)] = new ComponentPool<T>());

    private static void ValidateComponentType(Type? componentType)
    {
        if (componentType is null ||
            componentType.IsValueType == false ||
            typeof(IEntityComponent).IsAssignableFrom(componentType) == false)
        {
            throw new InvalidOperationException($"'{componentType?.FullName ?? "<null>"}' is not a valid entity component type.");
        }
    }

    public void AddTransform(Entity entity, in LocalTransform transform)
    {
        AddComponent(entity, transform);
        AddComponent(entity, new WorldTransform());
        MarkDirty(entity);
    }

    public void AddTransform(Entity entity, Matrix4x4 transform)
    {
        AddTransform(entity, new LocalTransform(transform));
    }

    public void SetParent(Entity child, Entity parent)
    {
        if (!parent.IsValid)
        {
            throw new ArgumentException("Parent entity must be valid.", nameof(parent));
        }

        if (child == parent)
        {
            throw new InvalidOperationException("An entity cannot be parented to itself.");
        }

        if (HasComponent<Parent>(child))
        {
            var currentParent = GetComponent<Parent>(child).Value;
            if (currentParent == parent)
            {
                return;
            }

            RemoveParent(child);
        }

        if (HasComponent<Sibling>(child))
        {
            RemoveComponent<Sibling>(child);
        }

        AddComponent(child, new Parent { Value = parent });

        if (HasComponent<Children>(parent))
        {
            ref var children = ref GetComponent<Children>(parent);
            if (!children.First.IsValid)
            {
                children.First = child;
            }
            else
            {
                var current = children.First;
                while (HasComponent<Sibling>(current))
                {
                    ref var currentSibling = ref GetComponent<Sibling>(current);
                    if (!currentSibling.Next.IsValid)
                    {
                        currentSibling.Next = child;
                        if (HasComponent<LocalTransform>(child))
                        {
                            MarkDirty(child);
                        }

                        return;
                    }

                    current = currentSibling.Next;
                }

                AddComponent(current, new Sibling { Next = child });
            }
        }
        else
        {
            AddComponent(parent, new Children { First = child });
        }

        if (HasComponent<LocalTransform>(child))
        {
            MarkDirty(child);
        }
    }

    public void RemoveParent(Entity child)
    {
        if (!HasComponent<Parent>(child))
        {
            if (HasComponent<Sibling>(child))
            {
                RemoveComponent<Sibling>(child);
            }

            return;
        }

        var parent = GetComponent<Parent>(child).Value;
        if (HasComponent<Children>(parent))
        {
            ref var children = ref GetComponent<Children>(parent);
            if (children.First == child)
            {
                var childNext = HasComponent<Sibling>(child) ? GetComponent<Sibling>(child).Next : default;
                if (childNext.IsValid)
                {
                    children.First = childNext;
                }
                else
                {
                    RemoveComponent<Children>(parent);
                }
            }
            else
            {
                var current = children.First;
                while (current.IsValid && HasComponent<Sibling>(current))
                {
                    ref var currentSibling = ref GetComponent<Sibling>(current);
                    if (currentSibling.Next == child)
                    {
                        var childNext = HasComponent<Sibling>(child) ? GetComponent<Sibling>(child).Next : default;
                        if (childNext.IsValid)
                        {
                            currentSibling.Next = childNext;
                        }
                        else
                        {
                            RemoveComponent<Sibling>(current);
                        }

                        break;
                    }

                    current = currentSibling.Next;
                }
            }
        }

        RemoveComponent<Parent>(child);

        if (HasComponent<Sibling>(child))
        {
            RemoveComponent<Sibling>(child);
        }

        if (HasComponent<LocalTransform>(child))
        {
            MarkDirty(child);
        }
    }

    public void MarkDirty(Entity entity)
    {
        GetComponent<LocalTransform>(entity).IsDirty = true;
        AddComponent<DirtyTransformRoot>(entity);
    }

    public void Translate(Entity e, Vector3 translation, bool isLocal = false)
    {
        ref var localTransform = ref GetComponent<LocalTransform>(e);

        if (isLocal)
        {
            localTransform.LocalPosition += translation;
        }
        else
        {
            ref var worldTransform = ref GetComponent<WorldTransform>(e);
            localTransform.LocalPosition += Vector3.TransformNormal(translation, worldTransform.WorldToLocal);
        }

        MarkDirty(e);
    }
    
    public void SetLocalPosition(Entity e, Vector3 position)
    {
        ref var localTransform = ref GetComponent<LocalTransform>(e);
        localTransform.LocalPosition = position;
        MarkDirty(e);
    }

    public void SetLocalRotation(Entity e, Quaternion rotation)
    {
        ref var localTransform = ref GetComponent<LocalTransform>(e);
        localTransform.LocalRotation = rotation;
        MarkDirty(e);
    }

    public void SetLocalScale(Entity e, Vector3 scale)
    {
        ref var localTransform = ref GetComponent<LocalTransform>(e);
        localTransform.LocalScale = scale;
        MarkDirty(e);
    }

    public void SetWorldPosition(Entity e, Vector3 position)
    {
        SetWorldTransform(e, position: position);
    }
    
    public void SetWorldRotation(Entity e, Quaternion rotation)
    {
        SetWorldTransform(e, rotation: rotation);
    }

    public void SetWorldScale(Entity e, Vector3 scale)
    {
        SetWorldTransform(e, scale: scale);
    }

    public void SetWorldTransform(Entity e, Vector3? position = null, Quaternion? rotation = null, Vector3? scale = null)
    {
        ref var localTransform = ref GetComponent<LocalTransform>(e);
        if (TryGetCurrentWorldMatrix(e, out var currentWorldMatrix) == false ||
            Matrix4x4.Decompose(currentWorldMatrix, out var currentScale, out var currentRotation, out var currentPosition) == false ||
            TryGetCurrentParentWorldToLocal(e, out var parentWorldToLocal) == false)
        {
            return;
        }

        var targetPosition = position ?? currentPosition;
        var targetRotation = NormalizeOrIdentity(rotation ?? currentRotation);
        var targetScale = scale ?? currentScale;
        var worldMatrix = ComposeTrs(targetScale, targetRotation, targetPosition);
        var localMatrix = worldMatrix * parentWorldToLocal;
        if (Matrix4x4.Decompose(localMatrix, out var localScale, out var localRotation, out var localPosition) == false)
        {
            return;
        }

        localTransform.LocalPosition = localPosition;
        localTransform.LocalRotation = NormalizeOrIdentity(localRotation);
        localTransform.LocalScale = localScale;
        MarkDirty(e);
    }

    private bool TryGetCurrentParentWorldToLocal(Entity entity, out Matrix4x4 parentWorldToLocal)
    {
        parentWorldToLocal = Matrix4x4.Identity;
        if (HasComponent<Parent>(entity) == false)
        {
            return true;
        }

        var parent = GetComponent<Parent>(entity).Value;
        if (parent.IsValid == false || HasComponent<WorldTransform>(parent) == false)
        {
            return true;
        }

        return TryGetCurrentWorldMatrix(parent, out var parentWorld) && Matrix4x4.Invert(parentWorld, out parentWorldToLocal);
    }

    private bool TryGetCurrentWorldMatrix(Entity entity, out Matrix4x4 worldMatrix)
    {
        worldMatrix = Matrix4x4.Identity;
        if (HasComponent<LocalTransform>(entity) == false || HasComponent<WorldTransform>(entity) == false)
        {
            return false;
        }

        var parentWorld = Matrix4x4.Identity;
        if (HasComponent<Parent>(entity))
        {
            var parent = GetComponent<Parent>(entity).Value;
            if (parent.IsValid && HasComponent<WorldTransform>(parent))
            {
                if (TryGetCurrentWorldMatrix(parent, out var resolvedParentWorld) == false)
                {
                    return false;
                }

                parentWorld = resolvedParentWorld;
            }
        }

        worldMatrix = GetComponent<LocalTransform>(entity).GetTransform() * parentWorld;
        return true;
    }

    internal bool TryGetWorldPoseAndScale(Entity entity, out Vector3 position, out Quaternion rotation, out Vector3 scale)
    {
        position = Vector3.Zero;
        rotation = Quaternion.Identity;
        scale = Vector3.One;

        if (HasComponent<LocalTransform>(entity) == false || HasComponent<WorldTransform>(entity) == false)
        {
            return false;
        }

        EnsureWorldTransformUpToDate(entity);
        var worldTransform = GetComponent<WorldTransform>(entity).LocalToWorld;
        if (Matrix4x4.Decompose(worldTransform, out scale, out rotation, out position) == false)
        {
            rotation = Quaternion.Identity;
            scale = Vector3.One;
            return false;
        }

        rotation = rotation.LengthSquared() > 0.0f ? Quaternion.Normalize(rotation) : Quaternion.Identity;
        scale = new Vector3(MathF.Abs(scale.X), MathF.Abs(scale.Y), MathF.Abs(scale.Z));
        return true;
    }

    internal Matrix4x4 GetParentWorldToLocalMatrix(Entity entity)
    {
        if (HasComponent<Parent>(entity) == false)
        {
            return Matrix4x4.Identity;
        }

        var parent = GetComponent<Parent>(entity).Value;
        if (parent.IsValid == false || HasComponent<WorldTransform>(parent) == false)
        {
            return Matrix4x4.Identity;
        }

        EnsureWorldTransformUpToDate(parent);
        return GetComponent<WorldTransform>(parent).WorldToLocal;
    }

    internal Matrix4x4 GetParentLocalToWorldMatrix(Entity entity)
    {
        if (HasComponent<Parent>(entity) == false)
        {
            return Matrix4x4.Identity;
        }

        var parent = GetComponent<Parent>(entity).Value;
        if (parent.IsValid == false || HasComponent<WorldTransform>(parent) == false)
        {
            return Matrix4x4.Identity;
        }

        EnsureWorldTransformUpToDate(parent);
        return GetComponent<WorldTransform>(parent).LocalToWorld;
    }

    internal void ApplyPhysicsWorldPoses(IReadOnlyList<PhysicsWorldPoseSyncItem> poses)
    {
        if (poses.Count == 0)
        {
            return;
        }

        var posesByEntity = new Dictionary<Entity, PhysicsWorldPoseSyncItem>(poses.Count);
        for (var index = 0; index < poses.Count; index++)
        {
            var pose = poses[index];
            posesByEntity[pose.Entity] = pose;
        }

        var applied = new HashSet<Entity>();
        for (var index = 0; index < poses.Count; index++)
        {
            ApplyPhysicsWorldPoseRecursive(poses[index].Entity, posesByEntity, applied);
        }
    }

    internal void ApplyPhysicsWorldPose(Entity entity, Vector3 worldPosition, Quaternion worldRotation)
    {
        if (HasComponent<LocalTransform>(entity) == false || HasComponent<WorldTransform>(entity) == false)
        {
            return;
        }

        ref var localTransform = ref GetComponent<LocalTransform>(entity);
        worldRotation = worldRotation.LengthSquared() > 0.0f ? Quaternion.Normalize(worldRotation) : Quaternion.Identity;

        var preservedLocalScale = localTransform.LocalScale;
        var worldMatrix = ComposeTrs(preservedLocalScale, worldRotation, worldPosition);
        var localMatrix = worldMatrix * GetParentWorldToLocalMatrix(entity);
        if (Matrix4x4.Decompose(localMatrix, out _, out var localRotation, out var localPosition) == false)
        {
            return;
        }

        localTransform.LocalPosition = localPosition;
        localTransform.LocalRotation = localRotation.LengthSquared() > 0.0f ? Quaternion.Normalize(localRotation) : Quaternion.Identity;
        localTransform.LocalScale = preservedLocalScale;
        localTransform.IsDirty = false;

        var finalLocalMatrix = ComposeTrs(localTransform.LocalScale, localTransform.LocalRotation, localTransform.LocalPosition);
        var parentLocalToWorld = GetParentLocalToWorldMatrix(entity);

        ref var worldTransform = ref GetComponent<WorldTransform>(entity);
        worldTransform.LocalToWorld = finalLocalMatrix * parentLocalToWorld;
        Matrix4x4.Invert(worldTransform.LocalToWorld, out worldTransform.WorldToLocal);
		MarkWorldTransformChanged(entity);

        if (HasComponent<DirtyTransformRoot>(entity))
        {
            RemoveComponent<DirtyTransformRoot>(entity);
        }

        MarkChildTransformsDirty(entity);
    }

    private void ApplyPhysicsWorldPoseRecursive(
        Entity entity,
        IReadOnlyDictionary<Entity, PhysicsWorldPoseSyncItem> posesByEntity,
        ISet<Entity> applied)
    {
        if (applied.Contains(entity))
        {
            return;
        }

        if (posesByEntity.TryGetValue(entity, out var pose) == false)
        {
            return;
        }

        if (HasComponent<Parent>(entity))
        {
            var parent = GetComponent<Parent>(entity).Value;
            if (parent.IsValid && posesByEntity.ContainsKey(parent))
            {
                ApplyPhysicsWorldPoseRecursive(parent, posesByEntity, applied);
            }
        }

        ApplyPhysicsWorldPose(entity, pose.WorldPosition, pose.WorldRotation);
        applied.Add(entity);
    }

    private void EnsureWorldTransformUpToDate(Entity entity)
    {
        if (HasComponent<LocalTransform>(entity) == false || HasComponent<WorldTransform>(entity) == false)
        {
            return;
        }

        ref var localTransform = ref GetComponent<LocalTransform>(entity);
        if (localTransform.IsDirty == false)
        {
            return;
        }

        ref var worldTransform = ref GetComponent<WorldTransform>(entity);
        var parentWorld = Matrix4x4.Identity;
        if (HasComponent<Parent>(entity))
        {
            var parent = GetComponent<Parent>(entity).Value;
            if (parent.IsValid && HasComponent<WorldTransform>(parent))
            {
                EnsureWorldTransformUpToDate(parent);
                parentWorld = GetComponent<WorldTransform>(parent).LocalToWorld;
            }
        }

        var worldMatrix = localTransform.GetTransform() * parentWorld;
        worldTransform.LocalToWorld = worldMatrix;
        Matrix4x4.Invert(worldMatrix, out worldTransform.WorldToLocal);
        localTransform.IsDirty = false;
		MarkWorldTransformChanged(entity);
    }

    private void MarkChildTransformsDirty(Entity entity)
    {
        if (HasComponent<Children>(entity) == false)
        {
            return;
        }

        var child = GetComponent<Children>(entity).First;
        while (child.IsValid)
        {
            if (HasComponent<LocalTransform>(child))
            {
                MarkDirty(child);
            }

            child = HasComponent<Sibling>(child)
                ? GetComponent<Sibling>(child).Next
                : default;
        }
    }

    private static Matrix4x4 ComposeTrs(Vector3 scale, Quaternion rotation, Vector3 position)
    {
        return
            Matrix4x4.CreateScale(scale) *
            Matrix4x4.CreateFromQuaternion(rotation) *
            Matrix4x4.CreateTranslation(position);
    }

    private static Quaternion NormalizeOrIdentity(Quaternion rotation)
    {
        return rotation.LengthSquared() > 0.0f ? Quaternion.Normalize(rotation) : Quaternion.Identity;
    }
}

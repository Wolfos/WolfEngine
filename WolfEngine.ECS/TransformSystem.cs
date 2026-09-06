using System.Numerics;

namespace WolfEngine.ECS;

public class TransformSystem : IPreRender
{
    private readonly List<Entity> _dirtyRoots = new();

    public void PreRender(float deltaTime, World world)
    {
        // Select disjoint subtrees before clearing any tags. A dirty ancestor
        // covers this entity even when it was marked later or resolved by physics.
        // Snapshot the roots so subtree updates do not mutate the enumerated view.
        _dirtyRoots.Clear();
        foreach (var entry in world.View<LocalTransform, WorldTransform, DirtyTransformRoot>())
        {
            if (!HasDirtyAncestor(entry.Entity, world))
                _dirtyRoots.Add(entry.Entity);
        }

        foreach (var root in _dirtyRoots)
        {
            var parentWorld = Matrix4x4.Identity;
            if (world.HasComponent<Parent>(root))
            {
                var parent = world.GetComponent<Parent>(root).Value;
                if (parent.IsValid && world.HasComponent<WorldTransform>(parent))
                    parentWorld = world.GetComponent<WorldTransform>(parent).LocalToWorld;
            }

            UpdateSubtreeIterative(root, parentWorld, world);
        }
        _dirtyRoots.Clear();
    }

    public WorldTag GetTag() => WorldTag.All;

    private static bool HasDirtyAncestor(Entity entity, World world)
    {
        while (world.HasComponent<Parent>(entity))
        {
            entity = world.GetComponent<Parent>(entity).Value;
            if (world.HasComponent<DirtyTransformRoot>(entity))
                return true;
        }

        return false;
    }

    private void UpdateSubtreeIterative(Entity root, in Matrix4x4 parentWorld, World world)
    {
        // explicit stack to avoid recursion limits
        Span<(Entity entity, Matrix4x4 parentWorld)> stack = stackalloc (Entity, Matrix4x4)[256];
        (Entity entity, Matrix4x4 parentWorld)[]? heapStack = null;
        int stackSize = 0;

        static void Push(
            ref (Entity entity, Matrix4x4 parentWorld)[]? heap,
            Span<(Entity entity, Matrix4x4 parentWorld)> localStack,
            ref int size,
            Entity e,
            in Matrix4x4 parent)
        {
            if (heap == null && size < localStack.Length)
            {
                localStack[size++] = (e, parent);
                return;
            }

            if (heap == null)
            {
                heap = new (Entity entity, Matrix4x4 parentWorld)[Math.Max(256, size * 2)];
                localStack.Slice(0, size).CopyTo(heap);
            }
            else if (size >= heap.Length)
            {
                Array.Resize(ref heap, Math.Max(heap.Length * 2, size + 1));
            }

            heap[size++] = (e, parent);
        }

        Push(ref heapStack, stack, ref stackSize, root, in parentWorld);

        while (stackSize > 0)
        {
            (Entity entity, Matrix4x4 parentW) frame;
            if (heapStack != null)
            {
                frame = heapStack[--stackSize];
            }
            else
            {
                frame = stack[--stackSize];
            }

            var e = frame.entity;
            var parentMatrix = frame.parentW;

            ref var local = ref world.GetComponent<LocalTransform>(e);
            ref var worldTransform = ref world.GetComponent<WorldTransform>(e);

            // compute local matrix
            var localM = ComposeTRS(local);
            var worldM = localM * parentMatrix;

            worldTransform.LocalToWorld = worldM;
            Matrix4x4.Invert(worldM, out worldTransform.WorldToLocal);
            local.IsDirty = false;
            world.RemoveComponent<DirtyTransformRoot>(e);

			world.MarkWorldTransformChanged(e);

            // push children with this world matrix as their parent
            if (world.HasComponent<Children>(e))
            {
                var childEntity = world.GetComponent<Children>(e).First;
                while (childEntity.IsValid)
                {
                    Push(ref heapStack, stack, ref stackSize, childEntity, in worldM);

                    if (world.HasComponent<Sibling>(childEntity))
                        childEntity = world.GetComponent<Sibling>(childEntity).Next;
                    else
                        break;
                }
            }
        }
    }

    private static Matrix4x4 ComposeTRS(in LocalTransform local)
    {
        return
            Matrix4x4.CreateScale(local.LocalScale) *
            Matrix4x4.CreateFromQuaternion(local.LocalRotation) *
            Matrix4x4.CreateTranslation(local.LocalPosition);
    }
}

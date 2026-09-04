using System.Numerics;

namespace WolfEngine.ECS;

public class TransformSystem : IPreRender
{
    public void PreRender(float deltaTime, World world)
    {
        // View of dirty subtree roots: they have Local+World+DirtyTransformRoot
        var roots = world.View<LocalTransform, WorldTransform, DirtyTransformRoot>();

        foreach (var entry in roots)
        {
            var rootEntity = entry.Entity;

            // parent of a root is identity
            UpdateSubtreeIterative(rootEntity, Matrix4x4.Identity, world);
            
            // clear the tag so it's not processed next frame
            world.RemoveComponent<DirtyTransformRoot>(rootEntity);
        }
    }

    public WorldTag GetTag() => WorldTag.All;

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

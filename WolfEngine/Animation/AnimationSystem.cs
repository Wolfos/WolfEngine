using System.Numerics;
using WolfEngine.ECS;

namespace WolfEngine.Animation;

/// <summary>
/// Advances every animator and turns its pose into skinning matrices.
/// </summary>
/// <remarks>
/// Runs as <see cref="IUpdate"/> rather than <see cref="IPreRender"/> so that it lands before
/// <see cref="TransformSystem"/>: exposed bone sockets write into entity local transforms, and
/// those need to propagate in the same frame they were produced, not the next one.
/// </remarks>
public sealed class AnimationSystem : IUpdate
{
	private readonly WorldTag _tag;
    public AnimationSystem() : this(WorldTag.All) { }
    public AnimationSystem(WorldTag tag) => _tag = tag;
    public WorldTag GetTag() => _tag;

	public void Update(float deltaTime, World world)
	{
		ArgumentNullException.ThrowIfNull(world);
        using var profile = Profiling.FrameProfiler.Instance.Measure("Animation evaluation");

		foreach (var entry in world.View<Animator>())
		{
			if (world.IsEnabled(entry.Entity) == false)
			{
				continue;
			}

			ref var animator = ref entry.First;
			if (animator.TryPrepare() == false)
			{
				continue;
			}


            var instance = animator.GraphInstance!;
            if (animator.Bindings is null)
            {
                try { animator.Bindings = AnimationOutputBindings.Resolve(world, entry.Entity, instance); }
                catch (InvalidOperationException exception) { animator.Diagnostic = exception.Message; continue; }
            }
            var changed = instance.Evaluate(deltaTime);
            if (changed || !animator.HasPreviousPose)
            {
                animator.SkinningMatrices!.AsSpan().CopyTo(animator.PreviousSkinningMatrices);
                instance.Output.ComputeSkinningMatrices(animator.Skeleton!, animator.SkinningMatrices!);
                var bonesChanged = !animator.HasPreviousPose || !animator.SkinningMatrices.AsSpan().SequenceEqual(animator.PreviousSkinningMatrices);
                if (!animator.HasPreviousPose)
                {
                    animator.SkinningMatrices!.AsSpan().CopyTo(animator.PreviousSkinningMatrices);
                    animator.HasPreviousPose = true;
                }
                if (bonesChanged) animator.PoseGeneration++;
            }
            animator.Bindings.Apply(instance.Output);

		}

		ApplyExposedBones(world);
	}

	/// <summary>
	/// Copies model-space bone transforms onto the entities that opted into being sockets.
	/// </summary>
	private static void ApplyExposedBones(World world)
	{
		foreach (var entry in world.View<ExposedBone>())
		{
			if (world.IsEnabled(entry.Entity) == false)
			{
				continue;
			}

			ref var exposedBone = ref entry.First;
			var animatorEntity = exposedBone.AnimatorEntity;
			if (animatorEntity.IsValid == false || world.HasComponent<Animator>(animatorEntity) == false)
			{
				continue;
			}

			ref var animator = ref world.GetComponent<Animator>(animatorEntity);
			var skeleton = animator.Skeleton;
			var pose = animator.Pose;
			if (skeleton is null || pose is null)
			{
				continue;
			}

			if (exposedBone.BoneIndex < 0 || exposedBone.BoneIndex >= skeleton.BoneCount || skeleton.BoneNames[exposedBone.BoneIndex] != exposedBone.BoneName)
			{
				if (skeleton.TryGetBoneIndex(exposedBone.BoneName, out var resolved) == false)
				{
					continue;
				}

				exposedBone.BoneIndex = resolved;
			}

			var modelSpace = pose.GetModelSpaceMatrix(exposedBone.BoneIndex);
			if (Matrix4x4.Decompose(modelSpace, out var scale, out var rotation, out var translation) == false)
			{
				continue;
			}

			// The socket is parented to the animator entity, so the bone's model-space transform is
			// already the correct local transform relative to it.
			world.SetLocalPosition(entry.Entity, translation);
			world.SetLocalRotation(entry.Entity, rotation);
			world.SetLocalScale(entry.Entity, scale);
		}
	}
}

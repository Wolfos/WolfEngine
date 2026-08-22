using System;
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
	public WorldTag GetTag() => WorldTag.All;

	public void Update(float deltaTime, World world)
	{
		ArgumentNullException.ThrowIfNull(world);

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

			var poseSource = animator.PoseSource;
			var pose = animator.Pose;
			var skeleton = animator.Skeleton;
			if (poseSource is null || pose is null || skeleton is null ||
			    animator.SkinningMatrices is null || animator.PreviousSkinningMatrices is null)
			{
				continue;
			}

			if (poseSource is SingleClipPoseSource clipSource)
			{
				// The component fields are the authoring surface, including the editor's scrubber,
				// so they drive the source rather than the other way round.
				clipSource.Speed = animator.Speed;
				clipSource.Playing = animator.Playing;
				clipSource.Time = animator.Time;
			}

			// Keep the last evaluated pose for motion vectors without copying matrices.
			(animator.SkinningMatrices, animator.PreviousSkinningMatrices) =
				(animator.PreviousSkinningMatrices, animator.SkinningMatrices);
			var skinningMatrices = animator.SkinningMatrices;

			poseSource.Evaluate(deltaTime, pose);
			pose.ComputeSkinningMatrices(skeleton, skinningMatrices);

			if (animator.HasPreviousPose == false)
			{
				// Seed the first previous pose to avoid identity-matrix motion.
				skinningMatrices.AsSpan().CopyTo(animator.PreviousSkinningMatrices);
				animator.HasPreviousPose = true;
			}

			if (poseSource is SingleClipPoseSource advanced)
			{
				animator.Time = advanced.Time;
			}

			animator.PoseGeneration++;
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

			if (exposedBone.BoneIndex < 0)
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

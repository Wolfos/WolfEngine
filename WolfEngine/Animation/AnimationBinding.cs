namespace WolfEngine.Animation;

/// <summary>
/// What a track drives. Bone tracks resolve against a <see cref="Skeleton"/>; property tracks
/// resolve against the entity hierarchy, which is how animation that does not drive bones
/// (an opening door, a pulsing light) travels through the same clips and the same blending.
/// </summary>
public enum AnimationBindingKind
{
	Bone = 0,
	Property = 1
}

/// <summary>
/// Addresses the target of a track by name rather than by index. Name addressing is what makes
/// a clip portable between skeletons, and therefore what retargeting will be built on; never
/// replace it with a baked index.
/// </summary>
/// <param name="Kind">Whether this binding targets a skeleton bone or an arbitrary property.</param>
/// <param name="Path">Bone name for <see cref="AnimationBindingKind.Bone"/>, otherwise an entity-relative node path such as "Turret/Barrel".</param>
/// <param name="Property">Field chain for <see cref="AnimationBindingKind.Property"/>, such as "Light.Intensity". Empty for bone bindings.</param>
public readonly record struct AnimationBinding(AnimationBindingKind Kind, string Path, string Property)
{
	public static AnimationBinding ForBone(string boneName) =>
		new(AnimationBindingKind.Bone, boneName ?? string.Empty, string.Empty);

	public static AnimationBinding ForProperty(string nodePath, string property) =>
		new(AnimationBindingKind.Property, nodePath ?? string.Empty, property ?? string.Empty);
}

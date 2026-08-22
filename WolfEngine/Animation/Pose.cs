using System.Numerics;

namespace WolfEngine.Animation;

/// <summary>
/// A single bone's local transform. Poses are kept as decomposed TRS rather than matrices
/// because blending and retargeting both need to operate on rotation separately; a baked matrix
/// pose cannot be blended correctly and cannot be retargeted at all.
/// </summary>
public struct BoneTransform
{
	public Vector3 Position;
	public Quaternion Rotation;
	public Vector3 Scale;

	public BoneTransform(Vector3 position, Quaternion rotation, Vector3 scale)
	{
		Position = position;
		Rotation = rotation;
		Scale = scale;
	}

	public static BoneTransform Identity => new(Vector3.Zero, Quaternion.Identity, Vector3.One);

	/// <summary>Composes to a row-vector matrix, matching <c>TransformSystem.ComposeTRS</c>.</summary>
	public readonly Matrix4x4 ToMatrix() =>
		Matrix4x4.CreateScale(Scale) *
		Matrix4x4.CreateFromQuaternion(Rotation) *
		Matrix4x4.CreateTranslation(Position);

	public static BoneTransform FromMatrix(in Matrix4x4 matrix)
	{
		if (Matrix4x4.Decompose(matrix, out var scale, out var rotation, out var translation) == false)
		{
			return Identity;
		}

		return new BoneTransform(translation, rotation, scale);
	}

	public static BoneTransform Lerp(in BoneTransform a, in BoneTransform b, float weight) =>
		new(
			Vector3.Lerp(a.Position, b.Position, weight),
			QuaternionMath.Nlerp(a.Rotation, b.Rotation, weight),
			Vector3.Lerp(a.Scale, b.Scale, weight));
}

/// <summary>
/// The output of any pose source: local-space bone transforms, local transforms for animated
/// non-bone targets, and the values of any scalar property tracks. All three live in one container
/// deliberately, so that when the animator graph arrives it blends everything a clip can drive
/// through a single code path instead of growing a parallel system per track kind.
/// </summary>
public sealed class Pose
{
	private Matrix4x4[] _modelSpaceScratch = Array.Empty<Matrix4x4>();

	public Pose(int boneCount, int transformCount = 0, int valueCount = 0)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(boneCount);
		ArgumentOutOfRangeException.ThrowIfNegative(transformCount);
		ArgumentOutOfRangeException.ThrowIfNegative(valueCount);
		Bones = new BoneTransform[boneCount];
		Transforms = new BoneTransform[transformCount];
		Values = new float[valueCount];
	}

	/// <summary>Local transforms indexed by skeleton bone index.</summary>
	public BoneTransform[] Bones { get; }

	/// <summary>Local transforms for animated targets that are not skeleton bones, indexed by non-bone track slot.</summary>
	public BoneTransform[] Transforms { get; }

	/// <summary>Scalar property values, indexed by property track.</summary>
	public float[] Values { get; }

	public int BoneCount => Bones.Length;

	public void SetToBindPose(Skeleton skeleton)
	{
		ArgumentNullException.ThrowIfNull(skeleton);
		var count = Math.Min(Bones.Length, skeleton.BoneCount);
		for (var i = 0; i < count; i++)
		{
			Bones[i] = skeleton.BindPoseLocal[i];
		}

		for (var i = 0; i < Transforms.Length; i++)
		{
			Transforms[i] = BoneTransform.Identity;
		}

		Array.Clear(Values);
	}

	public void CopyFrom(Pose other)
	{
		ArgumentNullException.ThrowIfNull(other);
		Array.Copy(other.Bones, Bones, Math.Min(other.Bones.Length, Bones.Length));
		Array.Copy(other.Transforms, Transforms, Math.Min(other.Transforms.Length, Transforms.Length));
		Array.Copy(other.Values, Values, Math.Min(other.Values.Length, Values.Length));
	}

	/// <summary>
	/// Per-element interpolation from <paramref name="a"/> to <paramref name="b"/>. Nothing in the
	/// POC calls this; it exists so the blend contract the animator graph will build on is fixed
	/// alongside the pose format rather than retrofitted onto it.
	/// </summary>
	public static void Blend(Pose a, Pose b, float weight, Pose destination)
	{
		ArgumentNullException.ThrowIfNull(a);
		ArgumentNullException.ThrowIfNull(b);
		ArgumentNullException.ThrowIfNull(destination);

		var boneCount = Math.Min(destination.Bones.Length, Math.Min(a.Bones.Length, b.Bones.Length));
		for (var i = 0; i < boneCount; i++)
		{
			destination.Bones[i] = BoneTransform.Lerp(a.Bones[i], b.Bones[i], weight);
		}

		var transformCount = Math.Min(destination.Transforms.Length, Math.Min(a.Transforms.Length, b.Transforms.Length));
		for (var i = 0; i < transformCount; i++)
		{
			destination.Transforms[i] = BoneTransform.Lerp(a.Transforms[i], b.Transforms[i], weight);
		}

		var valueCount = Math.Min(destination.Values.Length, Math.Min(a.Values.Length, b.Values.Length));
		for (var i = 0; i < valueCount; i++)
		{
			destination.Values[i] = float.Lerp(a.Values[i], b.Values[i], weight);
		}
	}

	/// <summary>
	/// Converts local bone transforms into the matrices the skinning shader consumes.
	/// One forward pass suffices because <see cref="Skeleton"/> guarantees parents precede children.
	/// </summary>
	public void ComputeSkinningMatrices(Skeleton skeleton, Span<Matrix4x4> destination)
	{
		ArgumentNullException.ThrowIfNull(skeleton);
		var boneCount = skeleton.BoneCount;
		if (destination.Length < boneCount)
		{
			throw new ArgumentException(
				$"Destination must hold at least {boneCount} matrices, but holds {destination.Length}.",
				nameof(destination));
		}

		if (Bones.Length < boneCount)
		{
			throw new InvalidOperationException(
				$"Pose holds {Bones.Length} bones but skeleton '{skeleton.Name}' has {boneCount}.");
		}

		if (_modelSpaceScratch.Length < boneCount)
		{
			_modelSpaceScratch = new Matrix4x4[boneCount];
		}

		var parents = skeleton.ParentIndices;
		var inverseBind = skeleton.InverseBindMatrices;
		for (var i = 0; i < boneCount; i++)
		{
			var local = Bones[i].ToMatrix();
			var parentIndex = parents[i];
			_modelSpaceScratch[i] = parentIndex >= 0
				? local * _modelSpaceScratch[parentIndex]
				: local;

			destination[i] = inverseBind[i] * _modelSpaceScratch[i];
		}
	}

	/// <summary>Model-space matrix of a single bone. Valid only after <see cref="ComputeSkinningMatrices"/>.</summary>
	public Matrix4x4 GetModelSpaceMatrix(int boneIndex)
	{
		if (boneIndex < 0 || boneIndex >= _modelSpaceScratch.Length)
		{
			return Matrix4x4.Identity;
		}

		return _modelSpaceScratch[boneIndex];
	}
}

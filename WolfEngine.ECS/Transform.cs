using System.Numerics;
using System.Text.Json.Serialization;

namespace WolfEngine.ECS;

[ExcludeFromAddComponent]
[NotSerialized]
public struct LocalTransform : IEntityComponent
{
	public Vector3 LocalPosition { get; internal set; }
	public Quaternion LocalRotation { get; internal set; }
	public Vector3 LocalScale { get; internal set; }

	[JsonIgnore] public bool IsDirty { get; internal set; }

	internal LocalTransform(Vector3 localPosition, Quaternion localRotation, Vector3 localScale)
	{
		LocalPosition = localPosition;
		LocalRotation = localRotation;
		LocalScale = localScale;
	}

	internal LocalTransform(Matrix4x4 fromTransform)
	{
		Matrix4x4.Decompose(fromTransform, out var scale, out var rotation, out var position);
		LocalPosition = position;
		LocalRotation = rotation;
		LocalScale = scale;
	}

	public Matrix4x4 GetTransform()
	{
		var scale = Matrix4x4.CreateScale(LocalScale);
		var rotation = Matrix4x4.CreateFromQuaternion(LocalRotation);
		var translation = Matrix4x4.CreateTranslation(LocalPosition);

		return scale * rotation * translation;
	}

	public Vector3 EulerAngles
	{
		get
		{
			float sinr_cosp = 2 * (LocalRotation.W * LocalRotation.Z + LocalRotation.X * LocalRotation.Y);
			float cosr_cosp = 1 - 2 * (LocalRotation.Y * LocalRotation.Y + LocalRotation.Z * LocalRotation.Z);
			float roll = MathF.Atan2(sinr_cosp, cosr_cosp);

			float sinp = 2 * (LocalRotation.W * LocalRotation.X - LocalRotation.Z * LocalRotation.Y);
			float pitch;
			if (MathF.Abs(sinp) >= 1)
				pitch = MathF.CopySign(MathF.PI / 2, sinp); // clamp
			else
				pitch = MathF.Asin(sinp);

			float siny_cosp = 2 * (LocalRotation.W * LocalRotation.Y + LocalRotation.X * LocalRotation.Z);
			float cosy_cosp = 1 - 2 * (LocalRotation.X * LocalRotation.X + LocalRotation.Y * LocalRotation.Y);
			float yaw = MathF.Atan2(siny_cosp, cosy_cosp);

			return new Vector3(pitch, yaw, roll);
		}
	}
}

[ExcludeFromEditor]
[NotSerialized]
public struct WorldTransform : IEntityComponent
{
	public Matrix4x4 LocalToWorld;
	public Matrix4x4 WorldToLocal;
}

[ExcludeFromEditor]
public struct Parent : IEntityComponent
{
	public Entity Value;
}

[ExcludeFromEditor]
public struct Children : IEntityComponent
{
	public Entity First;
}

[ExcludeFromEditor]
public struct Sibling : IEntityComponent
{
	public Entity Next;
}

[ExcludeFromEditor]
[NotSerialized]
public struct DirtyTransformRoot : IEntityComponent
{
}

internal readonly record struct PhysicsWorldPoseSyncItem(Entity Entity, Vector3 WorldPosition, Quaternion WorldRotation);

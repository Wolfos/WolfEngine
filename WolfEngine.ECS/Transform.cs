using System.Numerics;

namespace WolfEngine.ECS;

[ExcludeFromAddComponent]
public struct LocalTransform: IEntityComponent
{
	public Vector3 LocalPosition { get; internal set; }
	public Quaternion LocalRotation { get; internal set; }
	public Vector3 LocalScale { get; internal set; }

	public bool IsDirty { get; internal set; }

	internal LocalTransform(Vector3 localPosition, Quaternion localRotation, Vector3 localScale)
	{
		LocalPosition = localPosition;
		LocalRotation = localRotation;
		LocalScale = localScale;
	}

	internal LocalTransform(Matrix4x4 fromTransform)
	{
		Matrix4x4.Decompose(fromTransform, out var scale, out var rotation,  out var position);
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
}

[ExcludeFromEditor]
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
public struct DirtyTransformRoot : IEntityComponent
{
}
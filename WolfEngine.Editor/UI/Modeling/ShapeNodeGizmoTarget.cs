using System.Numerics;
using WolfEngine.Modeling;

namespace WolfEngine.Editor.UI;

/// <summary>
/// Lets the transform gizmo move one shape node. The preview draws shape space as world space, so the gizmo's world
/// transforms only need converting into the node's parent group. A whole drag is one undo step.
/// <para>
/// Scaling is per axis on the gizmo, but a node only has a uniform scale. A primitive turns an axis drag into the
/// dimension along that axis, such as a box's half height or a cylinder's radius; a group scales uniformly.
/// </para>
/// </summary>
internal sealed class ShapeNodeGizmoTarget : ITransformGizmoTarget
{
	private readonly ShapeEditorSession _session;
	private readonly Guid _nodeId;
	private IDisposable? _edit;
	private float _startScale;
	private Vector4 _startDimensions;

	public ShapeNodeGizmoTarget(ShapeEditorSession session, Guid nodeId)
	{
		_session = session;
		_nodeId = nodeId;
	}

	public int Count => 1;

	public TransformGizmoTargetState GetState(int index)
	{
		var tree = _session.Document!.Tree;
		tree.TryGetShapeMatrix(_nodeId, out var matrix);
		Matrix4x4.Decompose(matrix, out _, out var rotation, out _);

		// Scale is reported as one per axis and read back as a factor of the drag's start; see SetLocalScale.
		return new TransformGizmoTargetState(
			matrix.Translation,
			TransformGizmo.NormalizeOrIdentity(rotation),
			Vector3.One);
	}

	public void BeginEdit()
	{
		_edit ??= _session.BeginEdit("Transform Shape Node");
		if (_session.Document?.Tree.Find(_nodeId) is { } node)
		{
			_startScale = node.Scale;
			_startDimensions = node.Dimensions;
		}
	}

	public void SetWorldPosition(int index, Vector3 worldPosition)
	{
		var parentMatrix = GetParentMatrix();
		if (Matrix4x4.Invert(parentMatrix, out var shapeToParent) == false)
		{
			return;
		}

		var position = Vector3.Transform(worldPosition, shapeToParent);
		_session.EditNode(_nodeId, "Move Shape Node", node => node.Position = position);
	}

	public void SetWorldRotation(int index, Quaternion worldRotation)
	{
		Matrix4x4.Decompose(GetParentMatrix(), out _, out var parentRotation, out _);
		parentRotation = TransformGizmo.NormalizeOrIdentity(parentRotation);
		var rotation = TransformGizmo.NormalizeOrIdentity(Quaternion.Inverse(parentRotation) * worldRotation);
		_session.EditNode(_nodeId, "Rotate Shape Node", node => node.Rotation = rotation);
	}

	public void SetLocalScale(int index, Vector3 localScale)
	{
		_session.EditNode(_nodeId, "Scale Shape Node", node =>
		{
			if (node.IsGroup)
			{
				var factor = GetDominantFactor(localScale);
				node.Scale = MathF.Max(ShapeNode.MinScale, _startScale * factor);
				return;
			}

			node.Dimensions = ScaleDimensions(node.Primitive, _startDimensions, localScale);
		});
	}

	public void OnTransformsChanged()
	{
	}

	public void EndEdit(bool commit)
	{
		_edit?.Dispose();
		_edit = null;
	}

	/// <summary>Maps per-axis scale factors onto the dimensions they stretch, for each primitive.</summary>
	internal static Vector4 ScaleDimensions(ShapePrimitiveKind primitive, Vector4 start, Vector3 factors)
	{
		var radial = MathF.Abs(factors.X - 1.0f) >= MathF.Abs(factors.Z - 1.0f) ? factors.X : factors.Z;
		var result = start;
		switch (primitive)
		{
			case ShapePrimitiveKind.Sphere:
				result.X = start.X * GetDominantFactor(factors);
				break;

			case ShapePrimitiveKind.Box:
			case ShapePrimitiveKind.RoundedBox:
				result.X = start.X * factors.X;
				result.Y = start.Y * factors.Y;
				result.Z = start.Z * factors.Z;
				break;

			case ShapePrimitiveKind.Capsule:
			case ShapePrimitiveKind.Cylinder:
			case ShapePrimitiveKind.Cone:
				result.X = start.X * radial;
				result.Y = start.Y * factors.Y;
				break;

			case ShapePrimitiveKind.Torus:
				result.X = start.X * radial;
				result.Y = start.Y * factors.Y;
				break;
		}

		return ShapePrimitives.Sanitize(primitive, result);
	}

	/// <summary>The factor that moved furthest from one: the axis being dragged.</summary>
	private static float GetDominantFactor(Vector3 factors)
	{
		var result = factors.X;
		if (MathF.Abs(factors.Y - 1.0f) > MathF.Abs(result - 1.0f))
		{
			result = factors.Y;
		}

		if (MathF.Abs(factors.Z - 1.0f) > MathF.Abs(result - 1.0f))
		{
			result = factors.Z;
		}

		return result;
	}

	private Matrix4x4 GetParentMatrix()
	{
		var tree = _session.Document!.Tree;
		if (tree.FindParent(_nodeId) is { } parent && tree.TryGetShapeMatrix(parent.Id, out var matrix))
		{
			return matrix;
		}

		return Matrix4x4.Identity;
	}
}

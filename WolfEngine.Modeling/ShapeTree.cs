using System.Numerics;

namespace WolfEngine.Modeling;

/// <summary>Whether a shape node holds children or is a signed-distance primitive.</summary>
public enum ShapeNodeKind : byte
{
	Group = 0,
	Primitive = 1
}

/// <summary>
/// How a node combines with what its earlier siblings built. A group starts empty, so the first child of a group
/// must be a union to contribute anything: subtracting from or intersecting with nothing leaves nothing.
/// </summary>
public enum ShapeOperation : byte
{
	Union = 0,
	Subtract = 1,
	Intersect = 2
}

/// <summary>The signed-distance primitives a shape is built from. Each is centred on its local origin with +Y up.</summary>
public enum ShapePrimitiveKind : byte
{
	Sphere = 0,
	Box = 1,
	RoundedBox = 2,
	Capsule = 3,
	Cylinder = 4,
	Torus = 5,
	Cone = 6
}

/// <summary>
/// One node of a shape's operation tree. Transforms are relative to the parent group and use a uniform scale,
/// because a non-uniform scale would stop the field from being a true distance; primitives change proportion
/// through <see cref="Dimensions"/> instead.
/// <para>
/// Nodes are mutable so the editor can edit them in place. Anything that needs a stable copy, such as an undo step
/// or a compile running on another thread, takes a <see cref="Clone"/>.
/// </para>
/// </summary>
public sealed class ShapeNode
{
	public const float MinScale = 0.0001f;

	public Guid Id { get; set; } = Guid.NewGuid();
	public string Name { get; set; } = string.Empty;
	public ShapeNodeKind Kind { get; set; }
	public ShapeOperation Operation { get; set; }

	/// <summary>Blend radius of <see cref="Operation"/> in parent-group units. Zero is a sharp combination.</summary>
	public float Smoothness { get; set; }

	public Vector3 Position { get; set; }
	public Quaternion Rotation { get; set; } = Quaternion.Identity;
	public float Scale { get; set; } = 1.0f;

	/// <summary>Only meaningful for primitives.</summary>
	public ShapePrimitiveKind Primitive { get; set; }

	/// <summary>Primitive dimensions in local units. <see cref="ShapePrimitives"/> describes what each component means.</summary>
	public Vector4 Dimensions { get; set; }

	public List<ShapeNode> Children { get; } = new();

	public bool IsGroup => Kind == ShapeNodeKind.Group;

	public static ShapeNode CreateGroup(string name)
	{
		return new ShapeNode
		{
			Name = name,
			Kind = ShapeNodeKind.Group
		};
	}

	public static ShapeNode CreatePrimitive(ShapePrimitiveKind primitive, string? name = null)
	{
		return new ShapeNode
		{
			Name = name ?? ShapePrimitives.GetDisplayName(primitive),
			Kind = ShapeNodeKind.Primitive,
			Primitive = primitive,
			Dimensions = ShapePrimitives.GetDefaultDimensions(primitive)
		};
	}

	/// <summary>The node's transform relative to its parent group, in System.Numerics row-vector order.</summary>
	public Matrix4x4 GetLocalMatrix()
	{
		var scale = MathF.Max(Scale, MinScale);
		return Matrix4x4.CreateScale(scale) *
		       Matrix4x4.CreateFromQuaternion(Rotation) *
		       Matrix4x4.CreateTranslation(Position);
	}

	/// <summary>A deep copy that keeps every node's id, so a copy can stand in for the original in undo history.</summary>
	public ShapeNode Clone()
	{
		var copy = new ShapeNode
		{
			Id = Id,
			Name = Name,
			Kind = Kind,
			Operation = Operation,
			Smoothness = Smoothness,
			Position = Position,
			Rotation = Rotation,
			Scale = Scale,
			Primitive = Primitive,
			Dimensions = Dimensions
		};

		foreach (var child in Children)
		{
			copy.Children.Add(child.Clone());
		}

		return copy;
	}

	/// <summary>A deep copy where every node gets a fresh id, for duplicating part of a tree.</summary>
	public ShapeNode Duplicate()
	{
		var copy = Clone();
		foreach (var node in Enumerate(copy))
		{
			node.Id = Guid.NewGuid();
		}

		return copy;
	}

	/// <summary>True when <paramref name="other"/> is this node or sits anywhere below it.</summary>
	public bool Contains(ShapeNode other)
	{
		foreach (var node in Enumerate(this))
		{
			if (ReferenceEquals(node, other))
			{
				return true;
			}
		}

		return false;
	}

	private static IEnumerable<ShapeNode> Enumerate(ShapeNode root)
	{
		var stack = new Stack<ShapeNode>();
		stack.Push(root);
		while (stack.Count > 0)
		{
			var node = stack.Pop();
			yield return node;

			foreach (var child in node.Children)
			{
				stack.Push(child);
			}
		}
	}
}

/// <summary>The operation tree of one shape. The root is always a group.</summary>
public sealed class ShapeTree
{
	public ShapeTree()
		: this(ShapeNode.CreateGroup("Shape"))
	{
	}

	public ShapeTree(ShapeNode root)
	{
		ArgumentNullException.ThrowIfNull(root);
		if (root.IsGroup == false)
		{
			throw new ArgumentException("The root of a shape tree must be a group.", nameof(root));
		}

		Root = root;
	}

	public ShapeNode Root { get; }

	public ShapeTree Clone()
	{
		return new ShapeTree(Root.Clone());
	}

	public ShapeNode? Find(Guid id)
	{
		return Find(Root, id);
	}

	/// <summary>The group that holds a node, or null for the root and for ids not in the tree.</summary>
	public ShapeNode? FindParent(Guid id)
	{
		return FindParent(Root, id);
	}

	/// <summary>A node's transform relative to the shape, composed through every enclosing group.</summary>
	public bool TryGetShapeMatrix(Guid id, out Matrix4x4 shapeMatrix)
	{
		return TryGetShapeMatrix(Root, id, Matrix4x4.Identity, out shapeMatrix);
	}

	/// <summary>Every node in depth-first order, the root first.</summary>
	public IEnumerable<ShapeNode> EnumerateDepthFirst()
	{
		var stack = new Stack<ShapeNode>();
		stack.Push(Root);
		while (stack.Count > 0)
		{
			var node = stack.Pop();
			yield return node;

			for (var i = node.Children.Count - 1; i >= 0; i--)
			{
				stack.Push(node.Children[i]);
			}
		}
	}

	private static ShapeNode? Find(ShapeNode node, Guid id)
	{
		if (node.Id == id)
		{
			return node;
		}

		foreach (var child in node.Children)
		{
			var found = Find(child, id);
			if (found is not null)
			{
				return found;
			}
		}

		return null;
	}

	private static ShapeNode? FindParent(ShapeNode node, Guid id)
	{
		foreach (var child in node.Children)
		{
			if (child.Id == id)
			{
				return node;
			}

			var found = FindParent(child, id);
			if (found is not null)
			{
				return found;
			}
		}

		return null;
	}

	private static bool TryGetShapeMatrix(ShapeNode node, Guid id, Matrix4x4 parentMatrix, out Matrix4x4 shapeMatrix)
	{
		// The root's own transform positions the whole shape, like any other group.
		var matrix = node.GetLocalMatrix() * parentMatrix;
		if (node.Id == id)
		{
			shapeMatrix = matrix;
			return true;
		}

		foreach (var child in node.Children)
		{
			if (TryGetShapeMatrix(child, id, matrix, out shapeMatrix))
			{
				return true;
			}
		}

		shapeMatrix = default;
		return false;
	}
}

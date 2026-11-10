using System.Numerics;
using System.Runtime.InteropServices;

namespace WolfEngine.Modeling;

public enum ShapeSdfOpcode : uint
{
	/// <summary>Pushes an empty set onto the stack: a group starting.</summary>
	PushEmpty = 0,

	/// <summary>Evaluates a primitive and combines it into the top of the stack.</summary>
	Primitive = 1,

	/// <summary>Pops a finished group and combines it into the new top of the stack.</summary>
	PopCombine = 2
}

/// <summary>
/// One step of a flattened shape tree. The layout is shared with <c>ShapeSdfInstruction</c> in
/// <c>shape_sdf.compute.slang</c>; change both together. It is 96 bytes, a multiple of 16, so it packs the same way
/// in a structured buffer on every backend.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct ShapeSdfInstruction
{
	public uint Opcode;
	public uint Primitive;
	public uint Operation;
	public float Smoothness;

	// Shape space to primitive local space, as the three rows of an affine matrix applied to (p, 1).
	public Vector4 Row0;
	public Vector4 Row1;
	public Vector4 Row2;

	public Vector4 Dimensions;

	/// <summary>Converts a local distance back to shape units; the product of every enclosing uniform scale.</summary>
	public float Scale;
	public float Padding0;
	public float Padding1;
	public float Padding2;
}

/// <summary>
/// A shape tree flattened into a stack program that a CPU or GPU sampler runs per point. A program is immutable,
/// so a compile can run it on another thread while the editor keeps changing the tree it came from.
/// </summary>
public sealed class ShapeSdfProgram
{
	/// <summary>
	/// Deepest group nesting a program may use. The GPU sampler keeps the stack in registers, so this is fixed;
	/// <see cref="Compile"/> rejects deeper trees rather than letting the samplers disagree.
	/// </summary>
	public const int MaxStackDepth = 16;

	/// <summary>Stands for "no surface here". Finite, so the smooth operators never produce NaN.</summary>
	public const float EmptyDistance = 1.0e20f;

	private readonly ShapeSdfInstruction[] _instructions;
	private readonly Guid[] _primitiveIds;

	private ShapeSdfProgram(ShapeSdfInstruction[] instructions, Guid[] primitiveIds, ShapeBounds bounds)
	{
		_instructions = instructions;
		_primitiveIds = primitiveIds;
		Bounds = bounds;
	}

	public ReadOnlySpan<ShapeSdfInstruction> Instructions => _instructions;

	/// <summary>The node id of each instruction that is a primitive, or <see cref="Guid.Empty"/>.</summary>
	public ReadOnlySpan<Guid> InstructionNodeIds => _primitiveIds;

	/// <summary>A box that contains every point the shape could be inside, or empty when it has no positive volume.</summary>
	public ShapeBounds Bounds { get; }

	public static ShapeSdfProgram Compile(ShapeTree tree)
	{
		ArgumentNullException.ThrowIfNull(tree);

		var instructions = new List<ShapeSdfInstruction>();
		var ids = new List<Guid>();
		var bounds = ShapeBounds.Empty;
		Emit(tree.Root, Matrix4x4.Identity, 1.0f, 1, contributes: true, 0.0f, instructions, ids, ref bounds);

		return new ShapeSdfProgram(instructions.ToArray(), ids.ToArray(), bounds);
	}

	/// <summary>The shape's signed distance at a point in shape space.</summary>
	public float Evaluate(Vector3 point)
	{
		Span<float> stack = stackalloc float[MaxStackDepth];
		var top = -1;
		for (var i = 0; i < _instructions.Length; i++)
		{
			ref readonly var instruction = ref _instructions[i];
			switch ((ShapeSdfOpcode)instruction.Opcode)
			{
				case ShapeSdfOpcode.PushEmpty:
					top++;
					stack[top] = EmptyDistance;
					break;

				case ShapeSdfOpcode.Primitive:
				{
					var distance = EvaluatePrimitive(instruction, point);
					stack[top] = Combine((ShapeOperation)instruction.Operation, stack[top], distance, instruction.Smoothness);
					break;
				}

				case ShapeSdfOpcode.PopCombine:
				{
					var group = stack[top];
					top--;
					stack[top] = Combine((ShapeOperation)instruction.Operation, stack[top], group, instruction.Smoothness);
					break;
				}
			}
		}

		return top >= 0 ? stack[top] : EmptyDistance;
	}

	/// <summary>Distance to one primitive on its own, ignoring how it combines. Used for picking.</summary>
	public float EvaluateInstruction(int instructionIndex, Vector3 point)
	{
		return EvaluatePrimitive(_instructions[instructionIndex], point);
	}

	/// <summary>Normalised gradient by central differences; points out of the surface.</summary>
	public Vector3 EvaluateNormal(Vector3 point, float step)
	{
		var dx = Evaluate(point + new Vector3(step, 0.0f, 0.0f)) - Evaluate(point - new Vector3(step, 0.0f, 0.0f));
		var dy = Evaluate(point + new Vector3(0.0f, step, 0.0f)) - Evaluate(point - new Vector3(0.0f, step, 0.0f));
		var dz = Evaluate(point + new Vector3(0.0f, 0.0f, step)) - Evaluate(point - new Vector3(0.0f, 0.0f, step));
		var gradient = new Vector3(dx, dy, dz);
		var length = gradient.Length();

		return length > 1.0e-12f ? gradient / length : Vector3.UnitY;
	}

	/// <summary>Polynomial smooth minimum: exactly min(a, b) where the two are more than k apart.</summary>
	public static float SmoothMin(float a, float b, float k)
	{
		if (k <= 0.0f)
		{
			return MathF.Min(a, b);
		}

		var h = MathF.Max(k - MathF.Abs(a - b), 0.0f) / k;
		return MathF.Min(a, b) - h * h * k * 0.25f;
	}

	public static float SmoothMax(float a, float b, float k)
	{
		return -SmoothMin(-a, -b, k);
	}

	public static float Combine(ShapeOperation operation, float accumulated, float distance, float smoothness)
	{
		return operation switch
		{
			ShapeOperation.Union => SmoothMin(accumulated, distance, smoothness),
			ShapeOperation.Subtract => SmoothMax(accumulated, -distance, smoothness),
			ShapeOperation.Intersect => SmoothMax(accumulated, distance, smoothness),
			_ => accumulated
		};
	}

	private static float EvaluatePrimitive(in ShapeSdfInstruction instruction, Vector3 point)
	{
		var p4 = new Vector4(point, 1.0f);
		var local = new Vector3(
			Vector4.Dot(instruction.Row0, p4),
			Vector4.Dot(instruction.Row1, p4),
			Vector4.Dot(instruction.Row2, p4));

		return ShapePrimitives.Evaluate((ShapePrimitiveKind)instruction.Primitive, instruction.Dimensions, local) * instruction.Scale;
	}

	private static void Emit(
		ShapeNode node,
		Matrix4x4 parentMatrix,
		float parentScale,
		int depth,
		bool contributes,
		float inheritedPadding,
		List<ShapeSdfInstruction> instructions,
		List<Guid> ids,
		ref ShapeBounds bounds)
	{
		var matrix = node.GetLocalMatrix() * parentMatrix;
		var scale = parentScale * MathF.Max(node.Scale, ShapeNode.MinScale);

		// Only unions can grow the shape. Subtracted and intersected material bounds nothing, and a blend can push
		// the surface out by at most a quarter of its radius, which every enclosing blend adds to.
		var padding = inheritedPadding + MathF.Max(node.Smoothness, 0.0f) * 0.25f * parentScale;
		var nodeContributes = contributes && node.Operation == ShapeOperation.Union;

		if (node.IsGroup)
		{
			if (depth > MaxStackDepth)
			{
				throw new InvalidOperationException($"Shape groups are nested deeper than {MaxStackDepth} levels.");
			}

			instructions.Add(new ShapeSdfInstruction
			{
				Opcode = (uint)ShapeSdfOpcode.PushEmpty
			});
			ids.Add(Guid.Empty);

			foreach (var child in node.Children)
			{
				Emit(child, matrix, scale, depth + 1, nodeContributes, padding, instructions, ids, ref bounds);
			}

			// The root group is the result itself; every other group folds into its parent.
			if (depth > 1)
			{
				instructions.Add(new ShapeSdfInstruction
				{
					Opcode = (uint)ShapeSdfOpcode.PopCombine,
					Operation = (uint)node.Operation,
					Smoothness = MathF.Max(node.Smoothness, 0.0f) * parentScale
				});
				ids.Add(node.Id);
			}

			return;
		}

		if (Matrix4x4.Invert(matrix, out var inverse) == false)
		{
			return;
		}

		var dimensions = ShapePrimitives.Sanitize(node.Primitive, node.Dimensions);

		// Row-vector convention: local = p * inverse, so each local component is a dot product with one column.
		instructions.Add(new ShapeSdfInstruction
		{
			Opcode = (uint)ShapeSdfOpcode.Primitive,
			Primitive = (uint)node.Primitive,
			Operation = (uint)node.Operation,
			Smoothness = MathF.Max(node.Smoothness, 0.0f) * parentScale,
			Row0 = new Vector4(inverse.M11, inverse.M21, inverse.M31, inverse.M41),
			Row1 = new Vector4(inverse.M12, inverse.M22, inverse.M32, inverse.M42),
			Row2 = new Vector4(inverse.M13, inverse.M23, inverse.M33, inverse.M43),
			Dimensions = dimensions,
			Scale = scale
		});
		ids.Add(node.Id);

		if (nodeContributes)
		{
			var halfExtents = ShapePrimitives.GetLocalHalfExtents(node.Primitive, dimensions);
			bounds = bounds.Union(ShapeBounds.FromTransformedBox(halfExtents, matrix).Expand(padding));
		}
	}
}

/// <summary>An axis-aligned box in shape space.</summary>
public readonly record struct ShapeBounds(Vector3 Min, Vector3 Max)
{
	public static ShapeBounds Empty => new(new Vector3(float.MaxValue), new Vector3(float.MinValue));

	public bool IsEmpty => Min.X > Max.X || Min.Y > Max.Y || Min.Z > Max.Z;
	public Vector3 Size => IsEmpty ? Vector3.Zero : Max - Min;
	public Vector3 Center => (Min + Max) * 0.5f;

	public ShapeBounds Union(ShapeBounds other)
	{
		if (IsEmpty)
		{
			return other;
		}

		if (other.IsEmpty)
		{
			return this;
		}

		return new ShapeBounds(Vector3.Min(Min, other.Min), Vector3.Max(Max, other.Max));
	}

	public ShapeBounds Expand(float amount)
	{
		return IsEmpty ? this : new ShapeBounds(Min - new Vector3(amount), Max + new Vector3(amount));
	}

	public static ShapeBounds FromTransformedBox(Vector3 halfExtents, Matrix4x4 matrix)
	{
		var result = Empty;
		for (var corner = 0; corner < 8; corner++)
		{
			var local = new Vector3(
				(corner & 1) == 0 ? -halfExtents.X : halfExtents.X,
				(corner & 2) == 0 ? -halfExtents.Y : halfExtents.Y,
				(corner & 4) == 0 ? -halfExtents.Z : halfExtents.Z);
			var point = Vector3.Transform(local, matrix);
			result = result.Union(new ShapeBounds(point, point));
		}

		return result;
	}
}

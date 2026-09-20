using WolfEngine.ECS;

namespace WolfEngine.Rendering;

/// <summary>
/// Draws a screen-space outline around this entity's mesh.
///
/// The editor adds this to the current selection, but it is a runtime component
/// rather than an editor-only one so gameplay can use it for the same purpose --
/// highlighting an interactable, a quest target, or a hovered object.
///
/// The entity keeps rendering normally. The outline is an extra pass over the
/// finished image, so shadows, ray tracing, motion vectors and every
/// screen-space effect see an outlined entity exactly as they see any other.
/// </summary>
public struct OutlineHighlight : IEntityComponent
{
	/// <summary>WolfEngine blue.</summary>
	public static readonly ColorRGBA DefaultColor = new(0.675f, 0.78f, 0.984f, 1.0f);

	public const float DefaultThicknessPixels = 4.0f;

	public ColorRGBA Color;

	/// <summary>Border width in display pixels, held constant at any distance.</summary>
	public float ThicknessPixels;

	public void ApplyDefaultValues(World world, Entity entity)
	{
		_ = world;
		_ = entity;
		Color = DefaultColor;
		ThicknessPixels = DefaultThicknessPixels;
	}

	public float GetResolvedThicknessPixels() =>
		ThicknessPixels > 0.0f ? ThicknessPixels : DefaultThicknessPixels;
}

/// <summary>One outlined mesh, resolved on the game thread for the render thread.</summary>
public readonly struct OutlinePacket
{
	public OutlinePacket(Mesh mesh, System.Numerics.Matrix4x4 transform, ColorRGBA color, float thicknessPixels)
	{
		Mesh = mesh ?? throw new ArgumentNullException(nameof(mesh));
		Transform = transform;
		Color = color;
		ThicknessPixels = thicknessPixels;
	}

	public Mesh Mesh { get; }
	public System.Numerics.Matrix4x4 Transform { get; }
	public ColorRGBA Color { get; }
	public float ThicknessPixels { get; }
}

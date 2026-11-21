using System.Numerics;
using WolfEngine.ECS;
using WolfEngine.Editor.UI;
using WolfEngine.Rendering;

namespace WolfEngine.Editor;

/// <summary>
/// Marks every entity that has an icon set with that icon in the scene, so entities without visible
/// geometry, such as lights and cameras, can be found. It draws whatever icon the entity has, including
/// ones set by hand, and does not look at the entity's components. Switched with the Gizmos menu in the
/// Scene window.
/// </summary>
/// <remarks>
/// Not an <see cref="IOnDrawGizmos"/> system because icons live on the <see cref="EditorScene"/>, which
/// that interface does not receive.
/// </remarks>
public sealed class EntityIconGizmoDrawer
{
	public const float IconSizePixels = 24.0f;

	private static readonly ColorRGBA IconTint = ColorRGBA.White;
	private static readonly ColorRGBA SelectedIconTint = new(1.0f, 0.75f, 0.2f, 1.0f);

	private readonly IGizmoLineRenderer _lineRenderer;
	private readonly IIconManager _icons;

	public EntityIconGizmoDrawer(IGizmoLineRenderer lineRenderer, IIconManager icons)
	{
		_lineRenderer = lineRenderer ?? throw new ArgumentNullException(nameof(lineRenderer));
		_icons = icons ?? throw new ArgumentNullException(nameof(icons));
	}

	public void Draw(EditorScene scene)
	{
		ArgumentNullException.ThrowIfNull(scene);

		if (EditorPreferences.GetShowEntityIconGizmos() == false)
		{
			return;
		}

		var world = scene.World;
		foreach (var (entity, iconName) in scene.EntityIcons)
		{
			if (world.IsAlive(entity) == false ||
			    world.HasComponent<WorldTransform>(entity) == false ||
			    _icons.TryGet(iconName, out var texture) == false)
			{
				continue;
			}

			var position = world.GetComponent<WorldTransform>(entity).LocalToWorld.Translation;
			var tint = EditorGui.SelectedEntities.Contains(entity) ? SelectedIconTint : IconTint;
			_lineRenderer.DrawIcon(position, texture, IconSizePixels, tint);
		}
	}
}

using System.Numerics;
using ImGuiNET;
using WolfEngine.ECS;
using WolfEngine.Physics;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor.UI;

/// <summary>How a viewport pick combines with the selection that is already there.</summary>
public enum ScenePickSelectionMode
{
	Replace,
	Add,
	Toggle
}

/// <summary>
/// Turns a left click on the scene viewport into an entity selection.
/// </summary>
public sealed class SceneSelectionController
{
	/// <summary>
	/// How far the cursor may travel between press and release and still count as a click. Selecting
	/// on press would fight camera and gizmo drags that begin the same way, so selection is resolved
	/// on release and a press that turned into a drag is discarded.
	/// </summary>
	private const float ClickDragThresholdPixels = 4.0f;

	/// <summary>Fallback pick range for cameras that report no usable far plane.</summary>
	private const float MinimumPickDistance = 1000.0f;

	private readonly EditorViewportStateBus _viewportStateBus;
	private readonly EditorCameraContext _cameraContext;
	private readonly RigidbodySystem _rigidbodySystem;
	private bool _pressOwnedByViewport;
	private Vector2 _pressPosition;

	public SceneSelectionController(
		EditorViewportStateBus viewportStateBus,
		EditorCameraContext cameraContext,
		RigidbodySystem rigidbodySystem)
	{
		_viewportStateBus = viewportStateBus ?? throw new ArgumentNullException(nameof(viewportStateBus));
		_cameraContext = cameraContext ?? throw new ArgumentNullException(nameof(cameraContext));
		_rigidbodySystem = rigidbodySystem ?? throw new ArgumentNullException(nameof(rigidbodySystem));
	}

	/// <summary>
	/// Resolves the selection change for a pick. Shift extends the selection, the primary modifier
	/// (Ctrl, or Cmd on macOS) toggles a single entity, matching the entity hierarchy.
	/// </summary>
	public static ScenePickSelectionMode ResolveSelectionMode(bool shiftDown, bool primaryModifierDown)
	{
		if (primaryModifierDown)
		{
			return ScenePickSelectionMode.Toggle;
		}

		return shiftDown ? ScenePickSelectionMode.Add : ScenePickSelectionMode.Replace;
	}

	/// <summary>
	/// Must run after the transform gizmo has handled the frame, so a press that the gizmo claimed is
	/// visible here and does not also move the selection.
	/// </summary>
	internal void Update(EditorScene scene)
	{
		ArgumentNullException.ThrowIfNull(scene);

		var viewportState = _viewportStateBus.GetUiState();
		if (viewportState.Visible == false)
		{
			_pressOwnedByViewport = false;
			return;
		}

		if (ImGui.IsMouseDown(ImGuiMouseButton.Left) == false && ImGui.IsMouseReleased(ImGuiMouseButton.Left) == false)
		{
			// This only runs while the transform tool is active, so a press can begin in another tool
			// or in play mode and never be seen here. Dropping the latch whenever the button is idle
			// keeps such a press from being honoured as a click much later.
			_pressOwnedByViewport = false;
		}

		if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
		{
			// The gizmo only reports a drag while the button is held, and it has already released by
			// the time this sees the mouse-up. Latching ownership at press time is therefore the only
			// point where a gizmo press and a viewport press can still be told apart.
			_pressOwnedByViewport = viewportState.PointerAvailable && _viewportStateBus.IsGizmoDragging() == false;
			_pressPosition = ImGui.GetIO().MousePos;
		}

		if (ImGui.IsMouseReleased(ImGuiMouseButton.Left) == false)
		{
			return;
		}

		var pressOwnedByViewport = _pressOwnedByViewport;
		_pressOwnedByViewport = false;
		var mousePosition = ImGui.GetIO().MousePos;
		if (pressOwnedByViewport == false ||
		    Vector2.Distance(mousePosition, _pressPosition) > ClickDragThresholdPixels)
		{
			return;
		}

		var io = ImGui.GetIO();
		var primaryModifierDown = io.KeyCtrl || io.KeySuper;
		ApplySelection(
			scene.World,
			viewportState,
			mousePosition,
			ResolveSelectionMode(io.KeyShift, primaryModifierDown));
	}

	private void ApplySelection(
		World world,
		in SceneViewportUiState viewportState,
		Vector2 mousePosition,
		ScenePickSelectionMode mode)
	{
		if (_cameraContext.TryGet(out var camera, out var cameraWorldTransform) == false ||
		    SceneViewportRayUtility.TryBuildInverseViewProjection(camera, cameraWorldTransform, out var inverseViewProjection) == false ||
		    SceneViewportRayUtility.TryBuildWorldRay(viewportState, mousePosition, inverseViewProjection, out var ray) == false)
		{
			return;
		}

		var maxDistance = MathF.Max(camera.FarPlane, MinimumPickDistance);
		if (TryPick(world, ray, maxDistance, out var entity) == false)
		{
			// A click on empty space clears the selection, but a modified click is an edit to an
			// existing selection and must not throw it away when it misses.
			if (mode == ScenePickSelectionMode.Replace)
			{
				EditorGui.ClearEntitySelection();
			}

			return;
		}

		// Focus is not requested: a click in the viewport should leave focus in the viewport rather
		// than pulling the Components tab to the front on every pick.
		switch (mode)
		{
			case ScenePickSelectionMode.Toggle:
				EditorGui.ToggleEntitySelection(entity, world, requestFocus: false);
				break;
			case ScenePickSelectionMode.Add:
				EditorGui.AddEntitySelection(entity, world, requestFocus: false);
				break;
			default:
				EditorGui.ReplaceEntitySelection(entity, world, requestFocus: false);
				break;
		}
	}

	private bool TryPick(World world, in SceneViewportRay ray, float maxDistance, out Entity entity)
	{
		entity = default;
		var hasMeshHit = SceneViewportPicker.TryPick(world, ray, maxDistance, out var meshHit);
		var meshDistance = hasMeshHit ? meshHit.Distance : float.PositiveInfinity;
		if (TryPickTerrain(world, ray, maxDistance, out var terrainEntity, out var terrainDistance) &&
		    terrainDistance < meshDistance)
		{
			entity = terrainEntity;
			return true;
		}

		if (hasMeshHit == false)
		{
			return false;
		}

		entity = meshHit.Entity;
		return true;
	}

	/// <summary>
	/// Terrain is not drawn through a mesh renderer, so its surface is invisible to
	/// <see cref="SceneViewportPicker"/>. Its heightfield collider is queried instead — the same path
	/// the terrain brush already aims with — and non-terrain colliders are rejected so that an
	/// invisible trigger volume never becomes selectable when nothing was drawn for it.
	/// </summary>
	private bool TryPickTerrain(
		World world,
		in SceneViewportRay ray,
		float maxDistance,
		out Entity entity,
		out float distance)
	{
		entity = default;
		distance = float.PositiveInfinity;
		if (HasTerrain(world) == false)
		{
			// Querying physics builds world state on demand. Scenes without terrain have nothing to
			// gain from that, so they never pay for it.
			return false;
		}

		if (_rigidbodySystem.TryRaycast(world, ray.Origin, ray.Direction * maxDistance, out var hit) == false ||
		    world.IsAlive(hit.Entity) == false ||
		    world.HasComponent<TerrainComponent>(hit.Entity) == false)
		{
			return false;
		}

		entity = hit.Entity;
		distance = hit.Fraction * maxDistance;
		return true;
	}

	private static bool HasTerrain(World world)
	{
		foreach (var entry in world.View<WorldTransform, TerrainComponent>())
		{
			if (world.IsEnabled(entry.Entity))
			{
				return true;
			}
		}

		return false;
	}
}

using System.Numerics;
using ImGuiNET;
using WolfEngine.ECS;
using WolfEngine.Mathematics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor.UI;

/// <summary>
/// The scene view's transform gizmo: binds a <see cref="TransformGizmo"/> to the primary render
/// view, the editor camera and the selected entities.
/// </summary>
public sealed class TransformGizmoController
{
	private readonly EditorViewportStateBus _viewportStateBus;
	private readonly EditorCameraContext _cameraContext;
	private readonly IEditorInteractionState _interactionState;
	private readonly IEditorSceneSnapshotService _sceneSnapshotService;
	private readonly IEditorUndoRedoService _undoRedoService;
	private readonly TransformGizmo _gizmo = new();

	public TransformGizmoController(
		EditorViewportStateBus viewportStateBus,
		EditorCameraContext cameraContext,
		IEditorInteractionState interactionState,
		IEditorSceneSnapshotService sceneSnapshotService,
		IEditorUndoRedoService undoRedoService)
	{
		_viewportStateBus = viewportStateBus ?? throw new ArgumentNullException(nameof(viewportStateBus));
		_cameraContext = cameraContext ?? throw new ArgumentNullException(nameof(cameraContext));
		_interactionState = interactionState ?? throw new ArgumentNullException(nameof(interactionState));
		_sceneSnapshotService = sceneSnapshotService ?? throw new ArgumentNullException(nameof(sceneSnapshotService));
		_undoRedoService = undoRedoService ?? throw new ArgumentNullException(nameof(undoRedoService));
	}

	public void DrawAndHandle(
		EditorScene scene,
		World world,
		IReadOnlyList<Entity> selectedEntities,
		TransformGizmoMode mode,
		TransformSpace space,
		TransformPivotMode pivotMode)
	{
		var viewportState = _viewportStateBus.GetUiState(RenderViewId.Primary);
		if (IsViewportValid(viewportState) == false ||
		    selectedEntities.Count == 0 ||
		    _cameraContext.TryGet(out var camera, out _) == false ||
		    camera.ScreenResolution.X <= 0 ||
		    camera.ScreenResolution.Y <= 0)
		{
			CancelAndPublish();
			return;
		}

		var transformEntities = GetTransformEntities(world, selectedEntities);
		if (transformEntities.Count == 0)
		{
			CancelAndPublish();
			return;
		}

		var pivotWorld = transformEntities.Count == 1
			? ResolveGizmoPivotWorld(world, transformEntities[0], pivotMode)
			: GetAverageWorldPosition(world, transformEntities);
		var target = new SceneEntityGizmoTarget(
			scene,
			world,
			transformEntities,
			_interactionState,
			_sceneSnapshotService,
			_undoRedoService);
		var view = new TransformGizmoView(
			viewportState,
			_viewportStateBus.GetRenderState(RenderViewId.Primary).ViewProjection);
		var options = new TransformGizmoOptions(
			mode,
			space,
			pivotWorld,
			transformEntities.Count > 1 || pivotMode == TransformPivotMode.Center);

		_gizmo.Update(target, view, TransformGizmoPointer.FromImGui(viewportState), options);
		_gizmo.Draw(ImGui.GetWindowDrawList());
		_viewportStateBus.PublishGizmoDragging(_gizmo.IsDragging);
	}

	private void CancelAndPublish()
	{
		_gizmo.Cancel();
		_viewportStateBus.PublishGizmoDragging(false);
	}

	private static Vector3 ResolveGizmoPivotWorld(World world, Entity entity, TransformPivotMode pivotMode)
	{
		var worldPosition = world.GetComponent<WorldTransform>(entity).LocalToWorld.Translation;
		if (pivotMode == TransformPivotMode.TransformPivot)
		{
			return worldPosition;
		}

		if (TryGetVisualCenterWorld(world, entity, out var visualCenter))
		{
			return visualCenter;
		}

		return worldPosition;
	}

	private static List<Entity> GetTransformEntities(World world, IReadOnlyList<Entity> selectedEntities)
	{
		var entities = new List<Entity>(selectedEntities.Count);
		foreach (var entity in selectedEntities)
		{
			if (world.IsAlive(entity) &&
			    world.HasComponent<LocalTransform>(entity) &&
			    world.HasComponent<WorldTransform>(entity))
			{
				entities.Add(entity);
			}
		}

		return entities;
	}

	private static Vector3 GetAverageWorldPosition(World world, IReadOnlyList<Entity> entities)
	{
		var position = Vector3.Zero;
		foreach (var entity in entities)
		{
			position += world.GetComponent<WorldTransform>(entity).LocalToWorld.Translation;
		}

		return position / entities.Count;
	}

	private static bool TryGetVisualCenterWorld(World world, Entity entity, out Vector3 visualCenter)
	{
		visualCenter = Vector3.Zero;
		if (TryGetEntityMeshCenter(world, entity, out visualCenter))
		{
			return true;
		}

		if (world.HasComponent<Children>(entity) == false)
		{
			return false;
		}

		var sum = Vector3.Zero;
		var count = 0;
		var child = world.GetComponent<Children>(entity).First;
		while (child.IsValid)
		{
			AccumulateDescendantMeshCenters(world, child, ref sum, ref count);
			if (world.HasComponent<Sibling>(child) == false)
			{
				break;
			}

			child = world.GetComponent<Sibling>(child).Next;
		}

		if (count == 0)
		{
			return false;
		}

		visualCenter = sum / count;
		return true;
	}

	private static void AccumulateDescendantMeshCenters(World world, Entity entity, ref Vector3 sum, ref int count)
	{
		if (TryGetEntityMeshCenter(world, entity, out var center))
		{
			sum += center;
			count++;
		}

		if (world.HasComponent<Children>(entity) == false)
		{
			return;
		}

		var child = world.GetComponent<Children>(entity).First;
		while (child.IsValid)
		{
			AccumulateDescendantMeshCenters(world, child, ref sum, ref count);
			if (world.HasComponent<Sibling>(child) == false)
			{
				break;
			}

			child = world.GetComponent<Sibling>(child).Next;
		}
	}

	private static bool TryGetEntityMeshCenter(World world, Entity entity, out Vector3 centerWorld)
	{
		centerWorld = Vector3.Zero;
		if (world.HasComponent<MeshRenderer>(entity) == false || world.HasComponent<WorldTransform>(entity) == false)
		{
			return false;
		}

		ref var meshRenderer = ref world.GetComponent<MeshRenderer>(entity);
		if (meshRenderer.Mesh is null)
		{
			return false;
		}

		ref var entityWorldTransform = ref world.GetComponent<WorldTransform>(entity);
		var centerLocal = meshRenderer.Mesh.BoundingSphere.Center;
		centerWorld = Vector3.Transform(centerLocal, entityWorldTransform.LocalToWorld);
		return true;
	}

	private static bool IsViewportValid(in SceneViewportUiState state)
	{
		return state.Visible &&
		       state.ContentSizePixels.X > 0 &&
		       state.ContentSizePixels.Y > 0 &&
		       state.ImageMax.X > state.ImageMin.X &&
		       state.ImageMax.Y > state.ImageMin.Y;
	}
}

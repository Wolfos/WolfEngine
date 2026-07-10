using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using WolfEngine.ECS;
using WolfEngine.Editor.Projects;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor.UI;

public class EntitiesWindow : EditorWindow, IEditorEntityDeletionHandler
{
	private static readonly List<Entity> AllEntities = new();
	private static readonly List<Entity> RootEntities = new();
	private static readonly List<Entity> VisibleEntities = new();
	private static readonly List<Entity> PendingDeleteEntities = new();
	private static readonly Vector2 EntityIconSize = Vector2.One * 15.5f;
	private const string ContextMenuId = "EntitiesContextMenu";

	private readonly IIconManager _iconManager;
	private readonly IEditorInteractionState _interactionState;
	private readonly IEditorSceneSnapshotService _sceneSnapshotService;
	private readonly IEditorUndoRedoService _undoRedoService;
	private readonly IPrefabAssetCreator _prefabAssetCreator;
	private readonly IAssetSelectionService _assetSelectionService;
	private readonly IEditorNotificationService _notificationService;
	private Entity? _contextMenuEntity;
	private Entity? _pressedEntity;
	private Entity? _draggedEntity;
	private Entity? _hoveredEntity;
	private EntitySelectionClick? _pendingSelectionClick;

	public EntitiesWindow(
		IIconManager iconManager,
		IEditorInteractionState interactionState,
		IEditorSceneSnapshotService sceneSnapshotService,
		IEditorUndoRedoService undoRedoService,
		IPrefabAssetCreator prefabAssetCreator,
		IAssetSelectionService assetSelectionService,
		IEditorNotificationService notificationService)
	{
		_iconManager = iconManager;
		_interactionState = interactionState;
		_sceneSnapshotService = sceneSnapshotService;
		_undoRedoService = undoRedoService;
		_prefabAssetCreator = prefabAssetCreator;
		_assetSelectionService = assetSelectionService;
		_notificationService = notificationService;
	}

	public override string Name => "Entities";

	public override void Draw(EditorScene scene)
	{
		var world = scene.World;

		ImGui.SetNextWindowPos(new Vector2(0.0f, 0.0f), ImGuiCond.FirstUseEver);
		ImGui.SetNextWindowSize(new Vector2(188.0f, 720.0f), ImGuiCond.FirstUseEver);

		ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(2, 3.0f));
		Begin();
		ImGui.PopStyleVar();
		if (ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
		{
			_interactionState.SetFocusedWindow(EditorFocusedWindow.Entities);
		}

		world.GetAllEntities(AllEntities);
		BuildRootList(world);
		_hoveredEntity = null;
		VisibleEntities.Clear();

		var style = ImGui.GetStyle();
		ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(style.ItemSpacing.X, 0.0f));
		ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(style.FramePadding.X, 4.0f));

		foreach (var entity in RootEntities)
		{
			DrawEntityNode(entity, world, scene);
		}

		ClearSelectionOnBackgroundClick();
		ApplyPendingSelectionClick(world);

		CompleteDragDrop(scene);
		DrawContextMenu(scene);
		ImGui.PopStyleVar(2);
		ImGui.End();
	}

	private static void BuildRootList(World world)
	{
		RootEntities.Clear();
		foreach (var entity in AllEntities)
		{
			if (!world.HasComponent<Parent>(entity))
			{
				RootEntities.Add(entity);
				continue;
			}

			var parent = world.GetComponent<Parent>(entity).Value;
			if (!parent.IsValid)
			{
				RootEntities.Add(entity);
			}
		}
	}

	private unsafe void DrawEntityNode(Entity entity, World world, EditorScene scene)
	{
		ImGui.PushID(entity.Index);

		VisibleEntities.Add(entity);
		var isSelected = EditorGui.SelectedEntities.Contains(entity);
		var hasChildren = world.HasComponent<Children>(entity)
		                  && world.GetComponent<Children>(entity).First.IsValid;
		var flags = ImGuiTreeNodeFlags.SpanFullWidth | ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.FramePadding;
		if (!hasChildren)
		{
			flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;
		}

		if (isSelected)
		{
			flags |= ImGuiTreeNodeFlags.Selected;
			var selectedColor = ImGui.GetStyleColorVec4(ImGuiCol.HeaderActive);
			ImGui.PushStyleColor(ImGuiCol.Header, *selectedColor);
			ImGui.PushStyleColor(ImGuiCol.HeaderHovered, *selectedColor);
			ImGui.PushStyleColor(ImGuiCol.HeaderActive, *selectedColor);
		}

		var iconName = scene.EntityIcons.TryGetValue(entity, out var assignedIconName)
			? assignedIconName
			: EditorPrefabUtility.IsPrefabEntity(scene, entity)
				? "prefab"
				: "object";
		var iconTexture = ResolveIconTexture(_iconManager, iconName);

		var nameComponent = world.GetComponent<NameComponent>(entity);
		var name = nameComponent.Name ?? "Unnamed";
		var nodeCursorPosition = ImGui.GetCursorScreenPos();
		var open = ImGui.TreeNodeEx("##EntityNode", flags);
		var leftClicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
		if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByPopup |
		                        ImGuiHoveredFlags.AllowWhenBlockedByActiveItem))
		{
			_hoveredEntity = entity;
		}

		DrawEntityLabelWithIcon(name, iconTexture, nodeCursorPosition.X);

		if (leftClicked)
		{
			_pressedEntity = entity;
			_interactionState.SetFocusedWindow(EditorFocusedWindow.Entities);
			var io = ImGui.GetIO();
			_pendingSelectionClick = new EntitySelectionClick(
				entity,
				io.KeyShift,
				io.KeyCtrl);
		}

		if (hasChildren && open)
		{
			var childEntity = world.GetComponent<Children>(entity).First;
			while (childEntity.IsValid)
			{
				DrawEntityNode(childEntity, world, scene);

				if (world.HasComponent<Sibling>(childEntity))
				{
					childEntity = world.GetComponent<Sibling>(childEntity).Next;
				}
				else
				{
					break;
				}
			}

			ImGui.TreePop();
		}

		if (isSelected)
		{
			ImGui.PopStyleColor(3);
		}

		ImGui.PopID();
	}

	private void ClearSelectionOnBackgroundClick()
	{
		if (_hoveredEntity is null &&
		    ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows) &&
		    ImGui.IsMouseClicked(ImGuiMouseButton.Left))
		{
			EditorGui.ClearEntitySelection();
			_interactionState.SetFocusedWindow(EditorFocusedWindow.Entities);
		}
	}

	private void ApplyPendingSelectionClick(World world)
	{
		if (_pendingSelectionClick is not { } click)
		{
			return;
		}

		_pendingSelectionClick = null;
		if (click.Shift)
		{
			EditorGui.AddEntitySelectionRange(VisibleEntities, click.Entity, world);
		}
		else if (click.Additive)
		{
			EditorGui.AddEntitySelection(click.Entity, world);
		}
		else
		{
			EditorGui.ReplaceEntitySelection(click.Entity, world);
		}
	}

	private void CompleteDragDrop(EditorScene scene)
	{
		if (_pressedEntity is { } pressedEntity && _draggedEntity is null &&
		    ImGui.IsMouseDragging(ImGuiMouseButton.Left))
		{
			_draggedEntity = pressedEntity;
		}

		if (_draggedEntity is not { } draggedEntity)
		{
			if (ImGui.IsMouseDown(ImGuiMouseButton.Left) == false)
			{
				_pressedEntity = null;
			}

			return;
		}

		if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
		{
			ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
			return;
		}

		if (ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows))
		{
			EntityHierarchyEditorOperations.TryReparentEntity(
				scene,
				draggedEntity,
				_hoveredEntity,
				_sceneSnapshotService,
				_undoRedoService,
				_interactionState);
		}

		_pressedEntity = null;
		_draggedEntity = null;
	}

	private static nint ResolveIconTexture(IIconManager icons, string iconName)
	{
		if (icons.TryGet(iconName, out var textureId))
		{
			return textureId;
		}

		if (icons.TryGet("object", out textureId))
		{
			return textureId;
		}

		return 0;
	}

	private static void DrawEntityLabelWithIcon(string label, nint iconTexture, float nodeCursorX)
	{
		var itemMin = ImGui.GetItemRectMin();
		var itemMax = ImGui.GetItemRectMax();
		var rowHeight = itemMax.Y - itemMin.Y;

		var labelStartX = nodeCursorX + ImGui.GetTreeNodeToLabelSpacing();
		var iconSize = MathF.Min(EntityIconSize.X, MathF.Max(1.0f, rowHeight - 2.0f));
		var iconPosition = new Vector2(labelStartX, itemMin.Y + (rowHeight - iconSize) * 0.5f);
		var textSize = ImGui.CalcTextSize(label);
		var textPosition = new Vector2(
			iconPosition.X + iconSize + 4.0f,
			itemMin.Y + (rowHeight - textSize.Y) * 0.5f);
		var drawList = ImGui.GetWindowDrawList();

		if (iconTexture != 0)
		{
			drawList.AddImage(iconTexture, iconPosition, iconPosition + Vector2.One * iconSize);
		}

		drawList.AddText(textPosition, ImGui.GetColorU32(ImGuiCol.Text), label);
	}

	private void DrawContextMenu(EditorScene scene)
	{
		if (ImGui.BeginPopupContextWindow(ContextMenuId, ImGuiPopupFlags.MouseButtonRight) == false)
		{
			return;
		}

		if (ImGui.IsWindowAppearing())
		{
			_contextMenuEntity = _hoveredEntity;
			if (_contextMenuEntity is { } hoveredEntity)
			{
				_interactionState.SetFocusedWindow(EditorFocusedWindow.Entities);
				EditorGui.SelectEntity(hoveredEntity, scene.World, requestFocus: false);
			}
		}

		if (ImGui.BeginMenu("Create"))
		{
			if (ImGui.MenuItem("Entity"))
			{
				var createdEntity = scene.World.CreateEntity("Entity", Matrix4x4.Identity);
				EditorGui.SelectEntity(createdEntity, scene.World);
				_interactionState.MarkSceneDirty();
			}

			ImGui.EndMenu();
		}

		if (_contextMenuEntity is { } entity && scene.World.IsAlive(entity))
		{
			if (ImGui.MenuItem("Duplicate", "Ctrl/Cmd+D"))
			{
				DuplicateEntity(entity, scene);
				ImGui.CloseCurrentPopup();
			}

			if (ImGui.MenuItem("Save as Prefab"))
			{
				SaveEntityAsPrefab(scene, entity);
				ImGui.CloseCurrentPopup();
			}

			if (ImGui.MenuItem("Delete"))
			{
				DeleteEntity(entity, scene);
				ImGui.CloseCurrentPopup();
			}

			ImGui.Separator();
		}


		ImGui.EndPopup();
	}

	private void DuplicateEntity(Entity entity, EditorScene scene)
	{
		if (scene.World.IsAlive(entity) == false)
		{
			return;
		}

		if (EditorPrefabUtility.IsNestedPrefabEntity(scene, entity))
		{
			_notificationService.ReportError(
				"Cannot duplicate entities inside prefab instances. Duplicate the prefab root instance instead.");
			return;
		}

		EntityHierarchyEditorOperations.DuplicateEntity(
			scene,
			entity,
			_sceneSnapshotService,
			_undoRedoService,
			_interactionState);
	}

	private void DeleteEntity(Entity entity, EditorScene scene)
	{
		if (scene.World.IsAlive(entity) == false)
		{
			return;
		}

		if (EditorPrefabUtility.IsNestedPrefabEntity(scene, entity))
		{
			_notificationService.ReportError(
				"Cannot delete entities inside prefab instances. Delete the prefab root instance instead.");
			return;
		}

		PendingDeleteEntities.Clear();
		CollectEntitySubtree(entity, scene.World, PendingDeleteEntities);
		var deletedEntities = _sceneSnapshotService.CaptureDeletedEntities(scene, PendingDeleteEntities);
		scene.World.DestroyEntity(entity);

		for (var i = 0; i < PendingDeleteEntities.Count; i++)
		{
			var deletedEntity = PendingDeleteEntities[i];
			scene.EntityIcons.Remove(deletedEntity);
			scene.EntityCellKeys.Remove(deletedEntity);
			scene.EntityIds.Remove(deletedEntity);
			scene.EntityPrefabSourcePaths.Remove(deletedEntity);
		}

		if (EditorGui.HasSelectedEntity && PendingDeleteEntities.Contains(EditorGui.SelectedEntity))
		{
			EditorGui.ClearEntitySelection();
		}

		if (deletedEntities.Count > 0)
		{
			_undoRedoService.BeginCapture("Delete Entity");
			_undoRedoService.CommitCapture(new EntityDeletionUndoRedoEntry("Delete Entity", deletedEntities));
		}

		_interactionState.MarkSceneDirty();
		PendingDeleteEntities.Clear();
	}

	private void DeleteSelectedEntities(EditorScene scene)
	{
		PendingDeleteEntities.Clear();
		var roots = new List<Entity>();
		foreach (var entity in EditorGui.SelectedEntities)
		{
			if (scene.World.IsAlive(entity) == false || IsDescendantOfSelectedEntity(entity, scene.World))
			{
				continue;
			}

			if (EditorPrefabUtility.IsNestedPrefabEntity(scene, entity))
			{
				_notificationService.ReportError(
					"Cannot delete entities inside prefab instances. Delete the prefab root instance instead.");
				return;
			}

			roots.Add(entity);
			CollectEntitySubtree(entity, scene.World, PendingDeleteEntities);
		}

		if (roots.Count == 0)
		{
			return;
		}

		var deletedEntities = _sceneSnapshotService.CaptureDeletedEntities(scene, PendingDeleteEntities);
		foreach (var root in roots)
		{
			scene.World.DestroyEntity(root);
		}

		foreach (var entity in PendingDeleteEntities)
		{
			scene.EntityIcons.Remove(entity);
			scene.EntityCellKeys.Remove(entity);
			scene.EntityIds.Remove(entity);
			scene.EntityPrefabSourcePaths.Remove(entity);
		}

		EditorGui.ClearEntitySelection();
		if (deletedEntities.Count > 0)
		{
			_undoRedoService.BeginCapture("Delete Entity");
			_undoRedoService.CommitCapture(new EntityDeletionUndoRedoEntry("Delete Entity", deletedEntities));
		}

		_interactionState.MarkSceneDirty();
		PendingDeleteEntities.Clear();
	}

	public bool DeleteSelectedEntity(EditorScene scene)
	{
		if (EditorGui.HasSelectedEntity == false || scene.World.IsAlive(EditorGui.SelectedEntity) == false)
		{
			return false;
		}

		DeleteSelectedEntities(scene);
		return true;
	}

	public bool DuplicateSelectedEntity(EditorScene scene)
	{
		if (EditorGui.HasSelectedEntity == false || scene.World.IsAlive(EditorGui.SelectedEntity) == false)
		{
			return false;
		}

		DuplicateEntity(EditorGui.SelectedEntity, scene);
		return true;
	}

	private static void CollectEntitySubtree(Entity entity, World world, List<Entity> entities)
	{
		entities.Add(entity);
		if (world.HasComponent<Children>(entity) == false)
		{
			return;
		}

		var child = world.GetComponent<Children>(entity).First;
		while (child.IsValid)
		{
			var next = world.HasComponent<Sibling>(child)
				? world.GetComponent<Sibling>(child).Next
				: default;
			CollectEntitySubtree(child, world, entities);
			child = next;
		}
	}

	private static bool IsDescendantOfSelectedEntity(Entity entity, World world)
	{
		var current = entity;
		while (world.HasComponent<Parent>(current))
		{
			var parent = world.GetComponent<Parent>(current).Value;
			if (parent.IsValid == false)
			{
				return false;
			}

			if (EditorGui.SelectedEntities.Contains(parent))
			{
				return true;
			}

			current = parent;
		}

		return false;
	}

	private readonly record struct EntitySelectionClick(Entity Entity, bool Shift, bool Additive);

	private void SaveEntityAsPrefab(EditorScene scene, Entity entity)
	{
		var result = _prefabAssetCreator.SaveEntityAsPrefab(scene, entity, "Assets/Prefabs");
		if (result.Success)
		{
			if (result.AssetId is { } assetId)
			{
				_assetSelectionService.Select(assetId);
			}

			_interactionState.MarkSceneDirty();
			return;
		}

		if (string.IsNullOrWhiteSpace(result.ErrorMessage) == false)
		{
			_notificationService.ReportError(result.ErrorMessage);
		}
	}
}

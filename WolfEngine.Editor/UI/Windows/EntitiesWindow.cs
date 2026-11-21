using System.Numerics;
using ImGuiNET;
using WolfEngine.ECS;
using WolfEngine.Editor.Projects;

namespace WolfEngine.Editor.UI;

public sealed record EntitiesWindowRowState(string Name, bool MatchesSearch);
public sealed record EntitiesWindowState(string Search, IReadOnlyList<EntitiesWindowRowState> Rows);

public class EntitiesWindow : EditorWindow, IEditorEntityDeletionHandler
{
	private static readonly List<Entity> AllEntities = new();
	private static readonly List<Entity> RootEntities = new();
	private static readonly List<Entity> VisibleEntities = new();
	private static readonly List<EntitiesWindowRowState> DrawnRows = new();
	private static readonly List<Entity> PendingDeleteEntities = new();
	private static readonly List<Entity> DraggedEntities = new();
	private static readonly HashSet<Entity> RevealAncestors = new();
	private static readonly HashSet<Entity> SearchMatches = new();
	private static readonly HashSet<Entity> SearchMatchAncestors = new();
	private static readonly Vector2 EntityIconSize = Vector2.One * 15.5f;
	private const string ContextMenuId = "EntitiesContextMenu";
	private const float ClearSearchButtonSize = 20.0f;

	private readonly IIconManager _iconManager;
	private readonly IEditorInteractionState _interactionState;
	private readonly IEditorSceneSnapshotService _sceneSnapshotService;
	private readonly IEditorUndoRedoService _undoRedoService;
	private readonly IPrefabAssetCreator _prefabAssetCreator;
	private readonly IAssetSelectionService _assetSelectionService;
	private readonly IEditorNotificationService _notificationService;
	private readonly EditorCameraSystem? _editorCameraSystem;
	private Entity? _contextMenuEntity;
	private Entity? _pressedEntity;
	private Entity? _draggedEntity;
	private Entity? _hoveredEntity;
	private EntitySelectionClick? _pendingSelectionClick;
	private Entity? _deferredSelectionEntity;
	private Entity? _revealEntity;
	private string _searchText = string.Empty;
	private bool _wasSearchActive;

	public EntitiesWindow(
		IIconManager iconManager,
		IEditorInteractionState interactionState,
		IEditorSceneSnapshotService sceneSnapshotService,
		IEditorUndoRedoService undoRedoService,
		IPrefabAssetCreator prefabAssetCreator,
		IAssetSelectionService assetSelectionService,
		IEditorNotificationService notificationService,
		EditorCameraSystem? editorCameraSystem = null)
	{
		_iconManager = iconManager;
		_interactionState = interactionState;
		_sceneSnapshotService = sceneSnapshotService;
		_undoRedoService = undoRedoService;
		_prefabAssetCreator = prefabAssetCreator;
		_assetSelectionService = assetSelectionService;
		_notificationService = notificationService;
		_editorCameraSystem = editorCameraSystem;
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

		DrawSearchBar();

		world.GetAllEntities(AllEntities);
		BuildRootList(world);
		var isSearchActive = BuildSearchResults(world);
		_hoveredEntity = null;
		VisibleEntities.Clear();
		DrawnRows.Clear();
		BeginRevealRequest(world, isSearchActive);

		// The tree scrolls in its own child so the search bar stays in place above it.
		ImGui.BeginChild("##EntityTree", Vector2.Zero, ImGuiChildFlags.None);

		var style = ImGui.GetStyle();
		ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(style.ItemSpacing.X, 0.0f));
		ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(style.FramePadding.X, 4.0f));

		// Expansion while searching lives under its own ID scope, so the branches a search forces open
		// do not overwrite which branches the user had open in the unfiltered tree.
		ImGui.PushID(isSearchActive ? "Search" : "Tree");
		foreach (var entity in RootEntities)
		{
			if (IsShownBySearch(entity, isSearchActive) == false)
			{
				continue;
			}

			DrawEntityNode(entity, world, scene, isSearchActive);
		}

		ImGui.PopID();

		if (isSearchActive && SearchMatches.Count == 0)
		{
			ImGui.TextDisabled("No matching entities");
		}

		ClearSelectionOnBackgroundClick();
		ApplyPendingSelectionClick(world);
		EndRevealRequest();

		CompleteDragDrop(scene);
		DrawContextMenu(scene);
		ImGui.PopStyleVar(2);
		ImGui.EndChild();
		ImGui.End();
	}

	/// <summary>
	/// The search text and the rows drawn in the last frame, in tree order. Used by editor automation.
	/// </summary>
	public EntitiesWindowState GetState()
	{
		return new EntitiesWindowState(_searchText, DrawnRows.ToArray());
	}

	/// <summary>
	/// Replaces the search text, as typing into the search bar does. Used by editor automation.
	/// </summary>
	public void SetSearch(string search)
	{
		_searchText = search;
	}

	private void DrawSearchBar()
	{
		var style = ImGui.GetStyle();
		var hasSearchText = _searchText.Length > 0;
		var clearButtonWidth = hasSearchText
			? ClearSearchButtonSize + style.ItemSpacing.X
			: 0.0f;

		ImGui.SetNextItemWidth(MathF.Max(40.0f, ImGui.GetContentRegionAvail().X - clearButtonWidth));
		ImGui.InputTextWithHint("##EntitySearch", "Search entities...", ref _searchText, 256);
		if (ImGui.IsItemFocused() && ImGui.IsKeyPressed(ImGuiKey.Escape))
		{
			_searchText = string.Empty;
		}

		if (hasSearchText == false)
		{
			return;
		}

		ImGui.SameLine();
		if (DrawClearSearchButton())
		{
			_searchText = string.Empty;
		}
	}

	private bool DrawClearSearchButton()
	{
		var frameHeight = ImGui.GetFrameHeight();
		var buttonSize = new Vector2(ClearSearchButtonSize, frameHeight);
		ImGui.InvisibleButton("ClearEntitySearch", buttonSize);

		var min = ImGui.GetItemRectMin();
		var max = ImGui.GetItemRectMax();
		var drawList = ImGui.GetWindowDrawList();
		if (ImGui.IsItemHovered())
		{
			drawList.AddRectFilled(min, max, ImGui.GetColorU32(ImGuiCol.HeaderHovered), 4.0f);
		}

		var iconSize = MathF.Min(ClearSearchButtonSize, frameHeight) - 4.0f;
		var iconMin = (min + max - new Vector2(iconSize)) * 0.5f;
		var iconColor = ImGui.GetColorU32(ImGuiCol.TextDisabled);
		if (_iconManager.TryGet("close", out var texture))
		{
			drawList.AddImage(texture, iconMin, iconMin + new Vector2(iconSize), Vector2.Zero, Vector2.One, iconColor);
		}
		else
		{
			var inset = iconSize * 0.25f;
			drawList.AddLine(iconMin + new Vector2(inset), iconMin + new Vector2(iconSize - inset), iconColor, 1.5f);
			drawList.AddLine(
				iconMin + new Vector2(iconSize - inset, inset),
				iconMin + new Vector2(inset, iconSize - inset),
				iconColor,
				1.5f);
		}

		return ImGui.IsItemClicked();
	}

	/// <summary>
	/// Collects the entities whose name contains the search text, and every ancestor of them so the
	/// matches are still shown in their place in the hierarchy. Returns whether a search is active.
	/// </summary>
	private bool BuildSearchResults(World world)
	{
		SearchMatches.Clear();
		SearchMatchAncestors.Clear();

		var query = _searchText.Trim();
		if (query.Length == 0)
		{
			return false;
		}

		foreach (var entity in AllEntities)
		{
			if (world.HasComponent<NameComponent>(entity) == false)
			{
				continue;
			}

			var name = world.GetComponent<NameComponent>(entity).Name;
			if (name is null || name.Contains(query, StringComparison.OrdinalIgnoreCase) == false)
			{
				continue;
			}

			SearchMatches.Add(entity);
			AddSearchMatchAncestors(world, entity);
		}

		return true;
	}

	private static void AddSearchMatchAncestors(World world, Entity entity)
	{
		var current = entity;
		while (world.HasComponent<Parent>(current))
		{
			var parent = world.GetComponent<Parent>(current).Value;
			if (parent.IsValid == false)
			{
				return;
			}

			// An ancestor already in the set had its own ancestors added when it was first reached.
			if (SearchMatchAncestors.Add(parent) == false)
			{
				return;
			}

			current = parent;
		}
	}

	private static bool IsShownBySearch(Entity entity, bool isSearchActive)
	{
		if (isSearchActive == false)
		{
			return true;
		}

		return SearchMatches.Contains(entity) ||
		       SearchMatchAncestors.Contains(entity);
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

	private unsafe void DrawEntityNode(Entity entity, World world, EditorScene scene, bool isSearchActive)
	{
		ImGui.PushID(entity.Index);

		VisibleEntities.Add(entity);
		var isSelected = EditorGui.SelectedEntities.Contains(entity);
		var hasChildren = isSearchActive
			? SearchMatchAncestors.Contains(entity)
			: world.HasComponent<Children>(entity) && world.GetComponent<Children>(entity).First.IsValid;
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
		if (isSearchActive && hasChildren)
		{
			// While searching, every branch on the way to a match is shown open.
			ImGui.SetNextItemOpen(true);
		}
		else if (RevealAncestors.Contains(entity))
		{
			// Written into ImGui's own node storage, so the branch stays open afterwards and the user
			// can collapse it again like any other.
			ImGui.SetNextItemOpen(true);
		}

		var nodeCursorPosition = ImGui.GetCursorScreenPos();
		var open = ImGui.TreeNodeEx("##EntityNode", flags);
		if (_revealEntity == entity)
		{
			ImGui.SetScrollHereY(0.5f);
		}

		var leftClicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
		if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByPopup |
		                        ImGuiHoveredFlags.AllowWhenBlockedByActiveItem))
		{
			_hoveredEntity = entity;
		}

		// Ancestors that are only shown to place a match in the hierarchy are dimmed.
		var matchesSearch = isSearchActive == false || SearchMatches.Contains(entity);
		var labelColor = matchesSearch
			? ImGuiCol.Text
			: ImGuiCol.TextDisabled;
		DrawnRows.Add(new EntitiesWindowRowState(name, matchesSearch));
		DrawEntityLabelWithIcon(name, iconTexture, nodeCursorPosition.X, labelColor);

		if (leftClicked)
		{
			_pressedEntity = entity;
			_interactionState.SetFocusedWindow(EditorFocusedWindow.Entities);
			var io = ImGui.GetIO();
			if (isSelected && io.KeyShift == false && io.KeyCtrl == false)
			{
				// Pressing an already-selected row may be the start of dragging the whole selection, so
				// narrowing the selection to this row waits until the button is released without a drag.
				_deferredSelectionEntity = entity;
			}
			else
			{
				_pendingSelectionClick = new EntitySelectionClick(
					entity,
					io.KeyShift,
					io.KeyCtrl);
			}
		}

		if (hasChildren && open)
		{
			var childEntity = world.GetComponent<Children>(entity).First;
			while (childEntity.IsValid)
			{
				if (IsShownBySearch(childEntity, isSearchActive))
				{
					DrawEntityNode(childEntity, world, scene, isSearchActive);
				}

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

	/// <summary>
	/// Picks up a selection made elsewhere — the viewport, an undo, a freshly instantiated prefab —
	/// and unfolds this frame's tree down to it.
	/// </summary>
	private void BeginRevealRequest(World world, bool isSearchActive)
	{
		var searchCleared = _wasSearchActive && isSearchActive == false;
		_wasSearchActive = isSearchActive;

		if (EditorGui.ConsumeSelectionRevealRequest(out var entity) == false)
		{
			// Clearing a search goes back to the full tree, so unfold it to whatever was picked from the
			// results instead of leaving the user scrolled somewhere unrelated.
			if (searchCleared == false || EditorGui.HasSelectedEntity == false)
			{
				return;
			}

			entity = EditorGui.SelectedEntity;
		}

		if (world.IsAlive(entity) == false)
		{
			// The window is not drawn in every editor mode, so a request can outlive its entity.
			RevealAncestors.Clear();
			return;
		}

		_revealEntity = entity;
		EntityHierarchyReveal.CollectAncestors(world, entity, RevealAncestors);
	}

	private void EndRevealRequest()
	{
		_revealEntity = null;
		RevealAncestors.Clear();
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

		// The clicked row is already on screen under the cursor. Revealing it would only scroll it to
		// centre, moving the list out from under the click.
		EditorGui.DiscardSelectionRevealRequest();
	}

	private void CompleteDragDrop(EditorScene scene)
	{
		if (_pressedEntity is { } pressedEntity && _draggedEntity is null &&
		    ImGui.IsMouseDragging(ImGuiMouseButton.Left))
		{
			_draggedEntity = pressedEntity;
			_deferredSelectionEntity = null;
		}

		if (_draggedEntity is not { } draggedEntity)
		{
			if (ImGui.IsMouseDown(ImGuiMouseButton.Left) == false)
			{
				ApplyDeferredSelection(scene.World);
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
			CollectDraggedEntities(draggedEntity);
			EntityHierarchyEditorOperations.TryReparentEntities(
				scene,
				DraggedEntities,
				_hoveredEntity,
				_sceneSnapshotService,
				_undoRedoService,
				_interactionState);
			DraggedEntities.Clear();
		}

		_pressedEntity = null;
		_draggedEntity = null;
	}

	private void ApplyDeferredSelection(World world)
	{
		if (_deferredSelectionEntity is not { } entity)
		{
			return;
		}

		_deferredSelectionEntity = null;
		if (world.IsAlive(entity))
		{
			EditorGui.ReplaceEntitySelection(entity, world);
			EditorGui.DiscardSelectionRevealRequest();
		}
	}

	/// <summary>
	/// Dragging a selected row carries the whole selection, in hierarchy order so the moved entities
	/// keep their relative order under the new parent. Dragging an unselected row carries only that row.
	/// </summary>
	private static void CollectDraggedEntities(Entity draggedEntity)
	{
		DraggedEntities.Clear();
		if (EditorGui.SelectedEntities.Contains(draggedEntity) == false)
		{
			DraggedEntities.Add(draggedEntity);
			return;
		}

		foreach (var entity in VisibleEntities)
		{
			if (EditorGui.SelectedEntities.Contains(entity))
			{
				DraggedEntities.Add(entity);
			}
		}

		// Selected entities inside collapsed branches are not in the visible list.
		foreach (var entity in EditorGui.SelectedEntities)
		{
			if (DraggedEntities.Contains(entity) == false)
			{
				DraggedEntities.Add(entity);
			}
		}
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

	private static void DrawEntityLabelWithIcon(string label, nint iconTexture, float nodeCursorX, ImGuiCol textColor)
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

		drawList.AddText(textPosition, ImGui.GetColorU32(textColor), label);
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
			DrawCreateMenuItem(scene, EntityCreationPreset.Empty);
			ImGui.Separator();
			DrawCreateMenuItem(scene, EntityCreationPreset.PointLight);
			DrawCreateMenuItem(scene, EntityCreationPreset.DirectionalLight);
			ImGui.Separator();
			DrawCreateMenuItem(scene, EntityCreationPreset.Cube);
			DrawCreateMenuItem(scene, EntityCreationPreset.Sphere);

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

	private void DrawCreateMenuItem(EditorScene scene, EntityCreationPreset preset)
	{
		if (ImGui.MenuItem(EntityCreationOperations.GetDisplayName(preset)) == false)
		{
			return;
		}

		EntityCreationOperations.CreateEntity(
			scene,
			preset,
			GetNewEntityPosition(),
			_sceneSnapshotService,
			_undoRedoService,
			_interactionState);
	}

	private Vector3 GetNewEntityPosition()
	{
		if (_editorCameraSystem is null ||
		    _editorCameraSystem.TryGetCameraPose(out var position, out var forward) == false)
		{
			return Vector3.Zero;
		}

		return position + Vector3.Normalize(forward) * 5.0f;
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

		_interactionState.MarkSceneDirty(scene.World);
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

		_interactionState.MarkSceneDirty(scene.World);
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

			_interactionState.MarkSceneDirty(scene.World);
			return;
		}

		if (string.IsNullOrWhiteSpace(result.ErrorMessage) == false)
		{
			_notificationService.ReportError(result.ErrorMessage);
		}
	}
}

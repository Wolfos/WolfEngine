using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using WolfEngine.ECS;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor.UI;

public class EntitiesWindow: EditorWindow
{
	private static readonly List<Entity> AllEntities = new();
	private static readonly List<Entity> RootEntities = new();
	private static readonly List<Entity> PendingDeleteEntities = new();
	private static readonly Vector2 EntityIconSize = Vector2.One * 15.5f;
	private const string DeletePopupId = "EntitiesWindowDelete";
	private const string LocalItemContextMenuId = "EntitiesItemContextMenu";

	private readonly IIconManager _iconManager;
	private Entity? _pendingDeleteEntity;
	private bool _openDeletePopup;

	public EntitiesWindow(IIconManager iconManager)
	{
		_iconManager = iconManager;
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

		world.GetAllEntities(AllEntities);
		BuildRootList(world);

		var style = ImGui.GetStyle();
		ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(style.ItemSpacing.X, 0.0f));
		ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(style.FramePadding.X, 4.0f));

		foreach (var entity in RootEntities)
		{
			DrawEntityNode(entity, world, scene);
		}

		DrawContextMenu(scene);
		DrawDeletePopup(scene);
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

		var isSelected = EditorGui.HasSelectedEntity && EditorGui.SelectedEntity == entity;
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
			: "object";
		var iconTexture = ResolveIconTexture(_iconManager, iconName);

		var nameComponent = world.GetComponent<NameComponent>(entity);
		var name = nameComponent.Name ?? "Unnamed";
		var nodeCursorPosition = ImGui.GetCursorScreenPos();
		var open = ImGui.TreeNodeEx("##EntityNode", flags);
		var leftClicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
		var rightClicked = ImGui.IsItemClicked(ImGuiMouseButton.Right);
		DrawEntityLabelWithIcon(name, iconTexture, nodeCursorPosition.X);

		if (leftClicked)
		{
			EditorGui.SelectEntity(entity, world);
		}

		if (rightClicked)
		{
			EditorGui.SelectEntity(entity, world);
			ImGui.OpenPopup(LocalItemContextMenuId);
		}

		if (ImGui.BeginPopup(LocalItemContextMenuId))
		{
			DrawEntityContextMenu(entity, world);
			ImGui.EndPopup();
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

	private void DrawEntityContextMenu(Entity entity, World world)
	{
		if (ImGui.MenuItem("Delete"))
		{
			RequestDelete(entity, world);
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
	private static void DrawContextMenu(EditorScene scene)
	{
		if (ImGui.BeginPopupContextWindow("EntitiesContextMenu", ImGuiPopupFlags.MouseButtonRight) == false)
		{
			return;
		}

		if (ImGui.BeginMenu("Create"))
		{
			if (ImGui.MenuItem("Entity"))
			{
				var entity = scene.World.CreateEntity("Entity", Matrix4x4.Identity);
				EditorGui.SelectEntity(entity, scene.World);
			}

			ImGui.EndMenu();
		}

		ImGui.EndPopup();
	}

	private void DrawDeletePopup(EditorScene scene)
	{
		if (_openDeletePopup)
		{
			ImGui.OpenPopup(DeletePopupId);
			_openDeletePopup = false;
		}

		var isOpen = true;
		ImGui.SetNextWindowSize(new Vector2(420.0f, 0.0f), ImGuiCond.Appearing);
		if (ImGui.BeginPopupModal(DeletePopupId, ref isOpen, ImGuiWindowFlags.AlwaysAutoResize) == false)
		{
			return;
		}

		if (_pendingDeleteEntity.HasValue && scene.World.IsAlive(_pendingDeleteEntity.Value))
		{
			ImGui.TextWrapped(BuildDeleteConfirmationText(_pendingDeleteEntity.Value, scene.World));
			ImGui.Spacing();
			if (ImGui.Button("Delete", new Vector2(100.0f, 0.0f)))
			{
				ExecutePendingDelete(scene);
				ImGui.CloseCurrentPopup();
			}

			ImGui.SameLine();
			if (ImGui.Button("Cancel", new Vector2(100.0f, 0.0f)))
			{
				_pendingDeleteEntity = null;
				ImGui.CloseCurrentPopup();
			}
		}

		ImGui.EndPopup();
	}

	private void RequestDelete(Entity entity, World world)
	{
		if (world.IsAlive(entity) == false)
		{
			return;
		}

		_pendingDeleteEntity = entity;
		_openDeletePopup = true;
	}

	private static string BuildDeleteConfirmationText(Entity entity, World world)
	{
		var name = world.HasComponent<NameComponent>(entity)
			? world.GetComponent<NameComponent>(entity).Name ?? "Unnamed"
			: "Unnamed";
		return world.HasComponent<Children>(entity) && world.GetComponent<Children>(entity).First.IsValid
			? $"Delete '{name}' and all of its child entities?"
			: $"Delete '{name}'?";
	}

	private void ExecutePendingDelete(EditorScene scene)
	{
		if (_pendingDeleteEntity.HasValue == false || scene.World.IsAlive(_pendingDeleteEntity.Value) == false)
		{
			_pendingDeleteEntity = null;
			return;
		}

		PendingDeleteEntities.Clear();
		CollectEntitySubtree(_pendingDeleteEntity.Value, scene.World, PendingDeleteEntities);
		scene.World.DestroyEntity(_pendingDeleteEntity.Value);

		for (var i = 0; i < PendingDeleteEntities.Count; i++)
		{
			var entity = PendingDeleteEntities[i];
			scene.EntityIcons.Remove(entity);
			scene.EntityCellKeys.Remove(entity);
			scene.EntityIds.Remove(entity);
		}

		if (EditorGui.HasSelectedEntity && PendingDeleteEntities.Contains(EditorGui.SelectedEntity))
		{
			EditorGui.ClearEntitySelection();
		}

		PendingDeleteEntities.Clear();
		_pendingDeleteEntity = null;
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
}

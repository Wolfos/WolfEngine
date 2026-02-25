using System.Numerics;
using ImGuiNET;
using WolfEngine.ECS;

namespace WolfEngine.Editor.UI;

public class EntitiesWindow
{
	private static readonly List<Entity> AllEntities = new();
	private static readonly List<Entity> RootEntities = new();

	public static void Draw(World world)
	{
		ImGui.SetNextWindowPos(new Vector2(0.0f, 0.0f), ImGuiCond.FirstUseEver);
		ImGui.SetNextWindowSize(new Vector2(188.0f, 720.0f), ImGuiCond.FirstUseEver);

		ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0, 3.0f));
		ImGui.Begin("Entities");
		ImGui.PopStyleVar();
		
		world.GetAllEntities(AllEntities);

		BuildRootList(world);
		
		var style = ImGui.GetStyle();
		ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(style.ItemSpacing.X, 0.0f));
		ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(style.FramePadding.X, 4.0f));

		foreach (var entity in RootEntities)
		{
			DrawEntityNode(entity, world);
		}
		
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

	private static unsafe void DrawEntityNode(Entity entity, World world)
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

		var nameComponent = world.GetComponent<NameComponent>(entity);
		var name = nameComponent.Name ?? "Unnamed";
		var open = ImGui.TreeNodeEx(name, flags);

		if (ImGui.IsItemClicked())
		{
			SelectEntity(entity, world);
		}

		if (hasChildren && open)
		{
			var childEntity = world.GetComponent<Children>(entity).First;
			while (childEntity.IsValid)
			{
				DrawEntityNode(childEntity, world);

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
	
	private static void SelectEntity(Entity entity, World world)
	{
		EditorGui.HasSelectedEntity = true;
		EditorGui.SelectedEntity = entity;
		world.GetComponentTypes(entity, EditorGui.SelectedComponentTypes);
	}
}

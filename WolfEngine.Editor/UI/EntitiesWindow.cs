using ImGuiNET;
using WolfEngine.ECS;

namespace WolfEngine.Editor.UI;

public class EntitiesWindow
{
	private static readonly List<Entity> AllEntities = new();
	private static readonly List<Entity> RootEntities = new();

	public static void Draw(World world)
	{
		ImGui.SetNextWindowPos(new System.Numerics.Vector2(0.0f, 0.0f), ImGuiCond.FirstUseEver);
		ImGui.SetNextWindowSize(new System.Numerics.Vector2(188.0f, 720.0f), ImGuiCond.FirstUseEver);

		ImGui.Begin("Entities");
		world.GetAllEntities(AllEntities);

		BuildRootList(world);
		foreach (var entity in RootEntities)
		{
			DrawEntityNode(entity, world);
		}

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

	private static void DrawEntityNode(Entity entity, World world)
	{
		ImGui.PushID(entity.Index);

		var isSelected = EditorGui.HasSelectedEntity && EditorGui.SelectedEntity == entity;
		var hasChildren = world.HasComponent<Children>(entity)
		                 && world.GetComponent<Children>(entity).First.IsValid;
		var flags = ImGuiTreeNodeFlags.SpanFullWidth | ImGuiTreeNodeFlags.OpenOnArrow;
		if (!hasChildren)
		{
			flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;
		}

		if (isSelected)
		{
			unsafe
			{
				flags |= ImGuiTreeNodeFlags.Selected;
				var selectedColor = ImGui.GetStyleColorVec4(ImGuiCol.HeaderActive);
				ImGui.PushStyleColor(ImGuiCol.Header, *selectedColor);
				ImGui.PushStyleColor(ImGuiCol.HeaderHovered, *selectedColor);
				ImGui.PushStyleColor(ImGuiCol.HeaderActive, *selectedColor);
			}
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

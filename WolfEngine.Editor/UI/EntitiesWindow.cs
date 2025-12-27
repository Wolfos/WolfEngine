using ImGuiNET;
using WolfEngine.ECS;

namespace WolfEngine.Editor.UI;

public class EntitiesWindow
{
	private static readonly List<Entity> AllEntities = new();

	public static void Draw(World world)
	{
		ImGui.SetNextWindowPos(new System.Numerics.Vector2(0.0f, 0.0f), ImGuiCond.FirstUseEver);
		ImGui.SetNextWindowSize(new System.Numerics.Vector2(188.0f, 720.0f), ImGuiCond.FirstUseEver);

		ImGui.Begin("Entities");
		world.GetAllEntities(AllEntities);
        
		foreach (var entity in AllEntities)
		{
			var nameComponent = world.GetComponent<NameComponent>(entity);
			var name = nameComponent.Name ?? "Unnamed";
			if(ImGui.Selectable(name))
			{
				SelectEntity(entity, world);
			}
		}
        
		ImGui.End();
	}
	
	private static void SelectEntity(Entity entity, World world)
	{
		EditorGui.HasSelectedEntity = true;
		EditorGui.SelectedEntity = entity;
		world.GetComponentTypes(entity, EditorGui.SelectedComponentTypes);
	}
}
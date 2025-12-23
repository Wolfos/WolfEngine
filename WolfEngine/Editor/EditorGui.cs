using ImGuiNET;
using WolfEngine.ECS;

namespace WolfEngine.TestGame;

public static partial class EditorGui
{
    private static List<Entity> _allEntities = new();
    private static List<Type> _selectedComponentTypes = new();
    private static Entity _selectedEntity;
    private static bool _hasSelectedEntity = false;
    
    public static void Draw(World world)
    {
        DockSpace();

        ImGui.SetNextWindowPos(new System.Numerics.Vector2(0.0f, 0.0f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(188.0f, 720.0f), ImGuiCond.FirstUseEver);
        ImGui.Begin("Entities");
        world.GetAllEntities(_allEntities);
        
        foreach (var entity in _allEntities)
        {
            var nameComponent = world.GetComponent<NameComponent>(entity);
            var name = nameComponent.Name ?? "Unnamed";
            if(ImGui.Selectable(name))
            {
                SelectEntity(entity, world);
            }
        }
        
        ImGui.End();


        ImGui.SetNextWindowPos(new System.Numerics.Vector2(1041.0f, 0.0f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(239.0f, 720.0f), ImGuiCond.FirstUseEver);
        ImGui.Begin("Components");
        if (_hasSelectedEntity)
        {
            foreach (var componentType in _selectedComponentTypes)
            {
                DrawComponentEditor(world, _selectedEntity, componentType);
            }
        }
        ImGui.End();
    }

    private static void SelectEntity(Entity entity, World world)
    {
        _hasSelectedEntity = true;
        _selectedEntity = entity;
        world.GetComponentTypes(entity, _selectedComponentTypes);
    }

    private static void DockSpace()
    {
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.WorkPos);
        ImGui.SetNextWindowSize(viewport.WorkSize);
        ImGui.SetNextWindowViewport(viewport.ID);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, System.Numerics.Vector2.Zero);

        var flags = ImGuiWindowFlags.NoDocking
                    | ImGuiWindowFlags.NoTitleBar
                    | ImGuiWindowFlags.NoCollapse
                    | ImGuiWindowFlags.NoResize
                    | ImGuiWindowFlags.NoMove
                    | ImGuiWindowFlags.NoBringToFrontOnFocus
                    | ImGuiWindowFlags.NoNavFocus
                    | ImGuiWindowFlags.NoBackground;

        ImGui.Begin("DockSpace", flags);
        ImGui.PopStyleVar(3);

        ImGui.DockSpace(ImGui.GetID("MainDockSpace"), System.Numerics.Vector2.Zero, ImGuiDockNodeFlags.PassthruCentralNode);
        ImGui.End();
    }
}

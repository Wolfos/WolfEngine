using ImGuiNET;
using WolfEngine.ECS;

namespace WolfEngine.Editor.UI;

public class EditorGui
{
    // TODO: Maybe not public?
    public static readonly List<Type> SelectedComponentTypes = new();
    public static Entity SelectedEntity;
    public static bool HasSelectedEntity = false;

    private readonly IComponentEditor _componentEditor;
    private readonly IMenuBar _menuBar;

    public EditorGui(IComponentEditor componentEditor, IMenuBar menuBar)
    {
        _componentEditor = componentEditor;
        _menuBar = menuBar;
    }
    
    public void Draw(World world)
    {
        DockSpace();
        
        _menuBar.Draw(world);
        
        EntitiesWindow.Draw(world);
        
        ImGui.SetNextWindowPos(new System.Numerics.Vector2(1041.0f, 0.0f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(239.0f, 720.0f), ImGuiCond.FirstUseEver);
        ImGui.Begin("Components");
        if (HasSelectedEntity)
        {
            foreach (var componentType in SelectedComponentTypes)
            {
                _componentEditor.Draw(world, SelectedEntity, componentType);
            }
        }
        ImGui.End();
        
        EditorPreferencesMenu.Draw();

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

        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDocking
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

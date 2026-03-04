using System.Numerics;
using ImGuiNET;
using WolfEngine.ECS;
using WolfEngine.Mathematics;
using WolfEngine.Profiling;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor.UI;

public class EditorGui
{
    // TODO: Maybe not public?
    public static readonly List<Type> SelectedComponentTypes = new();
    public static Entity SelectedEntity;
    public static bool HasSelectedEntity = false;

    private readonly IComponentEditor _componentEditor;
    private readonly IIconManager _icons;
    private readonly IMenuBar _menuBar;
    private readonly IRenderer _renderer;

    public EditorGui(
        IComponentEditor componentEditor,
        IMenuBar menuBar,
        IRenderer renderer,
        EditorViewportStateBus viewportStateBus,
        IIconManager icons,
        TransformGizmoController transformGizmoController)
    {
        _componentEditor = componentEditor;
        _menuBar = menuBar;
        _renderer = renderer;
        _icons = icons;
        SceneWindow.Init(viewportStateBus, icons, transformGizmoController); 
    }
    
    public void Draw(EditorScene scene)
    {
        DockSpace();

        using (FrameProfiler.Instance.Measure("Menu Bar"))
        {
            _menuBar.Draw(scene);
        }

        using (FrameProfiler.Instance.Measure("Entities Window"))
        {
            EntitiesWindow.Draw(scene, _icons);
        }

        using (FrameProfiler.Instance.Measure("Scene Window"))
        {
            SceneWindow.Draw(scene);
        }

        using (FrameProfiler.Instance.Measure("Components Window"))
        {
            ImGui.SetNextWindowPos(new Vector2(1041.0f, 0.0f), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSize(new Vector2(239.0f, 720.0f), ImGuiCond.FirstUseEver);
            var pushedBoldTitle = ImGuiUiSystem.PushBoldFont();
            ImGui.Begin("Components");
            var pushedRegularContent = ImGuiUiSystem.PushRegularFont();
            if (HasSelectedEntity)
            {
                foreach (var componentType in SelectedComponentTypes)
                {
                    _componentEditor.Draw(scene, SelectedEntity, componentType);
                }
            }
            ImGuiUiSystem.PopFontIfPushed(pushedRegularContent);
            ImGui.End();
            ImGuiUiSystem.PopFontIfPushed(pushedBoldTitle);
        }

        using (FrameProfiler.Instance.Measure("Preferences"))
        {
            EditorPreferencesMenu.Draw();
        }

        using (FrameProfiler.Instance.Measure("Profiler"))
        {
            ProfilerWindow.Draw(_renderer);
        }
    }
    
    private static void DockSpace()
    {
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.WorkPos);
        ImGui.SetNextWindowSize(viewport.WorkSize);
        ImGui.SetNextWindowViewport(viewport.ID);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);

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

        ImGui.DockSpace(ImGui.GetID("MainDockSpace"), Vector2.Zero, ImGuiDockNodeFlags.PassthruCentralNode);
        ImGui.End();
    }
}

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
    private readonly IMenuBar _menuBar;
    private readonly IRenderer _renderer;
    private readonly EditorViewportStateBus _viewportStateBus;
    private float _sceneViewportScale;

    public EditorGui(
        IComponentEditor componentEditor,
        IMenuBar menuBar,
        IRenderer renderer,
        EditorViewportStateBus viewportStateBus)
    {
        _componentEditor = componentEditor;
        _menuBar = menuBar;
        _renderer = renderer;
        _viewportStateBus = viewportStateBus ?? throw new ArgumentNullException(nameof(viewportStateBus));
        _sceneViewportScale = Math.Clamp(EditorPreferences.GetSceneViewportResolutionScale(), 0.5f, 1.0f);
    }
    
    public void Draw(World world)
    {
        DockSpace();

        using (FrameProfiler.Instance.Measure("Menu Bar"))
        {
            _menuBar.Draw(world);
        }

        using (FrameProfiler.Instance.Measure("Entities Window"))
        {
            EntitiesWindow.Draw(world);
        }

        using (FrameProfiler.Instance.Measure("Scene Window"))
        {
            DrawSceneWindow();
        }

        using (FrameProfiler.Instance.Measure("Components Window"))
        {
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

    private void DrawSceneWindow()
    {
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(800.0f, 520.0f), ImGuiCond.FirstUseEver);
        ImGui.Begin("Scene");

        var scale = _sceneViewportScale;
        if (ImGui.SliderFloat("Resolution Scale", ref scale, 0.5f, 1.0f, "%.2fx"))
        {
            var snapped = (float)Math.Round(scale / 0.05f) * 0.05f;
            _sceneViewportScale = Math.Clamp(snapped, 0.5f, 1.0f);
            EditorPreferences.SetSceneViewportResolutionScale(_sceneViewportScale);
        }

        var contentSize = ImGui.GetContentRegionAvail();
        var io = ImGui.GetIO();
        var contentPixels = new Int2(
            Math.Max(0, (int)MathF.Round(contentSize.X * io.DisplayFramebufferScale.X)),
            Math.Max(0, (int)MathF.Round(contentSize.Y * io.DisplayFramebufferScale.Y)));

        var visible = ImGui.IsWindowCollapsed() == false && contentPixels.X > 0 && contentPixels.Y > 0;
        var hovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        var focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.ChildWindows);

        if (OperatingSystem.IsMacOS())
        {
            var renderState = _viewportStateBus.GetRenderState();
            if (renderState.TextureId != 0 && contentSize.X > 0.0f && contentSize.Y > 0.0f)
            {
                ImGui.Image(renderState.TextureId, contentSize);
            }
            else
            {
                ImGui.TextUnformatted("Scene render target unavailable.");
            }
        }
        else
        {
            ImGui.TextUnformatted("Scene viewport preview is Metal-only in this build.");
        }

        _viewportStateBus.PublishUiState(new SceneViewportUiState(
            visible,
            contentPixels,
            _sceneViewportScale,
            hovered,
            focused));

        ImGui.End();
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

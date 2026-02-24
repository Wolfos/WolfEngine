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
    private readonly IMenuBar _menuBar;
    private readonly IRenderer _renderer;
    private readonly EditorViewportStateBus _viewportStateBus;
    private readonly IIconManager _icons;
    private readonly TransformGizmoController _transformGizmoController;
    private float _sceneViewportScale;
    private TransformGizmoMode _gizmoMode = TransformGizmoMode.Translate;
    private TransformSpace _transformSpace = TransformSpace.Local;

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
        _viewportStateBus = viewportStateBus ?? throw new ArgumentNullException(nameof(viewportStateBus));
        _icons = icons;
        _transformGizmoController = transformGizmoController ?? throw new ArgumentNullException(nameof(transformGizmoController));
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
            DrawSceneWindow(world);
        }

        using (FrameProfiler.Instance.Measure("Components Window"))
        {
            ImGui.SetNextWindowPos(new Vector2(1041.0f, 0.0f), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSize(new Vector2(239.0f, 720.0f), ImGuiCond.FirstUseEver);
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

    private void DrawSceneWindow(World world)
    {
        ImGui.SetNextWindowSize(new Vector2(800.0f, 520.0f), ImGuiCond.FirstUseEver);
        ImGui.Begin("Scene");
        
        if (ImGui.ImageButton("Translate", _icons.Get("translate"), Vector2.One * 15))
        {
            _gizmoMode = TransformGizmoMode.Translate;
        }
        ImGui.SameLine();
        if (ImGui.ImageButton("Rotate", _icons.Get("rotate"), Vector2.One * 15))
        {
            _gizmoMode = TransformGizmoMode.Rotate;
        }
        ImGui.SameLine();
        if (ImGui.ImageButton("Scale", _icons.Get("scale"), Vector2.One * 15))
        {
            _gizmoMode = TransformGizmoMode.Scale;
        }

        ImGui.SameLine();
        if (ImGui.RadioButton("Local", _transformSpace == TransformSpace.Local))
        {
            _transformSpace = TransformSpace.Local;
        }

        ImGui.SameLine();
        if (ImGui.RadioButton("World", _transformSpace == TransformSpace.World))
        {
            _transformSpace = TransformSpace.World;
        }
        
        ImGui.SameLine();
        var scale = _sceneViewportScale;
        ImGui.SetNextItemWidth(100);
        if (ImGui.SliderFloat("Resolution", ref scale, 0.5f, 1.0f, "%.2fx"))
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
        if ((hovered || focused) && io.WantTextInput == false)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.W))
            {
                _gizmoMode = TransformGizmoMode.Translate;
            }
            else if (ImGui.IsKeyPressed(ImGuiKey.E))
            {
                _gizmoMode = TransformGizmoMode.Rotate;
            }
            else if (ImGui.IsKeyPressed(ImGuiKey.R))
            {
                _gizmoMode = TransformGizmoMode.Scale;
            }
        }

        var renderState = _viewportStateBus.GetRenderState();
        var imageMin = ImGui.GetCursorScreenPos();
        var imageMax = imageMin + contentSize;
        if (renderState.TextureId != 0 && contentSize.X > 0.0f && contentSize.Y > 0.0f)
        {
            ImGui.Image(renderState.TextureId, contentSize);
        }
        else
        {
            ImGui.Dummy(contentSize);
            var drawList = ImGui.GetWindowDrawList();
            drawList.AddText(
                imageMin + new Vector2(10.0f, 10.0f),
                ImGui.ColorConvertFloat4ToU32(new Vector4(0.9f, 0.9f, 0.9f, 1.0f)),
                "Scene render target unavailable.");
        }

        _viewportStateBus.PublishUiState(new SceneViewportUiState(
            visible,
            contentPixels,
            _sceneViewportScale,
            hovered,
            focused,
            imageMin,
            imageMax));

        _transformGizmoController.DrawAndHandle(world, SelectedEntity, HasSelectedEntity, _gizmoMode, _transformSpace);

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

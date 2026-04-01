using System;
using System.Numerics;
using ImGuiNET;
using WolfEngine.Mathematics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor.UI;

public class SceneWindow: EditorWindow
{
    private static readonly SceneDebugViewOption[] FallbackDebugViews =
    [
        new SceneDebugViewOption(SceneDebugViewIds.FinalColor, "Final Color", SceneDebugViewKind.Color)
    ];

    private readonly EditorViewportStateBus _viewportStateBus;
    private readonly IIconManager _icons;
    private readonly TransformGizmoController _transformGizmoController;
    private float _sceneViewportScale;
    private string _selectedDebugViewId = SceneDebugViewIds.FinalColor;
    private bool _rightMousePressStartedHere;
    
    private TransformGizmoMode _gizmoMode = TransformGizmoMode.Translate;
    private TransformSpace _transformSpace = TransformSpace.Local;
    private TransformPivotMode _pivotMode = TransformPivotMode.Center;


    public SceneWindow(EditorViewportStateBus viewportStateBus, IIconManager icons, TransformGizmoController transformGizmoController)
    {
        _viewportStateBus = viewportStateBus;
        _icons = icons;
        _transformGizmoController = transformGizmoController;
        
        _sceneViewportScale = Math.Clamp(EditorPreferences.GetSceneViewportResolutionScale(), 0.5f, 1.0f);
    }

    public override string Name => "Scene";

    public override void Draw(EditorScene scene)
    {
        var world = scene.World;
        
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0, 3.0f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0);
        ImGui.SetNextWindowSize(new Vector2(800.0f, 520.0f), ImGuiCond.FirstUseEver);
        Begin();
        ImGui.PopStyleVar(2);

        ImGui.SetCursorPosX(3);
        if (DrawTransformModeButton("Translate", "translate", TransformGizmoMode.Translate))
        {
            _gizmoMode = TransformGizmoMode.Translate;
        }
        ImGui.SameLine();
        if (DrawTransformModeButton("Rotate", "rotate", TransformGizmoMode.Rotate))
        {
            _gizmoMode = TransformGizmoMode.Rotate;
        }
        ImGui.SameLine();
        if (DrawTransformModeButton("Scale", "scale", TransformGizmoMode.Scale))
        {
            _gizmoMode = TransformGizmoMode.Scale;
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(80.0f);
        if (ImGui.BeginCombo("##Space", _transformSpace == TransformSpace.Local ? "Local" : "World"))
        {
            if (ImGui.Selectable("Local", _transformSpace == TransformSpace.Local))
            {
                _transformSpace = TransformSpace.Local;
            }

            if (ImGui.Selectable("World", _transformSpace == TransformSpace.World))
            {
                _transformSpace = TransformSpace.World;
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(80.0f);
        var pivotLabel = _pivotMode == TransformPivotMode.Center ? "Center" : "Pivot";
        if (ImGui.BeginCombo("##Pivot", pivotLabel))
        {
            if (ImGui.Selectable("Center", _pivotMode == TransformPivotMode.Center))
            {
                _pivotMode = TransformPivotMode.Center;
            }

            if (ImGui.Selectable("Pivot", _pivotMode == TransformPivotMode.TransformPivot))
            {
                _pivotMode = TransformPivotMode.TransformPivot;
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        var renderState = _viewportStateBus.GetRenderState();
        var debugViews = renderState.DebugViews.Length > 0 ? renderState.DebugViews : FallbackDebugViews;
        ImGui.SetNextItemWidth(150.0f);
        if (ImGui.BeginCombo("##DebugView", GetDebugViewLabel(debugViews, renderState.ActiveDebugViewId)))
        {
            for (var i = 0; i < debugViews.Length; i++)
            {
                var debugView = debugViews[i];
                var selected = string.Equals(_selectedDebugViewId, debugView.Id, StringComparison.Ordinal);
                if (ImGui.Selectable(debugView.Label, selected))
                {
                    _selectedDebugViewId = debugView.Id;
                }

                if (selected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        const float resolutionSliderWidth = 100.0f;
        var resolutionSliderX = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - resolutionSliderWidth - 3;
        if (resolutionSliderX > ImGui.GetCursorPosX())
        {
            ImGui.SetCursorPosX(resolutionSliderX);
        }

        var scale = _sceneViewportScale;
        ImGui.SetNextItemWidth(resolutionSliderWidth);
        if (ImGui.SliderFloat("##Resolution", ref scale, 0.5f, 1.0f, "%.2fx"))
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
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            _rightMousePressStartedHere = hovered;
        }
        else if (ImGui.IsMouseDown(ImGuiMouseButton.Right) == false)
        {
            _rightMousePressStartedHere = false;
        }

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

        var imageMin = ImGui.GetCursorScreenPos();
        var imageMax = imageMin + contentSize;
        if (renderState.TextureId != 0 && contentSize.X > 0.0f && contentSize.Y > 0.0f)
        {
            ImGui.Image(UiTextureIds.SceneViewport, contentSize);
        }
        else
        {
            ImGui.Dummy(contentSize);
            var drawList = ImGui.GetWindowDrawList();
            drawList.AddText(
                imageMin + new Vector2(10.0f, 10.0f),
                ImGui.ColorConvertFloat4ToU32(new ColorRGBA(0.9f, 0.9f, 0.9f, 1.0f)),
                "Scene render target unavailable.");
        }

        _viewportStateBus.PublishUiState(new SceneViewportUiState(
            visible,
            contentPixels,
            _sceneViewportScale,
            _selectedDebugViewId,
            hovered,
            focused,
            _rightMousePressStartedHere,
            imageMin,
            imageMax));

        _transformGizmoController.DrawAndHandle(
            world,
            EditorGui.SelectedEntity,
            EditorGui.HasSelectedEntity,
            _gizmoMode,
            _transformSpace,
            _pivotMode);

        ImGui.End();
    }

    private bool DrawTransformModeButton(string buttonId, string iconName, TransformGizmoMode mode)
    {
        var isSelected = _gizmoMode == mode;
        if (isSelected)
        {
            var selectedColor = ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive];
            ImGui.PushStyleColor(ImGuiCol.Button, selectedColor);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, selectedColor);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, selectedColor);
        }

        var clicked = ImGui.ImageButton(buttonId, _icons.Get(iconName), Vector2.One * 15.5f);
        if (isSelected)
        {
            ImGui.PopStyleColor(3);
        }

        return clicked;
    }

    private string GetDebugViewLabel(SceneDebugViewOption[] debugViews, string activeDebugViewId)
    {
        for (var i = 0; i < debugViews.Length; i++)
        {
            if (string.Equals(debugViews[i].Id, _selectedDebugViewId, StringComparison.Ordinal))
            {
                return debugViews[i].Label;
            }
        }

        for (var i = 0; i < debugViews.Length; i++)
        {
            if (string.Equals(debugViews[i].Id, activeDebugViewId, StringComparison.Ordinal))
            {
                return debugViews[i].Label;
            }
        }

        return "Final Color";
    }
}

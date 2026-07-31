using System;
using System.Numerics;
using ImGuiNET;
using WolfEngine.ECS;
using WolfEngine.Mathematics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor.UI;

public class SceneWindow: EditorWindow
{
    private const float ToolbarIconSize = 15.5f;
    private static readonly SceneDebugViewOption[] FallbackDebugViews =
    [
        new SceneDebugViewOption(SceneDebugViewIds.FinalColor, "Final Color", SceneDebugViewKind.Color)
    ];

    private readonly EditorViewportStateBus _viewportStateBus;
    private readonly IWorldManager _worldManager;
    private readonly IEditorPlaySession _playSession;
    private readonly IGizmoLineRenderer _gizmoLineRenderer;
    private readonly IIconManager _icons;
    private readonly TerrainToolSettingsOverlay _terrainToolSettingsOverlay;
    private readonly TerrainToolController _terrainToolController;
    private readonly TransformGizmoController _transformGizmoController;
    private float _sceneViewportScale;
    private string _selectedDebugViewId = SceneDebugViewIds.FinalColor;
    private bool _rightMousePressStartedHere;
    private SceneToolMode _sceneToolMode = SceneToolMode.Transform;
    private TerrainTool _terrainTool = TerrainTool.RaiseLower;
    private TransformGizmoMode _gizmoMode = TransformGizmoMode.Translate;
    private TransformSpace _transformSpace = TransformSpace.Local;
    private TransformPivotMode _pivotMode = TransformPivotMode.Center;


    public SceneWindow(
        EditorViewportStateBus viewportStateBus,
        IWorldManager worldManager,
        IEditorPlaySession playSession,
        IGizmoLineRenderer gizmoLineRenderer,
        IIconManager icons,
        TerrainToolSettingsOverlay terrainToolSettingsOverlay,
        TerrainToolController terrainToolController,
        TransformGizmoController transformGizmoController)
    {
        _viewportStateBus = viewportStateBus;
        _worldManager = worldManager;
        _playSession = playSession;
        _gizmoLineRenderer = gizmoLineRenderer;
        _icons = icons;
        _terrainToolSettingsOverlay = terrainToolSettingsOverlay;
        _terrainToolController = terrainToolController;
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
            SelectTransformMode(TransformGizmoMode.Translate);
        }
        ImGui.SameLine();
        if (DrawTransformModeButton("Rotate", "rotate", TransformGizmoMode.Rotate))
        {
            SelectTransformMode(TransformGizmoMode.Rotate);
        }
        ImGui.SameLine();
        if (DrawTransformModeButton("Scale", "scale", TransformGizmoMode.Scale))
        {
            SelectTransformMode(TransformGizmoMode.Scale);
        }
        ImGui.SameLine();
        if (DrawToolbarButton("Terrain", "terrain", _sceneToolMode == SceneToolMode.Terrain))
        {
            _sceneToolMode = SceneToolMode.Terrain;
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

        var io = ImGui.GetIO();
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

        var primaryModifierDown = ImGui.IsKeyDown(ImGuiKey.LeftCtrl)
                                  || ImGui.IsKeyDown(ImGuiKey.RightCtrl)
                                  || ImGui.IsKeyDown(ImGuiKey.LeftSuper)
                                  || ImGui.IsKeyDown(ImGuiKey.RightSuper);

        if (_rightMousePressStartedHere == false)
        {
            ApplyShortcut(SceneShortcutResolver.Resolve(new SceneShortcutSnapshot(
                hovered || focused,
                primaryModifierDown,
                ImGui.IsKeyPressed(ImGuiKey.W),
                ImGui.IsKeyPressed(ImGuiKey.E),
                ImGui.IsKeyPressed(ImGuiKey.R),
                ImGui.IsKeyPressed(ImGuiKey.T),
                ImGui.IsKeyPressed(ImGuiKey._1),
                ImGui.IsKeyPressed(ImGuiKey._2),
                ImGui.IsKeyPressed(ImGuiKey._3),
                ImGui.IsKeyPressed(ImGuiKey._4),
                ImGui.IsKeyPressed(ImGuiKey._5),
                ImGui.IsKeyPressed(ImGuiKey._6),
                io.WantTextInput,
                _sceneToolMode)));
        }

        if (_sceneToolMode == SceneToolMode.Terrain)
        {
            ImGui.Separator();
            DrawTerrainToolbar();
        }

        var imageMin = ImGui.GetCursorScreenPos();
        var contentSize = ImGui.GetContentRegionAvail();
        var contentPixels = new Int2(
            Math.Max(0, (int)MathF.Round(contentSize.X * io.DisplayFramebufferScale.X)),
            Math.Max(0, (int)MathF.Round(contentSize.Y * io.DisplayFramebufferScale.Y)));
        var visible = ImGui.IsWindowCollapsed() == false && contentPixels.X > 0 && contentPixels.Y > 0;
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

        var pointerAvailable = false;
        var pointerCaptured = false;
        if (contentSize.X > 0.0f && contentSize.Y > 0.0f)
        {
            ImGui.SetCursorScreenPos(imageMin);
            ImGui.InvisibleButton(
                "##SceneViewportInput",
                contentSize,
                ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
            pointerAvailable = ImGui.IsItemHovered();
            pointerCaptured = ImGui.IsItemActive();
        }

        if (_sceneToolMode == SceneToolMode.Terrain)
        {
            TerrainLayerSet? layerSet = null;
            if (EditorGui.HasSelectedEntity &&
                world.IsAlive(EditorGui.SelectedEntity) &&
                world.HasComponent<TerrainComponent>(EditorGui.SelectedEntity))
            {
                layerSet = world.GetComponent<TerrainComponent>(EditorGui.SelectedEntity).LayerSetAsset.Asset;
            }

            _terrainToolSettingsOverlay.Draw(_terrainTool, _terrainToolController.Settings, layerSet, imageMin, imageMax);
        }

        _viewportStateBus.PublishUiState(new SceneViewportUiState(
            visible,
            contentPixels,
            _sceneViewportScale,
            _selectedDebugViewId,
            hovered,
            focused,
            pointerAvailable,
            pointerCaptured,
            _rightMousePressStartedHere,
            imageMin,
            imageMax));

        _gizmoLineRenderer.BeginFrame();
        if (ShouldDrawGizmos(_playSession.State))
        {
            _worldManager.OnDrawGizmos(WorldTag.Authoring);

            switch (_sceneToolMode)
            {
                case SceneToolMode.Transform:
                    _terrainToolController.ClearPreview();
                    _transformGizmoController.DrawAndHandle(
                        scene,
                        world,
                        EditorGui.SelectedEntities,
                        _gizmoMode,
                        _transformSpace,
                        _pivotMode);
                    break;
                case SceneToolMode.Terrain:
                    _terrainToolController.DrawAndHandle(scene, _terrainTool);
                    break;
                default:
                    _terrainToolController.ClearPreview();
                    break;
            }
        }

        ImGui.End();
    }

    internal static bool ShouldDrawGizmos(EditorPlayState playState)
    {
        return playState is EditorPlayState.Edit or EditorPlayState.Paused;
    }

    private void ApplyShortcut(SceneShortcutCommand command)
    {
        switch (command)
        {
            case SceneShortcutCommand.SelectTranslate:
                SelectTransformMode(TransformGizmoMode.Translate);
                break;
            case SceneShortcutCommand.SelectRotate:
                SelectTransformMode(TransformGizmoMode.Rotate);
                break;
            case SceneShortcutCommand.SelectScale:
                SelectTransformMode(TransformGizmoMode.Scale);
                break;
            case SceneShortcutCommand.SelectTerrainMode:
                _sceneToolMode = SceneToolMode.Terrain;
                break;
            case SceneShortcutCommand.SelectRaiseLower:
                _terrainTool = TerrainTool.RaiseLower;
                break;
            case SceneShortcutCommand.SelectFlatten:
                _terrainTool = TerrainTool.Flatten;
                break;
            case SceneShortcutCommand.SelectSmooth:
                _terrainTool = TerrainTool.Smooth;
                break;
            case SceneShortcutCommand.SelectBrush:
                _terrainTool = TerrainTool.Brush;
                break;
            case SceneShortcutCommand.SelectEyedropper:
                _terrainTool = TerrainTool.Eyedropper;
                break;
            case SceneShortcutCommand.SelectPen:
                _terrainTool = TerrainTool.Pen;
                break;
        }
    }

    private void DrawTerrainToolbar()
    {
        ImGui.SetCursorPosX(3);
        DrawTerrainToolButton("RaiseLower", "raiselower", TerrainTool.RaiseLower);
        ImGui.SameLine();
        DrawTerrainToolButton("Flatten", "flatten", TerrainTool.Flatten);
        ImGui.SameLine();
        DrawTerrainToolButton("Smooth", "smooth", TerrainTool.Smooth);
        ImGui.SameLine();
        DrawTerrainToolButton("Brush", "brush", TerrainTool.Brush);
        ImGui.SameLine();
        DrawTerrainToolButton("Eyedropper", "eyedropper", TerrainTool.Eyedropper);
        ImGui.SameLine();
        DrawTerrainToolButton("Pen", "pen", TerrainTool.Pen);
    }

    private bool DrawTerrainToolButton(string buttonId, string iconName, TerrainTool tool)
    {
        var clicked = DrawToolbarButton(buttonId, iconName, _terrainTool == tool);
        if (clicked)
        {
            _terrainTool = tool;
        }

        return clicked;
    }

    private bool DrawToolbarButton(string buttonId, string iconName, bool isSelected)
    {
        if (isSelected)
        {
            var selectedColor = ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive];
            ImGui.PushStyleColor(ImGuiCol.Button, selectedColor);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, selectedColor);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, selectedColor);
        }

        var clicked = ImGui.ImageButton(buttonId, _icons.Get(iconName), Vector2.One * ToolbarIconSize);
        if (isSelected)
        {
            ImGui.PopStyleColor(3);
        }

        return clicked;
    }

    private void SelectTransformMode(TransformGizmoMode mode)
    {
        _sceneToolMode = SceneToolMode.Transform;
        _gizmoMode = mode;
    }

    private bool DrawTransformModeButton(string buttonId, string iconName, TransformGizmoMode mode)
    {
        return DrawToolbarButton(buttonId, iconName, _sceneToolMode == SceneToolMode.Transform && _gizmoMode == mode);
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

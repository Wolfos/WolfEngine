using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ImGuiNET;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Editor.Projects;
using WolfEngine.Rendering;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor.UI;

public interface IComponentEditor
{
    void DrawEntityControls(EditorScene scene, Entity entity);
    void Draw(EditorScene scene, Entity entity, Type componentType);
    void DrawAddComponentControls(EditorScene scene, Entity entity);
}

public class ComponentsWindow : EditorWindow, IComponentEditor
{
    private const string AddComponentPopupId = "AddComponentPopup";
    private static readonly MethodInfo DrawComponentEditorGenericMethod = typeof(ComponentsWindow).GetMethod(
        nameof(DrawComponentEditorGeneric),
        BindingFlags.NonPublic | BindingFlags.Instance)!;

    private readonly IIconManager _icons;
    private readonly IPropertyDrawerRegistry _propertyDrawerRegistry;
    private readonly IEditorProjectService _projectService;
    private readonly IProjectTypeCatalog _projectTypeCatalog;
    private readonly RenderGraph _renderGraph;
    private readonly IEditorInteractionState _interactionState;
    private readonly IEditorSceneSnapshotService _sceneSnapshotService;
    private readonly IEditorUndoRedoService _undoRedoService;
    private readonly List<ProjectTypeDescriptor> _addableComponentTypes = new();
    private readonly List<Type> _existingComponentTypes = new();
    private readonly Dictionary<string, int> _componentNameCounts = new(StringComparer.Ordinal);
    private Type? _pendingRemovedComponentType;
    private static readonly Vector2 EntityIconSize = Vector2.One * 15.5f;
    private static readonly Vector2 PickerIconSize = Vector2.One * 22.0f;
    private static readonly Vector2 ComponentActionIconSize = Vector2.One * 15.5f;

    public ComponentsWindow(
        IIconManager icons,
        IPropertyDrawerRegistry propertyDrawerRegistry,
        IEditorProjectService projectService,
        IProjectTypeCatalog projectTypeCatalog,
        RenderGraph renderGraph,
        IEditorInteractionState interactionState,
        IEditorSceneSnapshotService sceneSnapshotService,
        IEditorUndoRedoService undoRedoService)
    {
        _icons = icons;
        _propertyDrawerRegistry = propertyDrawerRegistry;
        _projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
        _projectTypeCatalog = projectTypeCatalog;
        _renderGraph = renderGraph;
        _interactionState = interactionState;
        _sceneSnapshotService = sceneSnapshotService;
        _undoRedoService = undoRedoService;
    }

    public override string Name => "Components";

    public void ResetCachedTypes()
    {
        _addableComponentTypes.Clear();
        _existingComponentTypes.Clear();
        _componentNameCounts.Clear();
    }

    public override void Draw(EditorScene scene)
    {
        ImGui.SetNextWindowPos(new Vector2(1041.0f, 0.0f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(239.0f, 720.0f), ImGuiCond.FirstUseEver);
        if (EditorGui.HasSelectedEntity && EditorGui.ConsumeComponentsWindowFocusRequest())
        {
            ImGui.SetNextWindowFocus();
        }

		var pushedBoldTitle = ImGuiUiSystem.PushBoldFont();
		ImGui.Begin(Name);
		FocusOnRightClickStart();
		var pushedRegularContent = ImGuiUiSystem.PushRegularFont();
		if (EditorGui.HasSelectedEntity)
		{
            _pendingRemovedComponentType = null;
            DrawEntityControls(scene, EditorGui.SelectedEntity);
            foreach (var componentType in EditorGui.SelectedComponentTypes)
            {
                Draw(scene, EditorGui.SelectedEntity, componentType);
                if (_pendingRemovedComponentType is not null)
                {
                    break;
                }
            }

            if (_pendingRemovedComponentType is not null)
            {
                RemoveComponent(scene, EditorGui.SelectedEntity, _pendingRemovedComponentType);
                _pendingRemovedComponentType = null;
            }

            ImGui.Separator();
            DrawAddComponentControls(scene, EditorGui.SelectedEntity);
        }
        else
        {
            ImGui.TextUnformatted("No entity selected.");
        }

        ImGuiUiSystem.PopFontIfPushed(pushedRegularContent);
        ImGui.End();
        ImGuiUiSystem.PopFontIfPushed(pushedBoldTitle);
    }

    public void DrawEntityControls(EditorScene scene, Entity entity)
    {
        var isEnabled = scene.World.IsEnabled(entity);
        if (EditorUIUtility.Checkbox("Enabled", ref isEnabled))
        {
            scene.World.SetEnabled(entity, isEnabled);
            _interactionState.MarkSceneDirty();
        }

        DrawPrefabControls(scene, entity);
        ImGui.Separator();
    }

    public void Draw(EditorScene scene, Entity entity, Type componentType)
    {
        if (Attribute.IsDefined(componentType, typeof(ExcludeFromEditorAttribute)))
            return;

        if (typeof(IEntityComponent).IsAssignableFrom(componentType) == false)
            return;

        if (componentType == typeof(NameComponent) ||
            componentType == typeof(LocalTransform) ||
            componentType == typeof(MeshRenderer) ||
            componentType == typeof(Light))
        {
            DrawComponentEditorGenericMethod.MakeGenericMethod(componentType)
                .Invoke(this, new object[] { scene, entity, _icons, _propertyDrawerRegistry, _renderGraph, _interactionState });
            return;
        }

        DrawGenericComponentEditor(scene, scene.World, entity, componentType, _propertyDrawerRegistry, _interactionState);
    }

    public void DrawAddComponentControls(EditorScene scene, Entity entity)
    {
        PopulateAddableComponentTypes(scene.World, entity);

        var hasAddableComponents = _addableComponentTypes.Count > 0;
        if (hasAddableComponents == false)
        {
            ImGui.BeginDisabled();
        }

        var buttonLabel = hasAddableComponents ? "Add Component" : "No Components Available";
        if (ImGui.Button(buttonLabel, new Vector2(ImGui.GetContentRegionAvail().X, 0.0f)) && hasAddableComponents)
        {
            ImGui.OpenPopup(AddComponentPopupId);
        }

        if (hasAddableComponents == false)
        {
            ImGui.EndDisabled();
            return;
        }

        if (ImGui.BeginPopup(AddComponentPopupId) == false)
        {
            return;
        }

        foreach (var descriptor in _addableComponentTypes)
        {
            if (ImGui.MenuItem(GetAddComponentLabel(descriptor)) == false)
            {
                continue;
            }

            RuntimeComponentAccessor.AddDefault(scene.World, entity, descriptor.Type);
            EditorGui.SelectEntity(entity, scene.World);
            _interactionState.MarkSceneDirty();
            ImGui.CloseCurrentPopup();
            break;
        }

        ImGui.EndPopup();
    }

    private void DrawComponentEditorGeneric<T>(EditorScene scene, Entity entity, IIconManager icons, IPropertyDrawerRegistry propertyDrawerRegistry, RenderGraph renderGraph, IEditorInteractionState interactionState)
        where T : struct, IEntityComponent
    {
        var world = scene.World;
        
        if (world.HasComponent<T>(entity) == false)
            return;

        ref var component = ref world.GetComponent<T>(entity);
        ImGui.PushID(typeof(T).FullName);

        if (typeof(T) == typeof(NameComponent))
        {
            ref var name = ref Unsafe.As<T, NameComponent>(ref component);
            var value = name.Name ?? string.Empty;
            if (scene.EntityIcons.TryGetValue(entity, out var iconName) == false)
            {
                iconName = "object";
            }

            var iconTexture = ResolveIconTexture(iconName, icons);
            var iconPickerPopupId = $"Icon Picker##{entity.Index}:{entity.Generation}";
            if(ImGui.ImageButton("IconButton", iconTexture, EntityIconSize))
            {
                ImGui.OpenPopup(iconPickerPopupId);
            }

            DrawIconPickerModal(scene, entity, iconPickerPopupId, icons, interactionState);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
            if (ImGui.InputText("##value", ref value, 256))
            {
                var before = CaptureSingleComponentSnapshot(scene, entity, typeof(NameComponent));
                name.Name = value;
                PushComponentEdit("Edit Name", before, CaptureSingleComponentSnapshot(scene, entity, typeof(NameComponent)));
                interactionState.MarkSceneDirty();
            }

            ImGui.Separator();
            ImGui.PopID();
            return;
        }

        if (typeof(T) == typeof(LocalTransform))
        {
            ref var local = ref Unsafe.As<T, LocalTransform>(ref component);

            var position = local.LocalPosition;
            if (EditorUIUtility.InputVector3("LocalPosition", ref position))
            {
                var before = CaptureSingleComponentSnapshot(scene, entity, typeof(LocalTransform));
                world.SetLocalPosition(entity, position);
                PushComponentEdit("Edit Transform", before, CaptureSingleComponentSnapshot(scene, entity, typeof(LocalTransform)));
                interactionState.MarkSceneDirty();
            }

            var rotation = local.LocalRotation;
            var eulerDegrees = QuaternionToEulerDegrees(rotation);
            if (EditorUIUtility.InputVector3("Rotation (deg)", ref eulerDegrees))
            {
                var before = CaptureSingleComponentSnapshot(scene, entity, typeof(LocalTransform));
                world.SetLocalRotation(entity, EulerDegreesToQuaternion(eulerDegrees));
                PushComponentEdit("Edit Transform", before, CaptureSingleComponentSnapshot(scene, entity, typeof(LocalTransform)));
                interactionState.MarkSceneDirty();
            }

            var scale = local.LocalScale;
            if (EditorUIUtility.InputVector3("LocalScale", ref scale))
            {
                var before = CaptureSingleComponentSnapshot(scene, entity, typeof(LocalTransform));
                world.SetLocalScale(entity, scale);
                PushComponentEdit("Edit Transform", before, CaptureSingleComponentSnapshot(scene, entity, typeof(LocalTransform)));
                interactionState.MarkSceneDirty();
            }

            ImGui.Separator();
            ImGui.PopID();
            return;
        }

        if (typeof(T) == typeof(MeshRenderer))
        {
            var isOpen = BeginComponentSection(typeof(T).Name, out var removeRequested);
            if (removeRequested)
            {
                QueueComponentRemoval(typeof(T));
                ImGui.PopID();
                return;
            }

            if (isOpen == false)
            {
                ImGui.PopID();
                return;
            }

            ref var meshRenderer = ref Unsafe.As<T, MeshRenderer>(ref component);
            var drawResult = propertyDrawerRegistry.Draw(new PropertyDrawerContext(
                nameof(MeshRenderer.MeshAsset),
                typeof(AssetRef<Mesh>),
                meshRenderer.MeshAsset,
                scene,
                entity));
            if (drawResult.Handled && drawResult.Changed && drawResult.Value is AssetRef<Mesh> meshAsset)
            {
                var before = CaptureSingleComponentSnapshot(scene, entity, typeof(MeshRenderer));
                meshRenderer.AssignMeshAsset(meshAsset);
                PushComponentEdit("Edit Mesh Renderer", before, CaptureSingleComponentSnapshot(scene, entity, typeof(MeshRenderer)));
                interactionState.MarkSceneDirty();
            }

            drawResult = propertyDrawerRegistry.Draw(new PropertyDrawerContext(
                nameof(MeshRenderer.MaterialAsset),
                typeof(AssetRef<Material>),
                meshRenderer.MaterialAsset,
                scene,
                entity));
            if (drawResult.Handled && drawResult.Changed && drawResult.Value is AssetRef<Material> materialAsset)
            {
                var before = CaptureSingleComponentSnapshot(scene, entity, typeof(MeshRenderer));
                meshRenderer.AssignMaterialAsset(materialAsset, renderGraph);
                PushComponentEdit("Edit Mesh Renderer", before, CaptureSingleComponentSnapshot(scene, entity, typeof(MeshRenderer)));
                interactionState.MarkSceneDirty();
            }

            ImGui.Separator();
            ImGui.PopID();
            return;
        }

        if (typeof(T) == typeof(Light))
        {
            var isOpen = BeginComponentSection(typeof(T).Name, out var removeRequested);
            if (removeRequested)
            {
                QueueComponentRemoval(typeof(T));
                ImGui.PopID();
                return;
            }

            if (isOpen == false)
            {
                ImGui.PopID();
                return;
            }

            ref var light = ref Unsafe.As<T, Light>(ref component);
            var before = CaptureSingleComponentSnapshot(scene, entity, typeof(Light));
            if (EditorUIUtility.EnumCombo(nameof(Light.Type), ref light.Type))
            {
                PushComponentEdit("Edit Light", before, CaptureSingleComponentSnapshot(scene, entity, typeof(Light)));
                interactionState.MarkSceneDirty();
            }

            before = CaptureSingleComponentSnapshot(scene, entity, typeof(Light));
            if (EditorUIUtility.InputFloat(nameof(Light.Intensity), ref light.Intensity))
            {
                PushComponentEdit("Edit Light", before, CaptureSingleComponentSnapshot(scene, entity, typeof(Light)));
                interactionState.MarkSceneDirty();
            }

            if (light.Type == LightType.Point)
            {
                before = CaptureSingleComponentSnapshot(scene, entity, typeof(Light));
                if (EditorUIUtility.InputFloat(nameof(Light.Range), ref light.Range))
                {
                    PushComponentEdit("Edit Light", before, CaptureSingleComponentSnapshot(scene, entity, typeof(Light)));
                    interactionState.MarkSceneDirty();
                }
            }

            var color = light.Color.ToVector4();
            before = CaptureSingleComponentSnapshot(scene, entity, typeof(Light));
            if (EditorUIUtility.ColorEdit4(nameof(Light.Color), ref color))
            {
                light.Color = ColorRGBA.FromVector4(color);
                PushComponentEdit("Edit Light", before, CaptureSingleComponentSnapshot(scene, entity, typeof(Light)));
                interactionState.MarkSceneDirty();
            }

            if (light.Type == LightType.Directional)
            {
                before = CaptureSingleComponentSnapshot(scene, entity, typeof(Light));
                if (EditorUIUtility.Checkbox(nameof(Light.HorizonFade), ref light.HorizonFade))
                {
                    PushComponentEdit("Edit Light", before, CaptureSingleComponentSnapshot(scene, entity, typeof(Light)));
                    interactionState.MarkSceneDirty();
                }
            }

            ImGui.Separator();
            ImGui.PopID();
            return;
        }

        var isComponentOpen = BeginComponentSection(typeof(T).Name, out var isRemoveRequested);
        if (isRemoveRequested)
        {
            QueueComponentRemoval(typeof(T));
            ImGui.PopID();
            return;
        }

        if (isComponentOpen == false)
        {
            ImGui.PopID();
            return;
        }

        var typedRef = __makeref(component);
        foreach (var field in typeof(T).GetFields(BindingFlags.Instance | BindingFlags.Public))
        {
            if (field.IsInitOnly ||
                Attribute.IsDefined(field, typeof(NotSerializedAttribute)) ||
                Attribute.IsDefined(field, typeof(HideFromEditorAttribute)))
            {
                continue;
            }

            var fieldType = field.FieldType;
            var label = field.Name;

            var drawResult = propertyDrawerRegistry.Draw(new PropertyDrawerContext(
                label,
                fieldType,
                field.GetValueDirect(typedRef),
                scene,
                entity,
                field));
            if (drawResult.Handled && drawResult.Changed)
            {
                var before = CaptureSingleComponentSnapshot(scene, entity, typeof(T));
                field.SetValueDirect(typedRef, drawResult.Value!);
                PushComponentEdit($"Edit {typeof(T).Name}", before, CaptureSingleComponentSnapshot(scene, entity, typeof(T)));
                interactionState.MarkSceneDirty();
            }
        }

        ImGui.Separator();
        ImGui.PopID();
    }

    private void DrawGenericComponentEditor(EditorScene scene, World world, Entity entity, Type componentType, IPropertyDrawerRegistry propertyDrawerRegistry, IEditorInteractionState interactionState)
    {
        object componentValue;
        try
        {
            componentValue = RuntimeComponentAccessor.ReadBoxed(world, entity, componentType);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        ImGui.PushID(componentType.FullName);
        var isOpen = BeginComponentSection(componentType.Name, out var removeRequested);
        if (removeRequested)
        {
            QueueComponentRemoval(componentType);
            ImGui.PopID();
            return;
        }

        if (isOpen == false)
        {
            ImGui.PopID();
            return;
        }

        if (RuntimeComponentFieldEditor.ApplyPublicFields(componentType, propertyDrawerRegistry, ref componentValue, scene, entity))
        {
            var before = CaptureSingleComponentSnapshot(scene, entity, componentType);
            RuntimeComponentAccessor.WriteBoxed(world, entity, componentType, componentValue);
            PushComponentEdit($"Edit {componentType.Name}", before, CaptureSingleComponentSnapshot(scene, entity, componentType));
            interactionState.MarkSceneDirty();
        }

        ImGui.Separator();
        ImGui.PopID();
    }

    private SceneComponentSnapshot CaptureSingleComponentSnapshot(EditorScene scene, Entity entity, Type componentType)
    {
        return _sceneSnapshotService.CaptureComponent(scene, entity, componentType);
    }

    private void PushComponentEdit(string description, SceneComponentSnapshot before, SceneComponentSnapshot after)
    {
        if (SnapshotsEqual(before, after))
        {
            return;
        }

        _undoRedoService.BeginCapture(description);
        _undoRedoService.CommitCapture(new SceneComponentEditUndoRedoEntry(description, [before], [after]));
    }

    private static bool SnapshotsEqual(SceneComponentSnapshot left, SceneComponentSnapshot right)
    {
        return left.EntityId == right.EntityId &&
               string.Equals(left.ComponentTypeId, right.ComponentTypeId, StringComparison.Ordinal) &&
               string.Equals(NormalizeSnapshotJson(left.Data), NormalizeSnapshotJson(right.Data), StringComparison.Ordinal);
    }

    private static string NormalizeSnapshotJson(JsonElement data)
    {
        return JsonSerializer.Serialize(data, AssetJson.SerializerOptions);
    }

    private bool BeginComponentSection(string label, out bool removeRequested)
    {
        removeRequested = false;

        var pushedBoldHeader = ImGuiUiSystem.PushBoldFont();
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0.0f);
        var isOpen = ImGui.CollapsingHeader(label, ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.AllowOverlap);
        ImGui.PopStyleVar();
        ImGuiUiSystem.PopFontIfPushed(pushedBoldHeader);

        if (_icons.TryGet("delete", out var deleteIcon) == false)
        {
            return isOpen;
        }

        var cursorAfterHeader = ImGui.GetCursorPos();
        var headerMin = ImGui.GetItemRectMin();
        var headerMax = ImGui.GetItemRectMax();
        var frameHeight = headerMax.Y - headerMin.Y;
        var framePadding = new Vector2(
            MathF.Max((frameHeight - ComponentActionIconSize.X) * 0.5f, 0.0f),
            MathF.Max((frameHeight - ComponentActionIconSize.Y) * 0.5f, 0.0f));
        var buttonWidth = ComponentActionIconSize.X + (framePadding.X * 2.0f);
        var buttonHeight = ComponentActionIconSize.Y + (framePadding.Y * 2.0f);
        var buttonPos = new Vector2(
            headerMax.X - buttonWidth - ImGui.GetStyle().FramePadding.X,
            headerMin.Y + MathF.Max((frameHeight - buttonHeight) * 0.5f, 0.0f));

        ImGui.SetCursorScreenPos(buttonPos);
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.0f, 0.0f, 0.0f, 0.0f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1.0f, 1.0f, 1.0f, 0.12f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(1.0f, 1.0f, 1.0f, 0.18f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, framePadding);
        removeRequested = ImGui.ImageButton($"Remove{label}", deleteIcon, ComponentActionIconSize);
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(3);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Remove component");
        }

        ImGui.SetCursorPos(cursorAfterHeader);
        return isOpen;
    }

    private void RemoveComponent(EditorScene scene, Entity entity, Type componentType)
    {
        var snapshot = CaptureSingleComponentSnapshot(scene, entity, componentType);
        RuntimeComponentAccessor.Remove(scene.World, entity, componentType);
        EditorGui.RefreshSelectedEntity(scene.World, requestFocus: false);
        _undoRedoService.BeginCapture($"Remove {componentType.Name}");
        _undoRedoService.CommitCapture(new SceneComponentRemovalUndoRedoEntry($"Remove {componentType.Name}", [snapshot]));
        _interactionState.MarkSceneDirty();
    }

    private void QueueComponentRemoval(Type componentType)
    {
        _pendingRemovedComponentType ??= componentType;
    }

    private static nint ResolveIconTexture(string iconName, IIconManager icons)
    {
        if (icons.TryGet(iconName, out var textureId))
        {
            return textureId;
        }

        if (icons.TryGet("object", out textureId))
        {
            return textureId;
        }

        return 0;
    }

    private static void DrawIconPickerModal(EditorScene scene, Entity entity, string popupId, IIconManager icons, IEditorInteractionState interactionState)
    {
        var isOpen = true;
        ImGui.SetNextWindowSize(new Vector2(360.0f, 260.0f), ImGuiCond.Appearing);
        if (ImGui.BeginPopupModal(popupId, ref isOpen, ImGuiWindowFlags.NoResize))
        {
            var iconNames = icons.GetNames();
            if (iconNames.Count == 0)
            {
                ImGui.TextUnformatted("No icons were found in Assets/Icons.");
            }
            else
            {
                var rowWidth = MathF.Max(ImGui.GetContentRegionAvail().X, PickerIconSize.X);
                var iconsPerRow = Math.Max(1, (int)(rowWidth / (PickerIconSize.X + 8.0f)));
                var drawnCount = 0;

                foreach (var name in iconNames)
                {
                    if (icons.TryGet(name, out var textureId) == false)
                    {
                        continue;
                    }

                    ImGui.PushID(name);
                    if (ImGui.ImageButton("##icon", textureId, PickerIconSize))
                    {
                        scene.EntityIcons[entity] = name;
                        interactionState.MarkSceneDirty();
                        ImGui.CloseCurrentPopup();
                    }

                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip(name);
                    }

                    ImGui.PopID();
                    drawnCount++;
                    if (drawnCount % iconsPerRow != 0)
                    {
                        ImGui.SameLine();
                    }
                }
            }

            ImGui.Separator();
            if (ImGui.Button("Close"))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }

    private static Vector3 QuaternionToEulerDegrees(Quaternion rotation)
    {
        rotation = Quaternion.Normalize(rotation);

        var sinPitch = 2.0f * (rotation.W * rotation.X - rotation.Y * rotation.Z);
        sinPitch = Math.Clamp(sinPitch, -1.0f, 1.0f);
        var pitch = MathF.Asin(sinPitch);

        var yaw = MathF.Atan2(
            2.0f * (rotation.W * rotation.Y + rotation.Z * rotation.X),
            1.0f - 2.0f * (rotation.X * rotation.X + rotation.Y * rotation.Y));

        var roll = MathF.Atan2(
            2.0f * (rotation.W * rotation.Z + rotation.X * rotation.Y),
            1.0f - 2.0f * (rotation.Z * rotation.Z + rotation.X * rotation.X));

        return new Vector3(
            WrapDegrees(RadiansToDegrees(pitch)),
            WrapDegrees(RadiansToDegrees(yaw)),
            WrapDegrees(RadiansToDegrees(roll)));
    }

    private static Quaternion EulerDegreesToQuaternion(Vector3 eulerDegrees)
    {
        var pitch = DegreesToRadians(eulerDegrees.X);
        var yaw = DegreesToRadians(eulerDegrees.Y);
        var roll = DegreesToRadians(eulerDegrees.Z);
        return Quaternion.CreateFromYawPitchRoll(yaw, pitch, roll);
    }

    private static float DegreesToRadians(float degrees) => degrees * (MathF.PI / 180.0f);

    private static float RadiansToDegrees(float radians) => radians * (180.0f / MathF.PI);

    private static float WrapDegrees(float degrees)
    {
        degrees %= 360.0f;
        if (degrees > 180.0f)
        {
            degrees -= 360.0f;
        }
        else if (degrees < -180.0f)
        {
            degrees += 360.0f;
        }

        return degrees;
    }

    private void PopulateAddableComponentTypes(World world, Entity entity)
    {
        _addableComponentTypes.Clear();
        _componentNameCounts.Clear();
        world.GetComponentTypes(entity, _existingComponentTypes);

        foreach (var descriptor in _projectTypeCatalog.GetComponentTypes())
        {
            if (IsAddableComponentType(descriptor.Type, _existingComponentTypes) == false)
            {
                continue;
            }

            if (_addableComponentTypes.Any(candidate => candidate.Type == descriptor.Type))
            {
                continue;
            }

            _addableComponentTypes.Add(descriptor);
            _componentNameCounts[descriptor.DisplayName] = _componentNameCounts.GetValueOrDefault(descriptor.DisplayName) + 1;
        }

        _addableComponentTypes.Sort((left, right) =>
        {
            var displayNameComparison = StringComparer.OrdinalIgnoreCase.Compare(left.DisplayName, right.DisplayName);
            if (displayNameComparison != 0)
            {
                return displayNameComparison;
            }

            return StringComparer.OrdinalIgnoreCase.Compare(left.QualifiedDisplayName, right.QualifiedDisplayName);
        });
    }

    private static bool IsAddableComponentType(Type componentType, List<Type> existingComponentTypes)
    {
        if (componentType is null)
        {
            return false;
        }

        if (componentType.IsValueType == false || typeof(IEntityComponent).IsAssignableFrom(componentType) == false)
        {
            return false;
        }

        if (Attribute.IsDefined(componentType, typeof(ExcludeFromEditorAttribute)) ||
            Attribute.IsDefined(componentType, typeof(EditorOnlyAttribute)) ||
            Attribute.IsDefined(componentType, typeof(ExcludeFromAddComponentAttribute)))
        {
            return false;
        }

        return existingComponentTypes.Contains(componentType) == false;
    }

    private string GetAddComponentLabel(ProjectTypeDescriptor descriptor)
    {
        return _componentNameCounts.TryGetValue(descriptor.DisplayName, out var count) && count > 1
            ? descriptor.QualifiedDisplayName
            : descriptor.DisplayName;
    }

    private void DrawPrefabControls(EditorScene scene, Entity entity)
    {
        if (scene.EntityPrefabSourcePaths.TryGetValue(entity, out var sourcePath) == false || sourcePath.Count == 0)
        {
            return;
        }

        var prefabLabel = "Prefab";
        if (_projectService.HasOpenProject && _projectService.TryGetAsset(sourcePath[0].PrefabAssetId, out var prefabAsset))
        {
            prefabLabel = $"Prefab: {prefabAsset.Name}";
        }

        ImGui.TextDisabled(prefabLabel);
        if (ImGui.Button("Apply Overrides"))
        {
            ApplyPrefabOverrides(scene, entity);
        }

        ImGui.SameLine();
        if (ImGui.Button("Revert Overrides"))
        {
            RevertPrefabOverrides(scene, entity);
        }
    }

    private void RevertPrefabOverrides(EditorScene scene, Entity entity)
    {
        if (_projectService.HasOpenProject == false ||
            scene.EntityPrefabSourcePaths.TryGetValue(entity, out var prefabSourcePath) == false ||
            prefabSourcePath.Count == 0)
        {
            return;
        }

        var prefabEntity = new SavedEntity
        {
            PrefabSourcePath = EditorPrefabUtility.ClonePrefabSourcePath(prefabSourcePath)
        };
        if (EditorPrefabUtility.TryResolvePrefabSourceEntity(_projectService, prefabEntity, out var sourceEntity) == false)
        {
            return;
        }

        ApplySavedEntityToScene(scene, entity, sourceEntity);
        _interactionState.MarkSceneDirty();
    }

    private void ApplyPrefabOverrides(EditorScene scene, Entity entity)
    {
        if (_projectService.HasOpenProject == false ||
            scene.EntityPrefabSourcePaths.TryGetValue(entity, out var prefabSourcePath) == false ||
            prefabSourcePath.Count == 0 ||
            _projectService.TryGetAsset(prefabSourcePath[0].PrefabAssetId, out var prefabAsset) == false ||
            prefabAsset.Type != AssetType.Prefab)
        {
            return;
        }

        var prefabPath = _projectService.GetAbsolutePath(prefabAsset.RelativeAssetPath);
        var prefabFile = PrefabAssetFile.Load(prefabPath);
        var sourceEntity = prefabFile.Entities.FirstOrDefault(candidate => candidate.EntityId == prefabSourcePath[0].PrefabEntityId);
        if (sourceEntity is null)
        {
            return;
        }

        var currentEntity = SerializeEntity(scene, entity, sourceEntity.EntityId);
        sourceEntity.HasName = currentEntity.HasName;
        sourceEntity.Name = currentEntity.Name;
        sourceEntity.Enabled = currentEntity.Enabled;
        sourceEntity.Icon = currentEntity.Icon;
        sourceEntity.LocalTransform = currentEntity.LocalTransform;
        sourceEntity.Components = currentEntity.Components.Select(EditorPrefabUtility.CloneComponent).ToList();
        if (sourceEntity.PrefabSourcePath.Count > 0 &&
            EditorPrefabUtility.TryResolvePrefabSourceEntity(_projectService, sourceEntity, out var nestedSourceEntity))
        {
            sourceEntity.PrefabOverrides = EditorPrefabUtility.ComputePrefabOverrides(sourceEntity, nestedSourceEntity);
        }
        else
        {
            sourceEntity.PrefabOverrides = new SavedPrefabOverrides();
        }

        var json = JsonSerializer.Serialize(prefabFile, AssetJson.SerializerOptions);
        File.WriteAllText(prefabPath, json);
        _projectService.RefreshAssetSource(prefabAsset.RelativeSourcePath);
    }

    private SavedEntity SerializeEntity(EditorScene scene, Entity entity, Guid entityId)
    {
        var world = scene.World;
        var hasName = world.HasComponent<NameComponent>(entity);
        var savedEntity = new SavedEntity
        {
            EntityId = entityId,
            HasName = hasName,
            Name = hasName ? world.GetComponent<NameComponent>(entity).Name ?? string.Empty : string.Empty,
            Enabled = world.IsEnabled(entity),
            Icon = scene.EntityIcons.TryGetValue(entity, out var iconName) ? iconName : string.Empty,
            LocalTransform = world.HasComponent<LocalTransform>(entity)
                ? world.GetComponent<LocalTransform>(entity).GetTransform()
                : null,
            Components = []
        };

        var componentTypes = new List<Type>();
        world.GetComponentTypes(entity, componentTypes);
        for (var i = 0; i < componentTypes.Count; i++)
        {
            var componentType = componentTypes[i];
            if (componentType == typeof(NameComponent) ||
                componentType == typeof(LocalTransform) ||
                componentType == typeof(WorldTransform) ||
                componentType == typeof(Parent) ||
                componentType == typeof(Children) ||
                componentType == typeof(Sibling) ||
                componentType == typeof(DirtyTransformRoot) ||
                Attribute.IsDefined(componentType, typeof(NotSerializedAttribute)) ||
                Attribute.IsDefined(componentType, typeof(ExcludeFromEditorAttribute)) ||
                Attribute.IsDefined(componentType, typeof(EditorOnlyAttribute)))
            {
                continue;
            }

            savedEntity.Components.Add(new SavedComponent
            {
                Type = ProjectTypeResolverUtility.GetTypeName(componentType),
                TypeId = ProjectTypeResolverUtility.GetStableTypeId(componentType),
                Data = EditorEntityReferenceUtility.SerializeComponentData(scene, componentType, RuntimeComponentAccessor.ReadBoxed(world, entity, componentType))
            });
        }

        return savedEntity;
    }

    private void ApplySavedEntityToScene(EditorScene scene, Entity entity, SavedEntity savedEntity)
    {
        var world = scene.World;
        if (savedEntity.HasName)
        {
            world.AddComponent(entity, new NameComponent { Name = savedEntity.Name });
        }
        else if (world.HasComponent<NameComponent>(entity))
        {
            world.RemoveComponent<NameComponent>(entity);
        }

        scene.World.SetEnabled(entity, savedEntity.Enabled);
        if (savedEntity.LocalTransform is { } localTransform)
        {
            if (world.HasComponent<LocalTransform>(entity) == false)
            {
                world.AddTransform(entity, localTransform);
            }
            else if (Matrix4x4.Decompose(localTransform, out var scale, out var rotation, out var position))
            {
                world.SetLocalPosition(entity, position);
                world.SetLocalRotation(entity, rotation);
                world.SetLocalScale(entity, scale);
            }
        }

        world.GetComponentTypes(entity, _existingComponentTypes);
        for (var i = 0; i < _existingComponentTypes.Count; i++)
        {
            var componentType = _existingComponentTypes[i];
            if (componentType == typeof(NameComponent) ||
                componentType == typeof(LocalTransform) ||
                componentType == typeof(WorldTransform) ||
                componentType == typeof(Parent) ||
                componentType == typeof(Children) ||
                componentType == typeof(Sibling) ||
                componentType == typeof(DirtyTransformRoot))
            {
                continue;
            }

            RuntimeComponentAccessor.Remove(world, entity, componentType);
        }

        for (var i = 0; i < savedEntity.Components.Count; i++)
        {
            var component = savedEntity.Components[i];
            if (ProjectTypeResolverUtility.TryResolveFromLoadedAssemblies(component.Type, out var componentType) == false)
            {
                continue;
            }

            var componentValue = ProjectTypeStateTransferUtility.DeserializeWithFieldMerge(component.Data, componentType, entityId =>
            {
                foreach (var entry in scene.EntityIds)
                {
                    if (entry.Value == entityId && scene.World.IsAlive(entry.Key))
                    {
                        return entry.Key;
                    }
                }

                return null;
            });
            RuntimeComponentAccessor.WriteBoxed(world, entity, componentType, componentValue);
        }

        if (string.IsNullOrWhiteSpace(savedEntity.Icon))
        {
            scene.EntityIcons.Remove(entity);
        }
        else
        {
            scene.EntityIcons[entity] = savedEntity.Icon;
        }

        EditorGui.SelectEntity(entity, world, requestFocus: false);
    }
}

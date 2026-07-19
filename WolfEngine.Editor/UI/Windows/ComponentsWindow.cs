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
    private static readonly Vector2 AddComponentPopupSize = new(420.0f, 320.0f);
    private static readonly MethodInfo DrawComponentEditorGenericMethod = typeof(ComponentsWindow).GetMethod(
        nameof(DrawComponentEditorGeneric),
        BindingFlags.NonPublic | BindingFlags.Instance)!;

    private readonly IIconManager _icons;
    private readonly IPropertyDrawerRegistry _propertyDrawerRegistry;
    private readonly IAssetSelectionService _assetSelectionService;
    private readonly IEditorProjectService _projectService;
    private readonly IProjectTypeCatalog _projectTypeCatalog;
    private readonly RenderGraph _renderGraph;
    private readonly IEditorInteractionState _interactionState;
    private readonly IEditorSceneSnapshotService _sceneSnapshotService;
    private readonly IEditorUndoRedoService _undoRedoService;
    private readonly IEditorAssetRefreshService _assetRefreshService;
    private readonly List<ProjectTypeDescriptor> _addableComponentTypes = new();
    private readonly List<Type> _existingComponentTypes = new();
    private readonly Dictionary<string, int> _componentNameCounts = new(StringComparer.Ordinal);
    private string _addComponentSearchText = string.Empty;
    private readonly Dictionary<TerrainEditKey, TerrainComponent> _pendingTerrainEdits = new();
    private readonly List<Entity> _componentTargets = new();
    private Type? _pendingRemovedComponentType;
    private static readonly Vector2 EntityIconSize = Vector2.One * 15.5f;
    private static readonly Vector2 PickerIconSize = Vector2.One * 22.0f;
    private static readonly Vector2 ComponentActionIconSize = Vector2.One * 15.5f;

    public ComponentsWindow(
        IIconManager icons,
        IPropertyDrawerRegistry propertyDrawerRegistry,
        IAssetSelectionService assetSelectionService,
        IEditorProjectService projectService,
        IProjectTypeCatalog projectTypeCatalog,
        RenderGraph renderGraph,
        IEditorInteractionState interactionState,
        IEditorSceneSnapshotService sceneSnapshotService,
        IEditorUndoRedoService undoRedoService,
        IEditorAssetRefreshService assetRefreshService)
    {
        _icons = icons;
        _propertyDrawerRegistry = propertyDrawerRegistry;
        _assetSelectionService = assetSelectionService ?? throw new ArgumentNullException(nameof(assetSelectionService));
        _projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
        _projectTypeCatalog = projectTypeCatalog;
        _renderGraph = renderGraph;
        _interactionState = interactionState;
        _sceneSnapshotService = sceneSnapshotService;
        _undoRedoService = undoRedoService;
        _assetRefreshService = assetRefreshService ?? throw new ArgumentNullException(nameof(assetRefreshService));
    }

    public override string Name => "Components";

    public void ResetCachedTypes()
    {
        _addableComponentTypes.Clear();
        _existingComponentTypes.Clear();
        _componentNameCounts.Clear();
        _pendingTerrainEdits.Clear();
    }

    private AssetLinkSelectionButton CreateAssetLinkSelectionButton()
    {
        return new AssetLinkSelectionButton(
            _icons.Get("search"),
            assetId => _assetSelectionService.Select(assetId));
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

        if (TryGetComponentTargets(scene.World, componentType, out var sourceEntity) == false)
            return;

        entity = sourceEntity;

        if (componentType == typeof(TerrainComponent))
        {
            DrawTerrainComponentEditor(scene, scene.World, entity, _propertyDrawerRegistry, _interactionState);
            return;
        }

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

        ImGui.SetNextWindowSize(AddComponentPopupSize, ImGuiCond.Appearing);
        if (ImGui.BeginPopup(AddComponentPopupId) == false)
        {
            return;
        }

        if (ImGui.IsWindowAppearing())
        {
            _addComponentSearchText = string.Empty;
            ImGui.SetKeyboardFocusHere();
        }

        ImGui.InputText("##ComponentSearch", ref _addComponentSearchText, 256);
        ImGui.Separator();
        ImGui.BeginChild("AddComponentResults", new Vector2(0.0f, 240.0f), ImGuiChildFlags.Borders);
        try
        {
            var matchingComponentCount = 0;
            foreach (var descriptor in _addableComponentTypes)
            {
                var label = GetAddComponentLabel(descriptor);
                if (MatchesComponentSearch(descriptor, _addComponentSearchText) == false)
                {
                    continue;
                }

                matchingComponentCount++;
                ImGui.PushID(descriptor.Type.FullName);
                try
                {
                    if (ImGui.Selectable(label) == false)
                    {
                        continue;
                    }

                    RuntimeComponentAccessor.AddDefault(scene.World, entity, descriptor.Type);
                    EditorGui.SelectEntity(entity, scene.World);
                    _interactionState.MarkSceneDirty();
                    ImGui.CloseCurrentPopup();
                    break;
                }
                finally
                {
                    ImGui.PopID();
                }
            }

            if (matchingComponentCount == 0)
            {
                ImGui.TextDisabled("No matching components.");
            }
        }
        finally
        {
            ImGui.EndChild();
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
            var hasMixedName = IsMixed(scene, typeof(NameComponent), [nameof(NameComponent.Name)]);
            if (ImGui.InputText("##value", ref value, 256))
            {
                ApplyComponentEdit(scene, typeof(NameComponent), "Edit Name", target =>
                {
                    ref var targetName = ref world.GetComponent<NameComponent>(target);
                    targetName.Name = value;
                });
            }

            if (hasMixedName && ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Multiple values");
            }

            ImGui.Separator();
            ImGui.PopID();
            return;
        }

        if (typeof(T) == typeof(LocalTransform))
        {
            ref var local = ref Unsafe.As<T, LocalTransform>(ref component);

            var position = local.LocalPosition;
            if (DrawWithMixedValue(IsTransformMixed(scene, transform => transform.LocalPosition), () => EditorUIUtility.InputVector3("LocalPosition", ref position)))
            {
                ApplyTransformEdit(scene, target => world.SetLocalPosition(target, position));
            }

            var rotation = local.LocalRotation;
            var eulerDegrees = QuaternionToEulerDegrees(rotation);
            if (DrawWithMixedValue(IsTransformMixed(scene, transform => transform.LocalRotation), () => EditorUIUtility.InputVector3("Rotation (deg)", ref eulerDegrees)))
            {
                var nextRotation = EulerDegreesToQuaternion(eulerDegrees);
                ApplyTransformEdit(scene, target => world.SetLocalRotation(target, nextRotation));
            }

            var scale = local.LocalScale;
            if (DrawWithMixedValue(IsTransformMixed(scene, transform => transform.LocalScale), () => EditorUIUtility.InputVector3("LocalScale", ref scale)))
            {
                ApplyTransformEdit(scene, target => world.SetLocalScale(target, scale));
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
                entity,
                AssetLinkSelectionButton: CreateAssetLinkSelectionButton(),
                IsMixedValue: IsMixed(scene, typeof(MeshRenderer), [nameof(MeshRenderer.MeshAsset)])));
            if (drawResult.Handled && drawResult.Changed && drawResult.Value is AssetRef<Mesh> meshAsset)
            {
                ApplyMeshRendererEdit(scene, target =>
                {
                    ref var targetRenderer = ref world.GetComponent<MeshRenderer>(target);
                    targetRenderer.AssignMeshAsset(meshAsset);
                });
            }

            drawResult = propertyDrawerRegistry.Draw(new PropertyDrawerContext(
                nameof(MeshRenderer.MaterialAsset),
                typeof(AssetRef<Material>),
                meshRenderer.MaterialAsset,
                scene,
                entity,
                AssetLinkSelectionButton: CreateAssetLinkSelectionButton(),
                IsMixedValue: IsMixed(scene, typeof(MeshRenderer), [nameof(MeshRenderer.MaterialAsset)])));
            if (drawResult.Handled && drawResult.Changed && drawResult.Value is AssetRef<Material> materialAsset)
            {
                ApplyMeshRendererEdit(scene, target =>
                {
                    ref var targetRenderer = ref world.GetComponent<MeshRenderer>(target);
                    targetRenderer.AssignMaterialAsset(materialAsset, renderGraph);
                });
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
            var lightValue = light;
            if (DrawWithMixedValue(IsMixed(scene, typeof(Light), [nameof(Light.Type)]), () => EditorUIUtility.EnumCombo(nameof(Light.Type), ref lightValue.Type)))
            {
                ApplyLightEdit(scene, target =>
                {
                    ref var targetLight = ref world.GetComponent<Light>(target);
                    targetLight.Type = lightValue.Type;
                });
            }

            if (DrawWithMixedValue(IsMixed(scene, typeof(Light), [nameof(Light.Intensity)]), () => EditorUIUtility.InputFloat(nameof(Light.Intensity), ref lightValue.Intensity)))
            {
                ApplyLightEdit(scene, target =>
                {
                    ref var targetLight = ref world.GetComponent<Light>(target);
                    targetLight.Intensity = lightValue.Intensity;
                });
            }

            if (lightValue.Type == LightType.Point)
            {
                if (DrawWithMixedValue(IsMixed(scene, typeof(Light), [nameof(Light.Range)]), () => EditorUIUtility.InputFloat(nameof(Light.Range), ref lightValue.Range)))
                {
                    ApplyLightEdit(scene, target =>
                    {
                        ref var targetLight = ref world.GetComponent<Light>(target);
                        targetLight.Range = lightValue.Range;
                    });
                }
            }

            var color = lightValue.Color.ToVector4();
            if (DrawWithMixedValue(IsMixed(scene, typeof(Light), [nameof(Light.Color)]), () => EditorUIUtility.ColorEdit4(nameof(Light.Color), ref color)))
            {
                var nextColor = ColorRGBA.FromVector4(color);
                ApplyLightEdit(scene, target =>
                {
                    ref var targetLight = ref world.GetComponent<Light>(target);
                    targetLight.Color = nextColor;
                });
            }

            if (lightValue.Type == LightType.Directional)
            {
                if (DrawWithMixedValue(IsMixed(scene, typeof(Light), [nameof(Light.HorizonFade)]), () => EditorUIUtility.Checkbox(nameof(Light.HorizonFade), ref lightValue.HorizonFade)))
                {
                    ApplyLightEdit(scene, target =>
                    {
                        ref var targetLight = ref world.GetComponent<Light>(target);
                        targetLight.HorizonFade = lightValue.HorizonFade;
                    });
                }
            }

            ImGui.Separator();
            ImGui.PopID();
            return;
        }

        DrawGenericComponentEditor(scene, scene.World, entity, typeof(T), propertyDrawerRegistry, interactionState);
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

        var edits = new List<RuntimeComponentFieldEditor.FieldEdit>();
        if (RuntimeComponentFieldEditor.ApplyPublicFields(
                componentType,
                propertyDrawerRegistry,
                ref componentValue,
                scene,
                entity,
                CreateAssetLinkSelectionButton(),
                edits,
                path => IsMixed(scene, componentType, path)))
        {
            ApplyGenericComponentEdits(scene, componentType, edits);
        }

        ImGui.Separator();
        ImGui.PopID();
    }

    private void DrawTerrainComponentEditor(
        EditorScene scene,
        World world,
        Entity entity,
        IPropertyDrawerRegistry propertyDrawerRegistry,
        IEditorInteractionState interactionState)
    {
        var componentType = typeof(TerrainComponent);
        object componentValue;
        try
        {
            componentValue = RuntimeComponentAccessor.ReadBoxed(world, entity, componentType);
        }
        catch (InvalidOperationException)
        {
            ClearPendingTerrainEdit(world, entity);
            return;
        }

        var currentComponent = (TerrainComponent)componentValue;
        var key = new TerrainEditKey(world, entity);
        if (_pendingTerrainEdits.TryGetValue(key, out var stagedComponent) == false)
        {
            stagedComponent = currentComponent;
        }

        object stagedValue = stagedComponent;
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

        if (RuntimeComponentFieldEditor.ApplyPublicFields(componentType, propertyDrawerRegistry, ref stagedValue, scene, entity, CreateAssetLinkSelectionButton()))
        {
            stagedComponent = (TerrainComponent)stagedValue;
            _pendingTerrainEdits[key] = stagedComponent;
        }

        var hasPendingChanges = TerrainComponentEquals(stagedComponent, currentComponent) == false;
        if (hasPendingChanges == false)
        {
            _pendingTerrainEdits.Remove(key);
        }
        else
        {
            _pendingTerrainEdits[key] = stagedComponent;
            ImGui.TextDisabled("Changes staged until Apply.");
        }

        if (hasPendingChanges == false)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button("Apply Terrain Settings"))
        {
            var before = CaptureSingleComponentSnapshot(scene, entity, componentType);
            RuntimeComponentAccessor.WriteBoxed(world, entity, componentType, stagedComponent);
            PushComponentEdit("Edit TerrainComponent", before, CaptureSingleComponentSnapshot(scene, entity, componentType));
            interactionState.MarkSceneDirty();
            _pendingTerrainEdits.Remove(key);
        }

        if (hasPendingChanges == false)
        {
            ImGui.EndDisabled();
        }

        ImGui.SameLine();

        if (hasPendingChanges == false)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button("Revert Terrain Settings"))
        {
            _pendingTerrainEdits.Remove(key);
        }

        if (hasPendingChanges == false)
        {
            ImGui.EndDisabled();
        }

        ImGui.Separator();
        ImGui.PopID();
    }

    private SceneComponentSnapshot CaptureSingleComponentSnapshot(EditorScene scene, Entity entity, Type componentType)
    {
        return _sceneSnapshotService.CaptureComponent(scene, entity, componentType);
    }

    private bool TryGetComponentTargets(World world, Type componentType, out Entity sourceEntity)
    {
        _componentTargets.Clear();
        for (var i = 0; i < EditorGui.SelectedEntities.Count; i++)
        {
            var candidate = EditorGui.SelectedEntities[i];
            if (world.IsAlive(candidate) && world.HasComponent(candidate, componentType))
            {
                _componentTargets.Add(candidate);
            }
        }

        sourceEntity = _componentTargets.Count > 0 ? _componentTargets[0] : default;
        return _componentTargets.Count > 0;
    }

    private void ApplyTransformEdit(EditorScene scene, Action<Entity> apply)
    {
        ApplyComponentEdit(scene, typeof(LocalTransform), "Edit Transform", apply);
    }

    private void ApplyLightEdit(EditorScene scene, Action<Entity> apply)
    {
        ApplyComponentEdit(scene, typeof(Light), "Edit Light", apply);
    }

    private void ApplyMeshRendererEdit(EditorScene scene, Action<Entity> apply)
    {
        ApplyComponentEdit(scene, typeof(MeshRenderer), "Edit Mesh Renderer", apply);
    }

    private void ApplyComponentEdit(EditorScene scene, Type componentType, string description, Action<Entity> apply)
    {
        if (TryGetComponentTargets(scene.World, componentType, out _) == false)
        {
            return;
        }

        var before = CaptureComponentSnapshots(scene, componentType);
        for (var i = 0; i < _componentTargets.Count; i++)
        {
            apply(_componentTargets[i]);
        }

        PushComponentEdit(description, before, CaptureComponentSnapshots(scene, componentType));
    }

    private void ApplyGenericComponentEdits(EditorScene scene, Type componentType, IReadOnlyList<RuntimeComponentFieldEditor.FieldEdit> edits)
    {
        if (edits.Count == 0 || TryGetComponentTargets(scene.World, componentType, out _) == false)
        {
            return;
        }

        var before = CaptureComponentSnapshots(scene, componentType);
        for (var i = 0; i < _componentTargets.Count; i++)
        {
            var target = _componentTargets[i];
            var targetValue = RuntimeComponentAccessor.ReadBoxed(scene.World, target, componentType);
            var changed = false;
            for (var editIndex = 0; editIndex < edits.Count; editIndex++)
            {
                var edit = edits[editIndex];
                if (Equals(GetFieldValue(targetValue, edit.Path), edit.Value))
                {
                    continue;
                }

                RuntimeComponentFieldEditor.ApplyFieldEdit(targetValue, edit);
                changed = true;
            }

            if (changed)
            {
                RuntimeComponentAccessor.WriteBoxed(scene.World, target, componentType, targetValue);
            }
        }

        PushComponentEdit($"Edit {componentType.Name}", before, CaptureComponentSnapshots(scene, componentType));
    }

    private List<SceneComponentSnapshot> CaptureComponentSnapshots(EditorScene scene, Type componentType)
    {
        var snapshots = new List<SceneComponentSnapshot>(_componentTargets.Count);
        for (var i = 0; i < _componentTargets.Count; i++)
        {
            snapshots.Add(CaptureSingleComponentSnapshot(scene, _componentTargets[i], componentType));
        }

        return snapshots;
    }

    private bool IsMixed(EditorScene scene, Type componentType, IReadOnlyList<string> fieldNames)
    {
        var fields = new FieldInfo[fieldNames.Count];
        for (var i = 0; i < fieldNames.Count; i++)
        {
            fields[i] = componentType.GetField(fieldNames[i], BindingFlags.Instance | BindingFlags.Public)
                ?? throw new InvalidOperationException($"Could not find field '{fieldNames[i]}' on '{componentType.FullName}'.");
        }

        return IsMixed(scene, componentType, fields);
    }

    private bool IsTransformMixed<TValue>(EditorScene scene, Func<LocalTransform, TValue> selectValue)
    {
        if (TryGetComponentTargets(scene.World, typeof(LocalTransform), out _) == false)
        {
            return false;
        }

        var firstValue = selectValue(scene.World.GetComponent<LocalTransform>(_componentTargets[0]));
        for (var i = 1; i < _componentTargets.Count; i++)
        {
            if (EqualityComparer<TValue>.Default.Equals(firstValue, selectValue(scene.World.GetComponent<LocalTransform>(_componentTargets[i]))) == false)
            {
                return true;
            }
        }

        return false;
    }

    private bool IsMixed(EditorScene scene, Type componentType, IReadOnlyList<FieldInfo> path)
    {
        if (TryGetComponentTargets(scene.World, componentType, out _) == false)
        {
            return false;
        }

        var firstValue = GetFieldValue(RuntimeComponentAccessor.ReadBoxed(scene.World, _componentTargets[0], componentType), path);
        for (var i = 1; i < _componentTargets.Count; i++)
        {
            var value = GetFieldValue(RuntimeComponentAccessor.ReadBoxed(scene.World, _componentTargets[i], componentType), path);
            if (Equals(firstValue, value) == false)
            {
                return true;
            }
        }

        return false;
    }

    private static object? GetFieldValue(object value, IReadOnlyList<FieldInfo> path)
    {
        object? current = value;
        for (var i = 0; i < path.Count; i++)
        {
            current = current is null ? null : path[i].GetValue(current);
        }

        return current;
    }

    private static bool DrawWithMixedValue(bool isMixed, Func<bool> draw)
    {
        if (isMixed)
        {
            ImGui.TextDisabled("Multiple values");
        }

		return draw();
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

    private void PushComponentEdit(string description, IReadOnlyList<SceneComponentSnapshot> before, IReadOnlyList<SceneComponentSnapshot> after)
    {
        var changedBefore = new List<SceneComponentSnapshot>();
        var changedAfter = new List<SceneComponentSnapshot>();
        var count = Math.Min(before.Count, after.Count);
        for (var i = 0; i < count; i++)
        {
            if (SnapshotsEqual(before[i], after[i]))
            {
                continue;
            }

            changedBefore.Add(before[i]);
            changedAfter.Add(after[i]);
        }

        if (changedBefore.Count == 0)
        {
            return;
        }

        _undoRedoService.BeginCapture(description);
        _undoRedoService.CommitCapture(new SceneComponentEditUndoRedoEntry(description, changedBefore, changedAfter));
        _interactionState.MarkSceneDirty();
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
        if (componentType == typeof(TerrainComponent))
        {
            ClearPendingTerrainEdit(scene.World, entity);
        }

        EditorGui.RefreshSelectedEntity(scene.World, requestFocus: false);
        _undoRedoService.BeginCapture($"Remove {componentType.Name}");
        _undoRedoService.CommitCapture(new SceneComponentRemovalUndoRedoEntry($"Remove {componentType.Name}", [snapshot]));
        _interactionState.MarkSceneDirty();
    }

    private void ClearPendingTerrainEdit(World world, Entity entity)
    {
        _pendingTerrainEdits.Remove(new TerrainEditKey(world, entity));
    }

    private static bool TerrainComponentEquals(in TerrainComponent left, in TerrainComponent right)
    {
        return string.Equals(
            JsonSerializer.Serialize(left, AssetJson.SerializerOptions),
            JsonSerializer.Serialize(right, AssetJson.SerializerOptions),
            StringComparison.Ordinal);
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

    private static bool MatchesComponentSearch(ProjectTypeDescriptor descriptor, string searchText)
    {
        return string.IsNullOrWhiteSpace(searchText) ||
               descriptor.DisplayName.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
               descriptor.QualifiedDisplayName.Contains(searchText, StringComparison.OrdinalIgnoreCase);
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

        if (EditorPrefabUtility.IsPrefabInstanceRoot(scene, entity) &&
            scene.World.HasComponent<LocalTransform>(entity))
        {
            sourceEntity = EditorPrefabUtility.CloneEntity(sourceEntity);
            sourceEntity.LocalTransform = scene.World.GetComponent<LocalTransform>(entity).GetTransform();
        }

        ApplySavedEntityToScene(scene, entity, sourceEntity);
        _interactionState.MarkSceneDirty();
    }

    private readonly record struct TerrainEditKey(World World, Entity Entity)
    {
        public override int GetHashCode()
        {
            return HashCode.Combine(RuntimeHelpers.GetHashCode(World), Entity);
        }
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

        var refreshSnapshot = _assetRefreshService.CaptureOpenSceneAssets();
        var currentEntity = SerializeEntity(scene, entity, sourceEntity.EntityId);
        sourceEntity.HasName = currentEntity.HasName;
        sourceEntity.Name = currentEntity.Name;
        sourceEntity.Enabled = currentEntity.Enabled;
        sourceEntity.Icon = currentEntity.Icon;
        if (EditorPrefabUtility.IsPrefabInstanceRoot(scene, entity) == false)
        {
            sourceEntity.LocalTransform = currentEntity.LocalTransform;
        }
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
        _assetRefreshService.RefreshOpenSceneAssets(refreshSnapshot);
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

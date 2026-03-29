using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
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
        BindingFlags.NonPublic | BindingFlags.Static)!;

    private readonly IIconManager _icons;
    private readonly IPropertyDrawerRegistry _propertyDrawerRegistry;
    private readonly IProjectTypeCatalog _projectTypeCatalog;
    private readonly RenderGraph _renderGraph;
    private readonly List<ProjectTypeDescriptor> _addableComponentTypes = new();
    private readonly List<Type> _existingComponentTypes = new();
    private readonly Dictionary<string, int> _componentNameCounts = new(StringComparer.Ordinal);
    private static readonly Vector2 EntityIconSize = Vector2.One * 15.5f;
    private static readonly Vector2 PickerIconSize = Vector2.One * 22.0f;

    public ComponentsWindow(
        IIconManager icons,
        IPropertyDrawerRegistry propertyDrawerRegistry,
        IProjectTypeCatalog projectTypeCatalog,
        RenderGraph renderGraph)
    {
        _icons = icons;
        _propertyDrawerRegistry = propertyDrawerRegistry;
        _projectTypeCatalog = projectTypeCatalog;
        _renderGraph = renderGraph;
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
        var pushedRegularContent = ImGuiUiSystem.PushRegularFont();
        if (EditorGui.HasSelectedEntity)
        {
            DrawEntityControls(scene, EditorGui.SelectedEntity);
            foreach (var componentType in EditorGui.SelectedComponentTypes)
            {
                Draw(scene, EditorGui.SelectedEntity, componentType);
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
        }

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
            DrawComponentEditorGenericMethod.MakeGenericMethod(componentType).Invoke(null, new object[] { scene, entity, _icons, _propertyDrawerRegistry, _renderGraph });
            return;
        }

        DrawGenericComponentEditor(scene.World, entity, componentType, _propertyDrawerRegistry);
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
            ImGui.CloseCurrentPopup();
            break;
        }

        ImGui.EndPopup();
    }

    private static void DrawComponentEditorGeneric<T>(EditorScene scene, Entity entity, IIconManager icons, IPropertyDrawerRegistry propertyDrawerRegistry, RenderGraph renderGraph)
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

            DrawIconPickerModal(scene, entity, iconPickerPopupId, icons);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
            if (ImGui.InputText("##value", ref value, 256))
            {
                name.Name = value;
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
                world.SetLocalPosition(entity, position);
            }

            var rotation = local.LocalRotation;
            var eulerDegrees = QuaternionToEulerDegrees(rotation);
            if (EditorUIUtility.InputVector3("Rotation (deg)", ref eulerDegrees))
            {
                world.SetLocalRotation(entity, EulerDegreesToQuaternion(eulerDegrees));
            }

            var scale = local.LocalScale;
            if (EditorUIUtility.InputVector3("LocalScale", ref scale))
            {
                world.SetLocalScale(entity, scale);
            }

            ImGui.Separator();
            ImGui.PopID();
            return;
        }

        if (typeof(T) == typeof(MeshRenderer))
        {
            if (BeginComponentSection(typeof(T).Name) == false)
            {
                ImGui.PopID();
                return;
            }

            ref var meshRenderer = ref Unsafe.As<T, MeshRenderer>(ref component);
            var drawResult = propertyDrawerRegistry.Draw(new PropertyDrawerContext(
                nameof(MeshRenderer.MaterialAsset),
                typeof(AssetRef<Material>),
                meshRenderer.MaterialAsset));
            if (drawResult.Handled && drawResult.Changed && drawResult.Value is AssetRef<Material> materialAsset)
            {
                meshRenderer.AssignMaterialAsset(materialAsset, renderGraph);
            }

            ImGui.Separator();
            ImGui.PopID();
            return;
        }

        if (typeof(T) == typeof(Light))
        {
            if (BeginComponentSection(typeof(T).Name) == false)
            {
                ImGui.PopID();
                return;
            }

            ref var light = ref Unsafe.As<T, Light>(ref component);
            EditorUIUtility.EnumCombo(nameof(Light.Type), ref light.Type);
            EditorUIUtility.InputFloat(nameof(Light.Intensity), ref light.Intensity);
            if (light.Type == LightType.Point)
            {
                EditorUIUtility.InputFloat(nameof(Light.Range), ref light.Range);
            }

            var color = light.Color.ToVector4();
            if (EditorUIUtility.ColorEdit4(nameof(Light.Color), ref color))
            {
                light.Color = ColorRGBA.FromVector4(color);
            }

            if (light.Type == LightType.Directional)
            {
                EditorUIUtility.Checkbox(nameof(Light.HorizonFade), ref light.HorizonFade);
            }

            ImGui.Separator();
            ImGui.PopID();
            return;
        }

        if (BeginComponentSection(typeof(T).Name) == false)
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
                field.GetValueDirect(typedRef)));
            if (drawResult.Handled && drawResult.Changed)
                field.SetValueDirect(typedRef, drawResult.Value!);
        }

        ImGui.Separator();
        ImGui.PopID();
    }

    private static void DrawGenericComponentEditor(World world, Entity entity, Type componentType, IPropertyDrawerRegistry propertyDrawerRegistry)
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
        if (BeginComponentSection(componentType.Name) == false)
        {
            ImGui.PopID();
            return;
        }

        if (RuntimeComponentFieldEditor.ApplyPublicFields(componentType, propertyDrawerRegistry, ref componentValue))
        {
            RuntimeComponentAccessor.WriteBoxed(world, entity, componentType, componentValue);
        }

        ImGui.Separator();
        ImGui.PopID();
    }

    private static bool BeginComponentSection(string label)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0);
        var isOpen = EditorUIUtility.CollapsingHeader(label, true);
        ImGui.PopStyleVar();
        return isOpen;
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

    private static void DrawIconPickerModal(EditorScene scene, Entity entity, string popupId, IIconManager icons)
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
}

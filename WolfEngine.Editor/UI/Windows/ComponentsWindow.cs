using System.Numerics;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using ImGuiNET;
using WolfEngine.ECS;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor.UI;

public interface IComponentEditor
{
    void Draw(EditorScene scene, Entity entity, Type componentType);
    void DrawAddComponentControls(EditorScene scene, Entity entity);
}

public class ComponentsWindow: IComponentEditor
{
    private const string AddComponentPopupId = "AddComponentPopup";
    private static readonly MethodInfo DrawComponentEditorGenericMethod = typeof(ComponentsWindow).GetMethod(
        nameof(DrawComponentEditorGeneric),
        BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo AddComponentGenericMethod = typeof(ComponentsWindow).GetMethod(
        nameof(AddComponentGeneric),
        BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly ConcurrentDictionary<Type, MethodInfo> AddComponentMethods = new();

    private readonly IIconManager _icons;
    private readonly IPropertyDrawerRegistry _propertyDrawerRegistry;
    private readonly List<Type> _addableComponentTypes = new();
    private readonly List<Type> _existingComponentTypes = new();
    private static readonly Vector2 EntityIconSize = Vector2.One * 15.5f;
    private static readonly Vector2 PickerIconSize = Vector2.One * 22.0f;

    public ComponentsWindow(IIconManager icons, IPropertyDrawerRegistry propertyDrawerRegistry)
    {
        _icons = icons;
        _propertyDrawerRegistry = propertyDrawerRegistry;
    }

    public void Draw(EditorScene scene, Entity entity, Type componentType)
    {
        if (Attribute.IsDefined(componentType, typeof(ExcludeFromEditorAttribute)))
            return;

        if (typeof(IEntityComponent).IsAssignableFrom(componentType) == false)
            return;
        
        DrawComponentEditorGenericMethod.MakeGenericMethod(componentType).Invoke(null, new object[] { scene, entity, _icons, _propertyDrawerRegistry });
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

        foreach (var componentType in _addableComponentTypes)
        {
            if (ImGui.MenuItem(componentType.Name) == false)
            {
                continue;
            }

            AddComponent(scene.World, entity, componentType);
            EditorGui.SelectEntity(entity, scene.World);
            ImGui.CloseCurrentPopup();
            break;
        }

        ImGui.EndPopup();
    }

    private static void DrawComponentEditorGeneric<T>(EditorScene scene, Entity entity, IIconManager icons, IPropertyDrawerRegistry propertyDrawerRegistry)
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

        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0);
        var isOpen = EditorUIUtility.CollapsingHeader(typeof(T).Name, true);
        ImGui.PopStyleVar();
        if (isOpen == false)
        {
            ImGui.PopID();
            return;
        }

        var typedRef = __makeref(component);
        foreach (var field in typeof(T).GetFields(BindingFlags.Instance | BindingFlags.Public))
        {
            var fieldType = field.FieldType;
            var label = field.Name;
            var presentationHint = fieldType == typeof(Vector4)
                ? PropertyPresentationHint.PreferColorPicker
                : PropertyPresentationHint.None;
            var drawResult = propertyDrawerRegistry.Draw(new PropertyDrawerContext(
                label,
                fieldType,
                field.GetValueDirect(typedRef),
                presentationHint));
            if (drawResult.Handled && drawResult.Changed)
                field.SetValueDirect(typedRef, drawResult.Value!);
        }

        ImGui.Separator();
        ImGui.PopID();
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
        world.GetComponentTypes(entity, _existingComponentTypes);

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            foreach (var componentType in GetLoadableTypes(assembly))
            {
                if (IsAddableComponentType(componentType, _existingComponentTypes) == false)
                {
                    continue;
                }

                if (_addableComponentTypes.Contains(componentType))
                {
                    continue;
                }

                _addableComponentTypes.Add(componentType);
            }
        }

        _addableComponentTypes.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));
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

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            var loadableTypes = new List<Type>(exception.Types.Length);
            foreach (var type in exception.Types)
            {
                if (type is not null)
                {
                    loadableTypes.Add(type);
                }
            }

            return loadableTypes;
        }
    }

    private static void AddComponent(World world, Entity entity, Type componentType)
    {
        var addMethod = AddComponentMethods.GetOrAdd(componentType, static type => AddComponentGenericMethod.MakeGenericMethod(type));
        addMethod.Invoke(null, new object[] { world, entity });
    }

    private static void AddComponentGeneric<T>(World world, Entity entity) where T : struct, IEntityComponent
    {
        world.AddComponent<T>(entity);
    }
}

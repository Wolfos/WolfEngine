using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using ImGuiNET;
using WolfEngine.ECS;

namespace WolfEngine.Editor.UI;

public interface IComponentEditor
{
    void Draw(World world, Entity entity, Type componentType);
}

public class ComponentEditor: IComponentEditor
{
    public void Draw(World world, Entity entity, Type componentType)
    {
        if (Attribute.IsDefined(componentType, typeof(ExcludeFromEditorAttribute)))
            return;

        if (componentType.GetInterface(nameof(IEntityComponent)) is null)
            return;

        var method = typeof(ComponentEditor).GetMethod(nameof(DrawComponentEditorGeneric),
            BindingFlags.NonPublic | BindingFlags.Static);
        method?.MakeGenericMethod(componentType).Invoke(null, new object[] { world, entity });
    }

    private static void DrawComponentEditorGeneric<T>(World world, Entity entity)
        where T : struct, IEntityComponent
    {
        if (world.HasComponent<T>(entity) == false)
            return;

        ref var component = ref world.GetComponent<T>(entity);
        ImGui.PushID(typeof(T).FullName);

        if (typeof(T) == typeof(NameComponent))
        {
            ref var name = ref Unsafe.As<T, NameComponent>(ref component);
            var value = name.Name ?? string.Empty;
            if (EditorUIUtility.DrawLabeledField("Name", () => ImGui.InputText("##value", ref value, 256)))
                name.Name = value;

            ImGui.Separator();
            ImGui.PopID();
            return;
        }

        if (typeof(T) == typeof(LocalTransform))
        {
            ref var local = ref Unsafe.As<T, LocalTransform>(ref component);

            var position = local.LocalPosition;
            if (EditorUIUtility.DrawLabeledField("LocalPosition", () => ImGui.InputFloat3("##value", ref position)))
            {
                world.SetLocalPosition(entity, position);
            }

            var rotation = local.LocalRotation;
            var eulerDegrees = QuaternionToEulerDegrees(rotation);
            if (EditorUIUtility.DrawLabeledField("Rotation (deg)", () => ImGui.InputFloat3("##value", ref eulerDegrees)))
            {
                world.SetLocalRotation(entity, EulerDegreesToQuaternion(eulerDegrees));
            }

            var scale = local.LocalScale;
            if (EditorUIUtility.DrawLabeledField("LocalScale", () => ImGui.InputFloat3("##value", ref scale)))
            {
                world.SetLocalScale(entity, scale);
            }

            ImGui.Separator();
            ImGui.PopID();
            return;
        }

        if (ImGui.CollapsingHeader(typeof(T).Name, ImGuiTreeNodeFlags.DefaultOpen) == false)
        {
            ImGui.PopID();
            return;
        }

        var typedRef = __makeref(component);
        foreach (var field in typeof(T).GetFields(BindingFlags.Instance | BindingFlags.Public))
        {
            var fieldType = field.FieldType;
            var label = field.Name;

            if (fieldType == typeof(Vector3))
            {
                var v = (Vector3) field.GetValueDirect(typedRef);
                if (EditorUIUtility.DrawLabeledField(label, () => ImGui.InputFloat3("##value", ref v)))
                    field.SetValueDirect(typedRef, v);
            }
            else if (fieldType == typeof(Vector4))
            {
                var v = (Vector4) field.GetValueDirect(typedRef);
                if (EditorUIUtility.DrawLabeledField(label, () => ImGui.ColorEdit4("##value", ref v)))
                    field.SetValueDirect(typedRef, v);
            }
            else if (fieldType == typeof(Quaternion))
            {
                var q = (Quaternion) field.GetValueDirect(typedRef);
                var v = new Vector4(q.X, q.Y, q.Z, q.W);
                if (EditorUIUtility.DrawLabeledField(label, () => ImGui.InputFloat4("##value", ref v)))
                    field.SetValueDirect(typedRef, new Quaternion(v.X, v.Y, v.Z, v.W));
            }
            else if (fieldType == typeof(string))
            {
                var s = (string) field.GetValueDirect(typedRef) ?? string.Empty;
                if (EditorUIUtility.DrawLabeledField(label, () => ImGui.InputText("##value", ref s, 256)))
                    field.SetValueDirect(typedRef, s);
            }
            else if (fieldType == typeof(float))
            {
                var f = (float) field.GetValueDirect(typedRef);
                if (EditorUIUtility.DrawLabeledField(label, () => ImGui.InputFloat("##value", ref f)))
                    field.SetValueDirect(typedRef, f);
            }
            else if (fieldType == typeof(int))
            {
                var i = (int) field.GetValueDirect(typedRef);
                if (EditorUIUtility.DrawLabeledField(label, () => ImGui.InputInt("##value", ref i)))
                    field.SetValueDirect(typedRef, i);
            }
        }

        ImGui.Separator();
        ImGui.PopID();
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

    
}

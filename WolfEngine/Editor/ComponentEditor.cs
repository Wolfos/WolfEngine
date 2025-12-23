using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using ImGuiNET;
using WolfEngine.ECS;

namespace WolfEngine.TestGame;

public static partial class EditorGui
{
    private static void DrawComponentEditor(World world, Entity entity, Type componentType)
    {
        if (componentType.GetInterface(nameof(IEntityComponent)) is null)
            return;

        var method = typeof(EditorGui).GetMethod(nameof(DrawComponentEditorGeneric),
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
            if (DrawLabeledField("Name", () => ImGui.InputText("##value", ref value, 256)))
                name.Name = value;

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
                if (DrawLabeledField(label, () => ImGui.InputFloat3("##value", ref v)))
                    field.SetValueDirect(typedRef, v);
            }
            else if (fieldType == typeof(Vector4))
            {
                var v = (Vector4) field.GetValueDirect(typedRef);
                if (DrawLabeledField(label, () => ImGui.ColorEdit4("##value", ref v)))
                    field.SetValueDirect(typedRef, v);
            }
            else if (fieldType == typeof(Quaternion))
            {
                var q = (Quaternion) field.GetValueDirect(typedRef);
                var v = new Vector4(q.X, q.Y, q.Z, q.W);
                if (DrawLabeledField(label, () => ImGui.InputFloat4("##value", ref v)))
                    field.SetValueDirect(typedRef, new Quaternion(v.X, v.Y, v.Z, v.W));
            }
            else if (fieldType == typeof(string))
            {
                var s = (string) field.GetValueDirect(typedRef) ?? string.Empty;
                if (DrawLabeledField(label, () => ImGui.InputText("##value", ref s, 256)))
                    field.SetValueDirect(typedRef, s);
            }
            else if (fieldType == typeof(float))
            {
                var f = (float) field.GetValueDirect(typedRef);
                if (DrawLabeledField(label, () => ImGui.InputFloat("##value", ref f)))
                    field.SetValueDirect(typedRef, f);
            }
            else if (fieldType == typeof(int))
            {
                var i = (int) field.GetValueDirect(typedRef);
                if (DrawLabeledField(label, () => ImGui.InputInt("##value", ref i)))
                    field.SetValueDirect(typedRef, i);
            }
        }

        ImGui.Separator();
        ImGui.PopID();
    }

    private static bool DrawLabeledField(string label, Func<bool> drawControl)
    {
        const float labelWidth = 140.0f;
        ImGui.PushID(label);
        var startX = ImGui.GetCursorPosX();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);
        ImGui.SameLine();
        ImGui.SetCursorPosX(startX + labelWidth);
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        var changed = drawControl();
        ImGui.PopID();
        return changed;
    }
}

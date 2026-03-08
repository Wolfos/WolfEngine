using ImGuiNET;
using System.Numerics;

namespace WolfEngine.Editor.UI;

public class EditorUIUtility
{
	private const float DefaultLabelWidth = 140.0f;

	public static bool DrawLabeledField(string label, Func<bool> drawControl)
	{
		BeginLabeledField(label);
		var changed = drawControl();
		EndLabeledField();
		return changed;
	}

	public static bool InputText(string label, ref string value, uint maxLength = 256)
	{
		BeginLabeledField(label);
		var changed = ImGui.InputText("##value", ref value, maxLength);
		EndLabeledField();
		return changed;
	}

	public static bool InputFloat(string label, ref float value)
	{
		BeginLabeledField(label);
		var changed = ImGui.InputFloat("##value", ref value);
		EndLabeledField();
		return changed;
	}

	public static bool InputInt(string label, ref int value)
	{
		BeginLabeledField(label);
		var changed = ImGui.InputInt("##value", ref value);
		EndLabeledField();
		return changed;
	}

	public static bool InputVector3(string label, ref Vector3 value)
	{
		BeginLabeledField(label);
		var changed = ImGui.InputFloat3("##value", ref value);
		EndLabeledField();
		return changed;
	}

	public static bool InputVector4(string label, ref Vector4 value)
	{
		BeginLabeledField(label);
		var changed = ImGui.InputFloat4("##value", ref value);
		EndLabeledField();
		return changed;
	}

	public static bool ColorEdit4(string label, ref Vector4 value)
	{
		BeginLabeledField(label);
		var changed = ImGui.ColorEdit4("##value", ref value);
		EndLabeledField();
		return changed;
	}

	private static void BeginLabeledField(string label)
	{
		ImGui.PushID(label);
		var startX = ImGui.GetCursorPosX();
		ImGui.AlignTextToFramePadding();
		ImGui.TextUnformatted(label);
		ImGui.SameLine();
		ImGui.SetCursorPosX(startX + DefaultLabelWidth);
		ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
	}

	private static void EndLabeledField()
	{
		ImGui.PopID();
	}
}

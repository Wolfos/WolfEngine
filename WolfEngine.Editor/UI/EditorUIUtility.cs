using ImGuiNET;

namespace WolfEngine.Editor.UI;

public class EditorUIUtility
{
	public static bool DrawLabeledField(string label, Func<bool> drawControl)
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
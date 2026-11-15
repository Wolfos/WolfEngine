using ImGuiNET;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor.UI;

public sealed class EditorPreferencesWindow : EditorWindow
{
	public override string Name => "Preferences";

	public override void Draw(EditorScene scene)
	{
		var pushedBoldTitle = ImGuiUiSystem.PushBoldFont();
		Begin();
		var pushedRegularContent = ImGuiUiSystem.PushRegularFont();
		if (ImGui.Button("Save"))
		{
			EditorPreferences.Save();
		}

		var limitFps = EditorPreferences.GetLimitFPS();
		var maxFps = EditorPreferences.GetMaxFPS();
		// Limits how often the 3D views are drawn. The editor UI keeps running at the display's rate.
		EditorUIUtility.Checkbox("Limit viewport FPS", ref limitFps);
		EditorUIUtility.InputInt("Max viewport FPS", ref maxFps);
		EditorPreferences.SetLimitFPS(limitFps);
		EditorPreferences.SetMaxFPS(maxFps);

		var style = ImGui.GetStyle();
		for (int i = 0; i < (int)ImGuiCol.COUNT; i++)
		{
			var v = style.Colors[i];
			if (EditorUIUtility.DrawLabeledField(((ImGuiCol)i).ToString(), () => ImGui.ColorEdit4("##value", ref v)))
			{
				style.Colors[i] = v;
				EditorPreferences.SetColor((ImGuiCol)i, v);
			}
		}

		ImGuiUiSystem.PopFontIfPushed(pushedRegularContent);
		ImGui.End();
		ImGuiUiSystem.PopFontIfPushed(pushedBoldTitle);
	}
}

using ImGuiNET;

namespace WolfEngine.Editor;

public static class MenuBar
{
	public static void Draw()
	{
		if (ImGui.BeginMainMenuBar())
		{
			if (ImGui.BeginMenu("File")) {
				if (ImGui.MenuItem("Preferences"))
				{
					EditorPreferencesMenu.Open();
				}
				ImGui.EndMenu();
			}
			if (ImGui.BeginMenu("Edit")) {
				ImGui.EndMenu();
			}
			ImGui.EndMainMenuBar();
		}
	}
}
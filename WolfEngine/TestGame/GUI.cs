using ImGuiNET;

namespace WolfEngine.TestGame;

public static class GUI
{
	public static void Draw()
	{
		ImGui.Begin("Hello World");
		ImGui.Text("Look at this!");
		ImGui.End();
	}
}
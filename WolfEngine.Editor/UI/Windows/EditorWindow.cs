using ImGuiNET;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor.UI;

public abstract class EditorWindow
{
	public abstract string Name { get; }
	public abstract void Draw(EditorScene scene);

	protected void Begin()
	{
		ImGui.Begin(Name);
		FocusOnRightClickStart();
	}

	protected void Begin(ref bool isOpen)
	{
		ImGui.Begin(Name, ref isOpen);
		FocusOnRightClickStart();
	}

	protected static void FocusOnRightClickStart()
	{
		if (ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem) &&
		    ImGui.IsMouseClicked(ImGuiMouseButton.Right))
		{
			ImGui.SetWindowFocus();
		}
	}
}

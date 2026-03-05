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
	}
}
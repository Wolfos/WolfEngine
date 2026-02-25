using WolfEngine.ECS;

namespace WolfEngine.Editor;

public class EditorScene
{
	public World World { get; set; }
	public Dictionary<Entity, string> EntityIcons { get; set; }
}
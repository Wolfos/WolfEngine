using WolfEngine.ECS;

namespace WolfEngine.Editor;

public class WolfEngineEditor
{
	private World _editorWorld;

	public WolfEngineEditor()
	{
		CreateWorld();
	}

	private void CreateWorld()
	{
		_editorWorld = new World(WorldTag.Editor);

	}
}
namespace WolfEngine.ECS;

[Flags]
public enum WorldTag
{
	Invalid = 0,
	Editor = 1,
	Authoring = 2,
	Game = 4,
	Scene = Authoring | Game,
	All = Editor | Scene
}

namespace WolfEngine.ECS;

[Flags]
public enum WorldTag
{
	Invalid = 0,
	Editor = 1,
	Game = 2,
	All = Editor | Game
}
namespace WolfEngine.ECS;

/// <summary>
/// What kind of world a world is, and which worlds a system runs for. A system runs for a world when their tags
/// share a flag.
/// </summary>
[Flags]
public enum WorldTag
{
	Invalid = 0,
	Editor = 1,
	Authoring = 2,
	Game = 4,
	GamePaused = 8,

	/// <summary>Runs for a Game world whether or not it is paused, such as menus and the HUD.</summary>
	AnyGame = Game | GamePaused,
	Scene = Authoring | Game,
	All = Editor | Scene | GamePaused
}

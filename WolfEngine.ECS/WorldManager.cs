namespace WolfEngine.ECS;

public interface IWorldManager
{
	public World CreateWorld(WorldTag tag);
	public void AddSystem<T>() where T : ISystem, new();
}

public class WorldManager: IWorldManager
{
	private List<World> _worlds;
	private Dictionary<WorldTag, IUpdateable> _updateables = new();
	private Dictionary<WorldTag, IPreRender> _preRenders = new();
	
	public World CreateWorld(WorldTag tag)
	{
		var world = new World(tag);
		_worlds.Add(world);
		return world;
	}
	

	public void AddSystem<T>() where T : ISystem, new()
	{
		var system = new T();
		// ReSharper disable once ConvertIfStatementToSwitchStatement, systems implementing multiple is *valid*
		if (system is IUpdateable u) _updateables.Add(u.GetTag(), u);
		if (system is IPreRender p) _preRenders.Add(p.GetTag(), p);
	}

	public void Update(float deltaTime)
	{
		foreach (var world in _worlds)
		{
			foreach (var system in _updateables)
			{
				if ((system.Key & world.Tag) != 0)
				{
					system.Value.Update(deltaTime, world);
				}
			}
		}
	}

	public void OnPreRender(float deltaTime)
	{
		foreach (var world in _worlds)
		{
			foreach (var system in _preRenders)
			{
				if ((system.Key & world.Tag) != 0)
				{
					system.Value.PreRender(deltaTime, world);
				}
			}
		}
	}
}
namespace WolfEngine.ECS;

public interface IWorldManager
{
	public World CreateWorld(WorldTag tag);
	public void AddSystem<T>() where T : ISystem, new();
	public void AddSystem(ISystem system);
	public void Update(float deltaTime, WorldTag tag);
	public void OnPreRender(float deltaTime, WorldTag tag);
}

public class WorldManager: IWorldManager
{
	private readonly List<World> _worlds = new();
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
		AddSystem(system);
	}

	public void AddSystem(ISystem system)
	{
		// ReSharper disable once ConvertIfStatementToSwitchStatement, systems implementing multiple is *valid*
		if (system is IUpdateable u) _updateables.Add(u.GetTag(), u);
		if (system is IPreRender p) _preRenders.Add(p.GetTag(), p);
	}

	public void Update(float deltaTime, WorldTag tag)
	{
		foreach (var world in _worlds)
		{
			if ((world.Tag & tag) == 0) continue;

			foreach (var system in _updateables)
			{
				if ((system.Key & world.Tag) != 0)
				{
					system.Value.Update(deltaTime, world);
				}
			}
		}
	}

	public void OnPreRender(float deltaTime, WorldTag tag)
	{
		foreach (var world in _worlds)
		{
			if ((world.Tag & tag) == 0) continue;
			
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

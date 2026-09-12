namespace WolfEngine.ECS;

public interface IWorldManager
{
	public World CreateWorld(WorldTag tag);
	public void RegisterWorld(World world);
	public bool RemoveWorld(World world);
	public void AddSystem<T>(SystemExecutionGroup group = SystemExecutionGroup.Shared) where T : ISystem, new();
	public void AddSystem(ISystem system, SystemExecutionGroup group = SystemExecutionGroup.Shared);
	public bool RemoveSystem(ISystem system);
	public void SetExceptionHandler(SystemExecutionGroup groupMask, Action<ISystem, Exception>? exceptionHandler);
	public void Update(float deltaTime, WorldTag worldTagMask, SystemExecutionGroup groupMask = SystemExecutionGroup.All);
	public void PhysicsUpdate(float fixedDeltaTime, WorldTag worldTagMask, SystemExecutionGroup groupMask = SystemExecutionGroup.All);
	public void OnPreRender(float deltaTime, WorldTag worldTagMask, SystemExecutionGroup groupMask = SystemExecutionGroup.All);
	public void OnDrawGizmos(WorldTag worldTagMask, SystemExecutionGroup groupMask = SystemExecutionGroup.All);
}

public class WorldManager: IWorldManager
{
	private readonly List<World> _worlds = new();
	private readonly List<SystemRegistration> _systems = new();
	private SystemExecutionGroup _recoverableExceptionGroupMask;
	private Action<ISystem, Exception>? _exceptionHandler;

	private readonly record struct SystemRegistration(ISystem System, SystemExecutionGroup Group);
	
	public World CreateWorld(WorldTag tag)
	{
		var world = new World(tag);
		RegisterWorld(world);
		return world;
	}

	public void RegisterWorld(World world)
	{
		ArgumentNullException.ThrowIfNull(world);
		if (_worlds.Contains(world))
		{
			return;
		}

		_worlds.Add(world);
	}

	public bool RemoveWorld(World world)
	{
		ArgumentNullException.ThrowIfNull(world);
		var removed = _worlds.Remove(world);
		if (removed == false)
		{
			return false;
		}

		for (var index = 0; index < _systems.Count; index++)
		{
			var registration = _systems[index];
			if (registration.System is not IWorldRemovedListener listener)
			{
				continue;
			}

			try
			{
				listener.OnWorldRemoved(world);
			}
			catch (Exception exception) when (CanRecoverException(registration.Group))
			{
				_exceptionHandler!(registration.System, exception);
			}
		}

		return true;
	}

	public void AddSystem<T>(SystemExecutionGroup group = SystemExecutionGroup.Shared) where T : ISystem, new()
	{
		var system = new T();
		AddSystem(system, group);
	}

	public void AddSystem(ISystem system, SystemExecutionGroup group = SystemExecutionGroup.Shared)
	{
		ArgumentNullException.ThrowIfNull(system);
		if (_systems.Exists(registration => ReferenceEquals(registration.System, system)))
		{
			return;
		}

		_systems.Add(new SystemRegistration(system, group));
	}

	public bool RemoveSystem(ISystem system)
	{
		ArgumentNullException.ThrowIfNull(system);
		var removed = false;
		for (var index = _systems.Count - 1; index >= 0; index--)
		{
			if (ReferenceEquals(_systems[index].System, system))
			{
				_systems.RemoveAt(index);
				removed = true;
			}
		}

		return removed;
	}

	public void SetExceptionHandler(SystemExecutionGroup groupMask, Action<ISystem, Exception>? exceptionHandler)
	{
		_recoverableExceptionGroupMask = exceptionHandler is null ? SystemExecutionGroup.None : groupMask;
		_exceptionHandler = exceptionHandler;
	}

	public void Update(float deltaTime, WorldTag worldTagMask, SystemExecutionGroup groupMask = SystemExecutionGroup.All)
	{
		foreach (var world in _worlds)
		{
			if ((world.Tag & worldTagMask) == 0)
			{
				continue;
			}

			for (var index = 0; index < _systems.Count; index++)
			{
				var registration = _systems[index];
				if ((registration.Group & groupMask) == 0 ||
				    registration.System is not IUpdate updateable)
				{
					continue;
				}

				try
				{
					if ((updateable.GetTag() & world.Tag) != 0)
					{
						updateable.Update(deltaTime, world);
					}
				}
				catch (Exception exception) when (CanRecoverException(registration.Group))
				{
					_exceptionHandler!(registration.System, exception);
				}
			}
		}
	}

	public void PhysicsUpdate(float fixedDeltaTime, WorldTag worldTagMask, SystemExecutionGroup groupMask = SystemExecutionGroup.All)
	{
		foreach (var world in _worlds)
		{
			if ((world.Tag & worldTagMask) == 0)
			{
				continue;
			}

			for (var index = 0; index < _systems.Count; index++)
			{
				var registration = _systems[index];
				if ((registration.Group & groupMask) == 0 ||
				    registration.System is not IPhysicsUpdate physicsUpdate)
				{
					continue;
				}

				try
				{
					if ((physicsUpdate.GetTag() & world.Tag) != 0)
					{
						physicsUpdate.PhysicsUpdate(fixedDeltaTime, world);
					}
				}
				catch (Exception exception) when (CanRecoverException(registration.Group))
				{
					_exceptionHandler!(registration.System, exception);
				}
			}
		}
	}

	public void OnPreRender(float deltaTime, WorldTag worldTagMask, SystemExecutionGroup groupMask = SystemExecutionGroup.All)
	{
		foreach (var world in _worlds)
		{
			if ((world.Tag & worldTagMask) == 0)
			{
				continue;
			}

			for (var index = 0; index < _systems.Count; index++)
			{
				var registration = _systems[index];
				if ((registration.Group & groupMask) == 0 ||
				    registration.System is not IPreRender preRender)
				{
					continue;
				}

				try
				{
					if ((preRender.GetTag() & world.Tag) != 0)
					{
						preRender.PreRender(deltaTime, world);
					}
				}
				catch (Exception exception) when (CanRecoverException(registration.Group))
				{
					_exceptionHandler!(registration.System, exception);
				}
			}
		}
	}

	private bool CanRecoverException(SystemExecutionGroup systemGroup)
	{
		return _exceptionHandler is not null &&
		       (systemGroup & _recoverableExceptionGroupMask) != 0;
	}

	public void OnDrawGizmos(WorldTag worldTagMask, SystemExecutionGroup groupMask = SystemExecutionGroup.All)
	{
		foreach (var world in _worlds)
		{
			if ((world.Tag & worldTagMask) == 0)
			{
				continue;
			}

			for (var index = 0; index < _systems.Count; index++)
			{
				var registration = _systems[index];
				if ((registration.Group & groupMask) == 0 ||
				    registration.System is not IOnDrawGizmos gizmoDrawer)
				{
					continue;
				}

				try
				{
					if ((gizmoDrawer.GetTag() & world.Tag) != 0)
					{
						gizmoDrawer.OnDrawGizmos(world);
					}
				}
				catch (Exception exception) when (CanRecoverException(registration.Group))
				{
					_exceptionHandler!(registration.System, exception);
				}
			}
		}
	}
}

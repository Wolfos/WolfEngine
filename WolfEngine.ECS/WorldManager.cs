using System;
using System.Collections.Generic;

namespace WolfEngine.ECS;

public interface IWorldManager
{
	public World CreateWorld(WorldTag tag);
	public void RegisterWorld(World world);
	public bool RemoveWorld(World world);
	public void AddSystem<T>(SystemExecutionGroup group = SystemExecutionGroup.Shared) where T : ISystem, new();
	public void AddSystem(ISystem system, SystemExecutionGroup group = SystemExecutionGroup.Shared);
	public bool RemoveSystem(ISystem system);
	public void Update(float deltaTime, WorldTag worldTagMask, SystemExecutionGroup groupMask = SystemExecutionGroup.All);
	public void PhysicsUpdate(float fixedDeltaTime, WorldTag worldTagMask, SystemExecutionGroup groupMask = SystemExecutionGroup.All);
	public void OnPreRender(float deltaTime, WorldTag worldTagMask, SystemExecutionGroup groupMask = SystemExecutionGroup.All);
	public void OnDrawGizmos(WorldTag worldTagMask, SystemExecutionGroup groupMask = SystemExecutionGroup.All);
}

public class WorldManager: IWorldManager
{
	private readonly List<World> _worlds = new();
	private readonly List<SystemRegistration> _systems = new();

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
			if (_systems[index].System is IWorldRemovedListener listener)
			{
				listener.OnWorldRemoved(world);
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
				    registration.System is not IUpdate updateable ||
				    (updateable.GetTag() & world.Tag) == 0)
				{
					continue;
				}

				updateable.Update(deltaTime, world);
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
				    registration.System is not IPhysicsUpdate physicsUpdate ||
				    (physicsUpdate.GetTag() & world.Tag) == 0)
				{
					continue;
				}

				physicsUpdate.PhysicsUpdate(fixedDeltaTime, world);
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
				    registration.System is not IPreRender preRender ||
				    (preRender.GetTag() & world.Tag) == 0)
				{
					continue;
				}

				preRender.PreRender(deltaTime, world);
			}
		}
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
				    registration.System is not IOnDrawGizmos gizmoDrawer ||
				    (gizmoDrawer.GetTag() & world.Tag) == 0)
				{
					continue;
				}

				gizmoDrawer.OnDrawGizmos(world);
			}
		}
	}
}

using System;
using System.Threading;
using WolfEngine.ECS;

namespace WolfEngine.Physics;

internal interface IPhysicsSensorCallbackRegistration
{
	void TryDispatch(World world, Entity sensorEntity, Entity otherEntity, PhysicsContactEvent contactEvent);
}

internal sealed class PhysicsSensorCallbackRegistration<TSensor, TOther> : IPhysicsSensorCallbackRegistration
	where TSensor : struct, IEntityComponent
	where TOther : struct, IEntityComponent
{
	private readonly Action<World, Entity, Entity, PhysicsContactEvent> _callback;

	public PhysicsSensorCallbackRegistration(Action<World, Entity, Entity, PhysicsContactEvent> callback)
	{
		_callback = callback;
	}

	public void TryDispatch(World world, Entity sensorEntity, Entity otherEntity, PhysicsContactEvent contactEvent)
	{
		if (world.IsAlive(sensorEntity) == false ||
		    world.IsAlive(otherEntity) == false ||
		    world.HasComponent<TSensor>(sensorEntity) == false ||
		    world.HasComponent<TOther>(otherEntity) == false)
		{
			return;
		}

		_callback(world, sensorEntity, otherEntity, contactEvent);
	}
}

internal sealed class PhysicsSensorCallbackSubscription : IDisposable
{
	private RigidbodySystem? _system;
	private IPhysicsSensorCallbackRegistration? _registration;

	public PhysicsSensorCallbackSubscription(RigidbodySystem system, IPhysicsSensorCallbackRegistration registration)
	{
		_system = system;
		_registration = registration;
	}

	public void Dispose()
	{
		var system = Interlocked.Exchange(ref _system, null);
		var registration = Interlocked.Exchange(ref _registration, null);
		if (system is not null && registration is not null)
		{
			system.UnregisterSensorCallback(registration);
		}
	}
}

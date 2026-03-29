using System.Numerics;
using JoltPhysicsSharp;
using WolfEngine.ECS;

namespace WolfEngine.Physics.Tests;

[TestFixture]
public sealed class RigidbodySystemTests
{
	[Test]
	public void PhysicsUpdate_BoxColliderWithoutRigidbodyCreatesStaticBody()
	{
		var world = new World(WorldTag.Game);
		var entity = world.CreateEntity("Static Box", Matrix4x4.Identity);
		world.AddComponent(entity, BoxCollider.CreateDefault());
		using var system = new RigidbodySystem();

		system.PhysicsUpdate(1.0f / 60.0f, world);

		Assert.That(system.GetTrackedBodyCount(world), Is.EqualTo(1));
		Assert.That(system.TryGetBodyMotionType(world, entity, out var motionType), Is.True);
		Assert.That(motionType, Is.EqualTo(MotionType.Static));
	}

	[Test]
	public void PhysicsUpdate_DynamicBodyStepsAndWritesBackTransform()
	{
		var world = new World(WorldTag.Game);
		var entity = world.CreateEntity("Dynamic Box", Matrix4x4.Identity);
		world.AddComponent(entity, BoxCollider.CreateDefault());
		var rigidbody = Rigidbody.CreateDefault();
		rigidbody.LinearVelocity = new Vector3(1.0f, 0.0f, 0.0f);
		rigidbody.GravityFactor = 0.0f;
		world.AddComponent(entity, rigidbody);
		using var system = new RigidbodySystem();

		system.PhysicsUpdate(1.0f / 60.0f, world);

		Assert.That(system.TryGetBodyMotionType(world, entity, out var motionType), Is.True);
		Assert.That(motionType, Is.EqualTo(MotionType.Dynamic));
		Assert.That(world.GetComponent<LocalTransform>(entity).LocalPosition.X, Is.GreaterThan(0.0f));
	}

	[Test]
	public void OnWorldRemoved_DiscardsTrackedPhysicsState()
	{
		var world = new World(WorldTag.Game);
		var entity = world.CreateEntity("Dynamic Box", Matrix4x4.Identity);
		world.AddComponent(entity, BoxCollider.CreateDefault());
		world.AddComponent(entity, Rigidbody.CreateDefault());
		using var system = new RigidbodySystem();

		system.PhysicsUpdate(1.0f / 60.0f, world);
		system.OnWorldRemoved(world);

		Assert.That(system.GetTrackedWorldCount(), Is.EqualTo(0));
		Assert.That(system.GetTrackedBodyCount(world), Is.EqualTo(0));
	}

	[Test]
	public void PhysicsUpdate_DestroyedEntityRemovesTrackedBody()
	{
		var world = new World(WorldTag.Game);
		var entity = world.CreateEntity("Transient Box", Matrix4x4.Identity);
		world.AddComponent(entity, BoxCollider.CreateDefault());
		world.AddComponent(entity, Rigidbody.CreateDefault());
		using var system = new RigidbodySystem();

		system.PhysicsUpdate(1.0f / 60.0f, world);
		world.DestroyEntity(entity);
		system.PhysicsUpdate(1.0f / 60.0f, world);

		Assert.That(system.GetTrackedBodyCount(world), Is.EqualTo(0));
	}
}

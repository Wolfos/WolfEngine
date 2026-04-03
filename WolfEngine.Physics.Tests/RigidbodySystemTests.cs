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

	[Test]
	public void PhysicsUpdate_DynamicVelocityChangeDoesNotRecreateBody()
	{
		var world = new World(WorldTag.Game);
		var entity = world.CreateEntity("Dynamic Box", Matrix4x4.Identity);
		world.AddComponent(entity, BoxCollider.CreateDefault());
		var rigidbody = Rigidbody.CreateDefault();
		rigidbody.GravityFactor = 0.0f;
		world.AddComponent(entity, rigidbody);
		using var system = new RigidbodySystem();

		system.PhysicsUpdate(1.0f / 60.0f, world);
		Assert.That(system.TryGetTrackedBodyId(world, entity, out var bodyIdBefore), Is.True);

		ref var updatedRigidbody = ref world.GetComponent<Rigidbody>(entity);
		updatedRigidbody.LinearVelocity = new Vector3(2.0f, 0.0f, 0.0f);
		system.PhysicsUpdate(1.0f / 60.0f, world);

		Assert.That(system.TryGetTrackedBodyId(world, entity, out var bodyIdAfter), Is.True);
		Assert.That(bodyIdAfter, Is.EqualTo(bodyIdBefore));
		Assert.That(world.GetComponent<LocalTransform>(entity).LocalPosition.X, Is.GreaterThan(0.0f));
	}

	[Test]
	public void PhysicsUpdate_BoxSizeChangeRecreatesBody()
	{
		var world = new World(WorldTag.Game);
		var entity = world.CreateEntity("Dynamic Box", Matrix4x4.Identity);
		world.AddComponent(entity, BoxCollider.CreateDefault());
		world.AddComponent(entity, Rigidbody.CreateDefault());
		using var system = new RigidbodySystem();

		system.PhysicsUpdate(1.0f / 60.0f, world);
		Assert.That(system.TryGetTrackedBodyId(world, entity, out var bodyIdBefore), Is.True);

		ref var collider = ref world.GetComponent<BoxCollider>(entity);
		collider.HalfExtents = new Vector3(1.0f, 0.5f, 0.5f);
		system.PhysicsUpdate(1.0f / 60.0f, world);

		Assert.That(system.TryGetTrackedBodyId(world, entity, out var bodyIdAfter), Is.True);
		Assert.That(bodyIdAfter, Is.Not.EqualTo(bodyIdBefore));
	}

	[Test]
	public void PhysicsUpdate_CapsuleColliderCreatesBody()
	{
		var world = new World(WorldTag.Game);
		var entity = world.CreateEntity("Capsule", Matrix4x4.Identity);
		world.AddComponent(entity, CapsuleCollider.CreateDefault());
		world.AddComponent(entity, Rigidbody.CreateDefault());
		using var system = new RigidbodySystem();

		system.PhysicsUpdate(1.0f / 60.0f, world);

		Assert.That(system.GetTrackedBodyCount(world), Is.EqualTo(1));
		Assert.That(system.TryGetBodyMotionType(world, entity, out var motionType), Is.True);
		Assert.That(motionType, Is.EqualTo(MotionType.Dynamic));
	}

	[Test]
	public void TryMoveKinematicBody_MovesBodyWithoutRecreation()
	{
		var world = new World(WorldTag.Game);
		var entity = world.CreateEntity("Kinematic Capsule", Matrix4x4.Identity);
		world.AddComponent(entity, CapsuleCollider.CreateDefault());
		var rigidbody = Rigidbody.CreateDefault();
		rigidbody.BodyType = RigidbodyBodyType.Kinematic;
		rigidbody.GravityFactor = 0.0f;
		world.AddComponent(entity, rigidbody);
		using var system = new RigidbodySystem();
		system.PhysicsUpdate(1.0f / 60.0f, world);
		Assert.That(system.TryGetTrackedBodyId(world, entity, out var bodyIdBefore), Is.True);

		var moved = system.TryMoveKinematicBody(
			world,
			entity,
			new Vector3(0.0f, 2.0f, 0.0f),
			Quaternion.Identity,
			1.0f / 60.0f);
		system.PhysicsUpdate(1.0f / 60.0f, world);

		Assert.That(moved, Is.True);
		Assert.That(system.TryGetTrackedBodyId(world, entity, out var bodyIdAfter), Is.True);
		Assert.That(bodyIdAfter, Is.EqualTo(bodyIdBefore));
		Assert.That(world.GetComponent<LocalTransform>(entity).LocalPosition.Y, Is.EqualTo(2.0f).Within(0.05f));
	}

	[Test]
	public void TryRaycast_ReturnsClosestStaticHit()
	{
		var world = new World(WorldTag.Game);
		var floor = world.CreateEntity("Floor", Matrix4x4.CreateTranslation(0.0f, -1.0f, 0.0f));
		var floorCollider = BoxCollider.CreateDefault();
		floorCollider.HalfExtents = new Vector3(5.0f, 0.5f, 5.0f);
		world.AddComponent(floor, floorCollider);
		using var system = new RigidbodySystem();
		system.PhysicsUpdate(1.0f / 60.0f, world);

		var hitSomething = system.TryRaycast(
			world,
			new Vector3(0.0f, 2.0f, 0.0f),
			new Vector3(0.0f, -10.0f, 0.0f),
			out var hit);

		Assert.That(hitSomething, Is.True);
		Assert.That(hit.Entity, Is.EqualTo(floor));
		Assert.That(hit.Point.Y, Is.EqualTo(-0.5f).Within(0.05f));
		Assert.That(hit.Normal.Y, Is.GreaterThan(0.9f));
	}

	[Test]
	public void TryCastCapsule_ReturnsHitAgainstWall()
	{
		var world = new World(WorldTag.Game);
		var wall = world.CreateEntity("Wall", Matrix4x4.CreateTranslation(2.0f, 0.0f, 0.0f));
		var wallCollider = BoxCollider.CreateDefault();
		wallCollider.HalfExtents = new Vector3(0.5f, 2.0f, 2.0f);
		world.AddComponent(wall, wallCollider);
		using var system = new RigidbodySystem();
		system.PhysicsUpdate(1.0f / 60.0f, world);

		var castHit = system.TryCastCapsule(
			world,
			Vector3.Zero,
			Quaternion.Identity,
			CapsuleCollider.CreateDefault(),
			new Vector3(5.0f, 0.0f, 0.0f),
			out var hit);

		Assert.That(castHit, Is.True);
		Assert.That(hit.Entity, Is.EqualTo(wall));
		Assert.That(hit.Fraction, Is.GreaterThan(0.0f));
		Assert.That(hit.Fraction, Is.LessThan(1.0f));
		Assert.That(hit.Normal.X, Is.LessThan(-0.5f));
	}

	[Test]
	public void OverlapCapsule_ReturnsIntersectingBodies()
	{
		var world = new World(WorldTag.Game);
		var obstacle = world.CreateEntity("Obstacle", Matrix4x4.Identity);
		world.AddComponent(obstacle, BoxCollider.CreateDefault());
		using var system = new RigidbodySystem();
		system.PhysicsUpdate(1.0f / 60.0f, world);

		var hits = new List<PhysicsOverlapHit>();
		var count = system.OverlapCapsule(
			world,
			Vector3.Zero,
			Quaternion.Identity,
			CapsuleCollider.CreateDefault(),
			hits);

		Assert.That(count, Is.EqualTo(1));
		Assert.That(hits[0].Entity, Is.EqualTo(obstacle));
		Assert.That(hits[0].PenetrationDepth, Is.GreaterThanOrEqualTo(0.0f));
	}

	[Test]
	public void PhysicsUpdate_CollisionFilterBlocksContactsAndQueries()
	{
		var world = new World(WorldTag.Game);
		var floor = world.CreateEntity("Floor", Matrix4x4.CreateTranslation(0.0f, -1.0f, 0.0f));
		var floorCollider = BoxCollider.CreateDefault();
		floorCollider.HalfExtents = new Vector3(5.0f, 0.5f, 5.0f);
		world.AddComponent(floor, floorCollider);
		world.AddComponent(floor, new CollisionFilter { Layer = 2, CollidesWith = 1u << 2 });

		var box = world.CreateEntity("Falling Box", Matrix4x4.CreateTranslation(0.0f, 0.0f, 0.0f));
		world.AddComponent(box, BoxCollider.CreateDefault());
		var rigidbody = Rigidbody.CreateDefault();
		rigidbody.LinearVelocity = Vector3.Zero;
		world.AddComponent(box, rigidbody);
		world.AddComponent(box, new CollisionFilter { Layer = 1, CollidesWith = 1u << 1 });
		using var system = new RigidbodySystem();

		for (var i = 0; i < 20; i++)
		{
			system.PhysicsUpdate(1.0f / 60.0f, world);
		}

		var contacts = system.GetContactEvents(world);
		Assert.That(contacts, Is.Empty);
		Assert.That(world.GetComponent<LocalTransform>(box).LocalPosition.Y, Is.LessThan(-0.1f));

		var hitOnLayer1 = system.TryRaycast(
			world,
			new Vector3(0.0f, 2.0f, 0.0f),
			new Vector3(0.0f, -10.0f, 0.0f),
			out _,
			layerMask: 1u << 1,
			ignoredEntity: box);
		Assert.That(hitOnLayer1, Is.False);
	}

	[Test]
	public void PhysicsUpdate_ContactEventsReportAddedAndPersisted()
	{
		var world = new World(WorldTag.Game);
		var floor = world.CreateEntity("Floor", Matrix4x4.CreateTranslation(0.0f, -1.0f, 0.0f));
		var floorCollider = BoxCollider.CreateDefault();
		floorCollider.HalfExtents = new Vector3(5.0f, 0.5f, 5.0f);
		world.AddComponent(floor, floorCollider);

		var box = world.CreateEntity("Falling Box", Matrix4x4.CreateTranslation(0.0f, 0.0f, 0.0f));
		world.AddComponent(box, BoxCollider.CreateDefault());
		world.AddComponent(box, Rigidbody.CreateDefault());
		using var system = new RigidbodySystem();

		var sawAdded = false;
		var sawPersisted = false;
		for (var i = 0; i < 120; i++)
		{
			system.PhysicsUpdate(1.0f / 60.0f, world);
			foreach (var contactEvent in system.GetContactEvents(world))
			{
				if ((contactEvent.EntityA == floor && contactEvent.EntityB == box) ||
				    (contactEvent.EntityA == box && contactEvent.EntityB == floor))
				{
					sawAdded |= contactEvent.EventType == PhysicsContactEventType.Added;
					sawPersisted |= contactEvent.EventType == PhysicsContactEventType.Persisted;
				}
			}

			if (sawAdded && sawPersisted)
			{
				break;
			}
		}

		Assert.That(sawAdded, Is.True);
		Assert.That(sawPersisted, Is.True);
	}
}

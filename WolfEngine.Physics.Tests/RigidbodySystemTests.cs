using System.Numerics;
using JoltPhysicsSharp;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Rendering;

namespace WolfEngine.Physics.Tests;

[TestFixture]
public sealed class RigidbodySystemTests
{
	[TearDown]
	public void TearDown()
	{
		AssetDatabase.ClearInstanceRegistry();
	}

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
		Assert.That(world.GetComponent<LocalTransform>(entity).IsDirty, Is.False);
	}

	[Test]
	public void PhysicsUpdate_DynamicChildBodyWritesBackLocalTransformUnderParent()
	{
		var world = new World(WorldTag.Game);
		var parent = world.CreateEntity("Holder", Matrix4x4.CreateTranslation(new Vector3(10.0f, 0.0f, 0.0f)));
		var child = world.CreateEntity("Dynamic Box", Matrix4x4.Identity);
		world.SetParent(child, parent);
		world.AddComponent(child, BoxCollider.CreateDefault());
		var rigidbody = Rigidbody.CreateDefault();
		rigidbody.GravityFactor = 0.0f;
		rigidbody.LinearVelocity = new Vector3(1.0f, 0.0f, 0.0f);
		world.AddComponent(child, rigidbody);
		using var system = new RigidbodySystem();

		system.PhysicsUpdate(1.0f / 60.0f, world);

		var transform = world.GetComponent<LocalTransform>(child);
		Assert.That(transform.LocalPosition.X, Is.GreaterThan(0.0f));
		Assert.That(transform.LocalPosition.X, Is.LessThan(1.0f));
		Assert.That(transform.IsDirty, Is.False);
	}

	[Test]
	public void PhysicsUpdate_DynamicSiblingsUnderSharedParentWriteBackLocalTransforms()
	{
		var world = new World(WorldTag.Game);
		var parent = world.CreateEntity("Holder", Matrix4x4.CreateTranslation(new Vector3(10.0f, 0.0f, 0.0f)));
		var firstChild = world.CreateEntity("First", Matrix4x4.Identity);
		var secondChild = world.CreateEntity("Second", Matrix4x4.CreateTranslation(new Vector3(2.0f, 0.0f, 0.0f)));
		world.SetParent(firstChild, parent);
		world.SetParent(secondChild, parent);

		world.AddComponent(firstChild, BoxCollider.CreateDefault());
		var firstRigidbody = Rigidbody.CreateDefault();
		firstRigidbody.GravityFactor = 0.0f;
		firstRigidbody.LinearVelocity = new Vector3(1.0f, 0.0f, 0.0f);
		world.AddComponent(firstChild, firstRigidbody);

		world.AddComponent(secondChild, BoxCollider.CreateDefault());
		var secondRigidbody = Rigidbody.CreateDefault();
		secondRigidbody.GravityFactor = 0.0f;
		secondRigidbody.LinearVelocity = new Vector3(2.0f, 0.0f, 0.0f);
		world.AddComponent(secondChild, secondRigidbody);

		using var system = new RigidbodySystem();
		system.PhysicsUpdate(1.0f / 60.0f, world);

		var firstTransform = world.GetComponent<LocalTransform>(firstChild);
		var secondTransform = world.GetComponent<LocalTransform>(secondChild);
		Assert.That(firstTransform.LocalPosition.X, Is.GreaterThan(0.0f));
		Assert.That(firstTransform.LocalPosition.X, Is.LessThan(1.0f));
		Assert.That(secondTransform.LocalPosition.X, Is.GreaterThan(2.0f));
		Assert.That(secondTransform.LocalPosition.X, Is.LessThan(3.0f));
		Assert.That(firstTransform.IsDirty, Is.False);
		Assert.That(secondTransform.IsDirty, Is.False);
	}

	[Test]
	public void PhysicsUpdate_DynamicParentAndChildUseUpdatedParentPose()
	{
		var world = new World(WorldTag.Game);
		var parent = world.CreateEntity("Parent", Matrix4x4.CreateTranslation(new Vector3(10.0f, 0.0f, 0.0f)));
		var child = world.CreateEntity("Child", Matrix4x4.CreateTranslation(new Vector3(1.0f, 0.0f, 0.0f)));
		world.SetParent(child, parent);

		world.AddComponent(parent, BoxCollider.CreateDefault());
		var parentRigidbody = Rigidbody.CreateDefault();
		parentRigidbody.GravityFactor = 0.0f;
		parentRigidbody.LinearVelocity = new Vector3(1.0f, 0.0f, 0.0f);
		world.AddComponent(parent, parentRigidbody);

		world.AddComponent(child, BoxCollider.CreateDefault());
		var childRigidbody = Rigidbody.CreateDefault();
		childRigidbody.GravityFactor = 0.0f;
		childRigidbody.LinearVelocity = new Vector3(2.0f, 0.0f, 0.0f);
		world.AddComponent(child, childRigidbody);

		using var system = new RigidbodySystem();
		system.PhysicsUpdate(1.0f / 60.0f, world);

		var parentTransform = world.GetComponent<LocalTransform>(parent);
		var childTransform = world.GetComponent<LocalTransform>(child);
		Assert.That(parentTransform.LocalPosition.X, Is.GreaterThan(10.0f));
		Assert.That(parentTransform.LocalPosition.X, Is.LessThan(11.0f));
		Assert.That(childTransform.LocalPosition.X, Is.EqualTo(1.0f + (1.0f / 60.0f)).Within(0.05f));
		Assert.That(childTransform.IsDirty, Is.False);
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
	public void PhysicsUpdate_MeshColliderWithoutRigidbodyCreatesStaticBody()
	{
		var world = new World(WorldTag.Game);
		var entity = world.CreateEntity("Static Mesh", Matrix4x4.Identity);
		world.AddComponent(entity, CreateMeshCollider(CreateQuadMesh(), Guid.NewGuid()));
		using var system = new RigidbodySystem();

		system.PhysicsUpdate(1.0f / 60.0f, world);

		Assert.That(system.GetTrackedBodyCount(world), Is.EqualTo(1));
		Assert.That(system.TryGetBodyMotionType(world, entity, out var motionType), Is.True);
		Assert.That(motionType, Is.EqualTo(MotionType.Static));
	}

	[Test]
	[Explicit("Exercises native Jolt terrain heightfield integration.")]
	public void PhysicsUpdate_TerrainComponentCreatesStaticBody()
	{
		using var registry = new TestAssetRegistry();
		var heightmapId = Guid.NewGuid();
		registry.Register(heightmapId, CreateHeightTexture("terrain-flat", 5, 5, 0));

		var world = new World(WorldTag.Game);
		var entity = world.CreateEntity("Terrain", Matrix4x4.Identity);
		world.AddComponent(entity, CreateTerrainComponent(heightmapId));
		using var system = new RigidbodySystem();

		system.PhysicsUpdate(1.0f / 60.0f, world);

		Assert.That(system.GetTrackedBodyCount(world), Is.EqualTo(1));
		Assert.That(system.TryGetBodyMotionType(world, entity, out var motionType), Is.True);
		Assert.That(motionType, Is.EqualTo(MotionType.Static));
	}

	[Test]
	[Explicit("Exercises native Jolt terrain heightfield integration.")]
	public void PhysicsUpdate_NonSquareTerrainSkipsHeightfieldBodyButKeepsSampling()
	{
		using var registry = new TestAssetRegistry();
		var heightmapId = Guid.NewGuid();
		registry.Register(heightmapId, CreateHeightTexture("terrain-rect", 5, 3, 0));

		var world = new World(WorldTag.Game);
		var entity = world.CreateEntity("Terrain", Matrix4x4.Identity);
		world.AddComponent(entity, CreateTerrainComponent(heightmapId));
		using var system = new RigidbodySystem();

		system.PhysicsUpdate(1.0f / 60.0f, world);

		Assert.That(system.GetTrackedBodyCount(world), Is.EqualTo(0));
		Assert.That(system.TryGetTrackedBodyId(world, entity, out _), Is.False);
		Assert.That(system.TrySampleTerrainSurface(world, Vector3.Zero, out var sample), Is.True);
		Assert.That(sample.Entity, Is.EqualTo(entity));
		Assert.That(system.TryRaycast(world, new Vector3(0.0f, 2.0f, 0.0f), new Vector3(0.0f, -4.0f, 0.0f), out _), Is.False);
	}

	[Test]
	public void TryRaycast_HitsMeshCollider()
	{
		var world = new World(WorldTag.Game);
		var entity = world.CreateEntity("Floor Mesh", Matrix4x4.Identity);
		world.AddComponent(entity, CreateMeshCollider(CreateQuadMesh(), Guid.NewGuid()));
		using var system = new RigidbodySystem();

		system.PhysicsUpdate(1.0f / 60.0f, world);

		var hitSomething = system.TryRaycast(
			world,
			new Vector3(0.0f, 1.0f, 0.0f),
			new Vector3(0.0f, -2.0f, 0.0f),
			out var hit);

		Assert.That(hitSomething, Is.True);
		Assert.That(hit.Entity, Is.EqualTo(entity));
		Assert.That(hit.Point.Y, Is.EqualTo(0.0f).Within(0.05f));
		Assert.That(MathF.Abs(hit.Normal.Y), Is.GreaterThan(0.9f));
	}

	[Test]
	[Explicit("Exercises native Jolt terrain heightfield integration.")]
	public void TryRaycast_HitsTerrainComponent()
	{
		using var registry = new TestAssetRegistry();
		var heightmapId = Guid.NewGuid();
		registry.Register(heightmapId, CreateHeightTexture("terrain-flat", 5, 5, 0));

		var world = new World(WorldTag.Game);
		var entity = world.CreateEntity("Terrain", Matrix4x4.Identity);
		world.AddComponent(entity, CreateTerrainComponent(heightmapId));
		using var system = new RigidbodySystem();

		var hitSomething = system.TryRaycast(
			world,
			new Vector3(0.0f, 2.0f, 0.0f),
			new Vector3(0.0f, -4.0f, 0.0f),
			out var hit);

		Assert.That(hitSomething, Is.True);
		Assert.That(hit.Entity, Is.EqualTo(entity));
		Assert.That(hit.Point.Y, Is.EqualTo(0.0f).Within(0.05f));
		Assert.That(hit.Normal.Y, Is.GreaterThan(0.9f));
	}

	[Test]
	public void TrySampleTerrainSurface_ReturnsHighestTerrain()
	{
		using var registry = new TestAssetRegistry();
		var lowerHeightmapId = Guid.NewGuid();
		var upperHeightmapId = Guid.NewGuid();
		registry.Register(lowerHeightmapId, CreateHeightTexture("terrain-low", 5, 5, 0));
		registry.Register(upperHeightmapId, CreateHeightTexture("terrain-high", 5, 5, 0));

		var world = new World(WorldTag.Game);
		var lower = world.CreateEntity("Lower Terrain", Matrix4x4.Identity);
		world.AddComponent(lower, CreateTerrainComponent(lowerHeightmapId));
		var upper = world.CreateEntity("Upper Terrain", Matrix4x4.CreateTranslation(0.0f, 2.0f, 0.0f));
		world.AddComponent(upper, CreateTerrainComponent(upperHeightmapId));
		using var system = new RigidbodySystem();

		var sampled = system.TrySampleTerrainSurface(world, Vector3.Zero, out var sample);

		Assert.That(sampled, Is.True);
		Assert.That(sample.Entity, Is.EqualTo(upper));
		Assert.That(sample.Point.Y, Is.EqualTo(2.0f).Within(0.05f));
		Assert.That(sample.Normal.Y, Is.GreaterThan(0.9f));
	}

	[Test]
	public void PhysicsUpdate_MeshAssetChangeRecreatesBody()
	{
		var world = new World(WorldTag.Game);
		var entity = world.CreateEntity("Static Mesh", Matrix4x4.Identity);
		world.AddComponent(entity, CreateMeshCollider(CreateQuadMesh(), Guid.NewGuid()));
		using var system = new RigidbodySystem();

		system.PhysicsUpdate(1.0f / 60.0f, world);
		Assert.That(system.TryGetTrackedBodyId(world, entity, out var bodyIdBefore), Is.True);

		ref var collider = ref world.GetComponent<MeshCollider>(entity);
		collider.MeshAsset = new AssetRef<Mesh> { NodeId = Guid.NewGuid() };
		collider.Mesh = CreateRaisedQuadMesh();
		system.PhysicsUpdate(1.0f / 60.0f, world);

		Assert.That(system.TryGetTrackedBodyId(world, entity, out var bodyIdAfter), Is.True);
		Assert.That(bodyIdAfter, Is.Not.EqualTo(bodyIdBefore));
	}

	[Test]
	public void PhysicsUpdate_MeshCollisionFilterChangeKeepsBodyAndUpdatesQueries()
	{
		var world = new World(WorldTag.Game);
		var entity = world.CreateEntity("Filtered Mesh", Matrix4x4.Identity);
		world.AddComponent(entity, CreateMeshCollider(CreateQuadMesh(), Guid.NewGuid()));
		world.AddComponent(entity, new CollisionFilter { Layer = 1, CollidesWith = 1u << 1 });
		using var system = new RigidbodySystem();

		system.PhysicsUpdate(1.0f / 60.0f, world);
		Assert.That(system.TryGetTrackedBodyId(world, entity, out var bodyIdBefore), Is.True);

		ref var filter = ref world.GetComponent<CollisionFilter>(entity);
		filter.Layer = 2;
		filter.CollidesWith = 1u << 2;
		system.PhysicsUpdate(1.0f / 60.0f, world);

		Assert.That(system.TryGetTrackedBodyId(world, entity, out var bodyIdAfter), Is.True);
		Assert.That(bodyIdAfter, Is.EqualTo(bodyIdBefore));

		var hitsOldLayer = system.TryRaycast(
			world,
			new Vector3(0.0f, 1.0f, 0.0f),
			new Vector3(0.0f, -2.0f, 0.0f),
			out _,
			layerMask: 1u << 1);
		var hitsNewLayer = system.TryRaycast(
			world,
			new Vector3(0.0f, 1.0f, 0.0f),
			new Vector3(0.0f, -2.0f, 0.0f),
			out _,
			layerMask: 1u << 2);

		Assert.That(hitsOldLayer, Is.False);
		Assert.That(hitsNewLayer, Is.True);
	}

	[Test]
	[Explicit("Exercises native Jolt terrain heightfield integration.")]
	public void PhysicsUpdate_TerrainCollisionFilterBlocksRaycasts()
	{
		using var registry = new TestAssetRegistry();
		var heightmapId = Guid.NewGuid();
		registry.Register(heightmapId, CreateHeightTexture("terrain-flat", 5, 5, 0));

		var world = new World(WorldTag.Game);
		var terrain = world.CreateEntity("Filtered Terrain", Matrix4x4.Identity);
		world.AddComponent(terrain, CreateTerrainComponent(heightmapId));
		world.AddComponent(terrain, new CollisionFilter { Layer = 2, CollidesWith = 1u << 2 });
		using var system = new RigidbodySystem();

		system.PhysicsUpdate(1.0f / 60.0f, world);

		var hitsOldLayer = system.TryRaycast(
			world,
			new Vector3(0.0f, 2.0f, 0.0f),
			new Vector3(0.0f, -4.0f, 0.0f),
			out _,
			layerMask: 1u << 1);
		var hitsNewLayer = system.TryRaycast(
			world,
			new Vector3(0.0f, 2.0f, 0.0f),
			new Vector3(0.0f, -4.0f, 0.0f),
			out _,
			layerMask: 1u << 2);

		Assert.That(hitsOldLayer, Is.False);
		Assert.That(hitsNewLayer, Is.True);
	}

	[Test]
	[Explicit("Exercises native Jolt terrain heightfield integration.")]
	public void PhysicsUpdate_TerrainHeightmapChangeRecreatesBody()
	{
		using var registry = new TestAssetRegistry();
		var heightmapId = Guid.NewGuid();
		var texture = CreateHeightTexture("terrain-flat", 5, 5, 0);
		registry.Register(heightmapId, texture);

		var world = new World(WorldTag.Game);
		var terrain = world.CreateEntity("Terrain", Matrix4x4.Identity);
		world.AddComponent(terrain, CreateTerrainComponent(heightmapId));
		using var system = new RigidbodySystem();

		system.PhysicsUpdate(1.0f / 60.0f, world);
		Assert.That(system.TryGetTrackedBodyId(world, terrain, out var bodyIdBefore), Is.True);

		texture.ApplyTextureData(9, 9, false, TextureFormat.Rgba8Unorm, CreateHeightMipLevels(9, 9, 0));
		system.PhysicsUpdate(1.0f / 60.0f, world);

		Assert.That(system.TryGetTrackedBodyId(world, terrain, out var bodyIdAfter), Is.True);
		Assert.That(bodyIdAfter, Is.Not.EqualTo(bodyIdBefore));
	}

	[TestCase(RigidbodyBodyType.Dynamic)]
	[TestCase(RigidbodyBodyType.Kinematic)]
	public void PhysicsUpdate_NonStaticMeshColliderDoesNotCreateBody(RigidbodyBodyType bodyType)
	{
		var world = new World(WorldTag.Game);
		var entity = world.CreateEntity("NonStatic Mesh", Matrix4x4.Identity);
		world.AddComponent(entity, CreateMeshCollider(CreateQuadMesh(), Guid.NewGuid()));
		var rigidbody = Rigidbody.CreateDefault();
		rigidbody.BodyType = bodyType;
		world.AddComponent(entity, rigidbody);
		using var system = new RigidbodySystem();

		system.PhysicsUpdate(1.0f / 60.0f, world);

		Assert.That(system.GetTrackedBodyCount(world), Is.EqualTo(0));
		Assert.That(system.TryGetTrackedBodyId(world, entity, out _), Is.False);
	}

	[Test]
	public void PhysicsUpdate_DynamicBodyWithBoxCenterOffsetPreservesEntityTransform()
	{
		var world = new World(WorldTag.Game);
		var entity = world.CreateEntity("Offset Box", Matrix4x4.Identity);
		var collider = BoxCollider.CreateDefault();
		collider.Center = new Vector3(0.0f, 2.0f, 0.0f);
		world.AddComponent(entity, collider);
		var rigidbody = Rigidbody.CreateDefault();
		rigidbody.GravityFactor = 0.0f;
		world.AddComponent(entity, rigidbody);
		using var system = new RigidbodySystem();

		system.PhysicsUpdate(1.0f / 60.0f, world);

		var transform = world.GetComponent<LocalTransform>(entity);
		Assert.That(transform.LocalPosition, Is.EqualTo(Vector3.Zero).Using(Vector3Comparer.Within(0.01f)));
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
	public void TryRaycast_HitsBoxColliderCenterOffset()
	{
		var world = new World(WorldTag.Game);
		var box = world.CreateEntity("Offset Box", Matrix4x4.Identity);
		var collider = BoxCollider.CreateDefault();
		collider.Center = new Vector3(0.0f, 2.0f, 0.0f);
		world.AddComponent(box, collider);
		using var system = new RigidbodySystem();
		system.PhysicsUpdate(1.0f / 60.0f, world);

		var hitSomething = system.TryRaycast(
			world,
			new Vector3(0.0f, 5.0f, 0.0f),
			new Vector3(0.0f, -10.0f, 0.0f),
			out var hit);

		Assert.That(hitSomething, Is.True);
		Assert.That(hit.Entity, Is.EqualTo(box));
		Assert.That(hit.Point.Y, Is.EqualTo(2.5f).Within(0.05f));
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
	public void PhysicsUpdate_DynamicBodyWithCapsuleCenterOffsetPreservesEntityTransform()
	{
		var world = new World(WorldTag.Game);
		var capsule = CapsuleCollider.CreateDefault();
		capsule.Center = new Vector3(1.0f, 0.0f, 0.0f);
		var entity = world.CreateEntity("Offset Capsule", Matrix4x4.Identity);
		world.AddComponent(entity, capsule);
		var rigidbody = Rigidbody.CreateDefault();
		rigidbody.GravityFactor = 0.0f;
		world.AddComponent(entity, rigidbody);
		using var system = new RigidbodySystem();

		system.PhysicsUpdate(1.0f / 60.0f, world);

		var transform = world.GetComponent<LocalTransform>(entity);
		Assert.That(transform.LocalPosition, Is.EqualTo(Vector3.Zero).Using(Vector3Comparer.Within(0.01f)));
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
	[Explicit("Exercises native Jolt terrain heightfield integration.")]
	public void PhysicsUpdate_DynamicBodySettlesOnTerrain()
	{
		using var registry = new TestAssetRegistry();
		var heightmapId = Guid.NewGuid();
		registry.Register(heightmapId, CreateHeightTexture("terrain-flat", 5, 5, 0));

		var world = new World(WorldTag.Game);
		var terrain = world.CreateEntity("Terrain", Matrix4x4.Identity);
		world.AddComponent(terrain, CreateTerrainComponent(heightmapId));

		var box = world.CreateEntity("Box", Matrix4x4.CreateTranslation(0.0f, 2.0f, 0.0f));
		world.AddComponent(box, BoxCollider.CreateDefault());
		world.AddComponent(box, Rigidbody.CreateDefault());
		using var system = new RigidbodySystem();

		for (var i = 0; i < 120; i++)
		{
			system.PhysicsUpdate(1.0f / 60.0f, world);
		}

		Assert.That(world.GetComponent<LocalTransform>(box).LocalPosition.Y, Is.GreaterThan(0.35f));
		Assert.That(world.GetComponent<LocalTransform>(box).LocalPosition.Y, Is.LessThan(0.75f));
	}

	[Test]
	[Explicit("Exercises native Jolt terrain heightfield integration.")]
	public void TryRaycast_ReturnsClosestHitWhenBoxIsAboveTerrain()
	{
		using var registry = new TestAssetRegistry();
		var heightmapId = Guid.NewGuid();
		registry.Register(heightmapId, CreateHeightTexture("terrain-flat", 5, 5, 0));

		var world = new World(WorldTag.Game);
		var terrain = world.CreateEntity("Terrain", Matrix4x4.Identity);
		world.AddComponent(terrain, CreateTerrainComponent(heightmapId));

		var box = world.CreateEntity("Box", Matrix4x4.CreateTranslation(0.0f, 1.0f, 0.0f));
		world.AddComponent(box, BoxCollider.CreateDefault());
		using var system = new RigidbodySystem();

		var hitSomething = system.TryRaycast(
			world,
			new Vector3(0.0f, 4.0f, 0.0f),
			new Vector3(0.0f, -8.0f, 0.0f),
			out var hit);

		Assert.That(hitSomething, Is.True);
		Assert.That(hit.Entity, Is.EqualTo(box));
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

	[Test]
	public void VehiclePhysicsUpdate_CreatesVehicleRuntimeAndSingleChassisBody()
	{
		var world = new World(WorldTag.Game);
		var chassis = CreateVehicleChassis(world, Matrix4x4.CreateTranslation(0.0f, 2.0f, 0.0f));
		using var vehicleSystem = new VehicleSystem();
		using var rigidbodySystem = new RigidbodySystem();

		StepVehicle(world, vehicleSystem, rigidbodySystem, 1);

		Assert.That(vehicleSystem.GetTrackedVehicleCount(world), Is.EqualTo(1));
		Assert.That(rigidbodySystem.GetTrackedBodyCount(world), Is.EqualTo(1));
		Assert.That(rigidbodySystem.TryGetTrackedBodyId(world, chassis, out _), Is.True);
	}

	[Test]
	public void VehiclePhysicsUpdate_MissingRigidbodySkipsVehicleCreation()
	{
		var world = new World(WorldTag.Game);
		var chassis = world.CreateEntity("Vehicle", Matrix4x4.Identity);
		world.AddComponent(chassis, BoxCollider.CreateDefault());
		world.AddComponent(chassis, Vehicle.CreateDefault());
		using var vehicleSystem = new VehicleSystem();
		using var rigidbodySystem = new RigidbodySystem();

		StepVehicle(world, vehicleSystem, rigidbodySystem, 1);

		Assert.That(vehicleSystem.GetTrackedVehicleCount(world), Is.EqualTo(0));
		Assert.That(rigidbodySystem.GetTrackedBodyCount(world), Is.EqualTo(0));
	}

	[Test]
	public void VehiclePhysicsUpdate_ThrottleMovesChassisForward()
	{
		var world = CreateVehicleWorldWithGround(out var chassis);
		ref var input = ref world.GetComponent<VehicleInput>(chassis);
		input.Throttle = 1.0f;
		using var vehicleSystem = new VehicleSystem();
		using var rigidbodySystem = new RigidbodySystem();

		StepVehicle(world, vehicleSystem, rigidbodySystem, 60);

		Assert.That(world.GetComponent<LocalTransform>(chassis).LocalPosition.Z, Is.GreaterThan(0.2f));
	}

	[Test]
	public void VehiclePhysicsUpdate_SteerChangesChassisHeading()
	{
		var world = CreateVehicleWorldWithGround(out var chassis);
		ref var input = ref world.GetComponent<VehicleInput>(chassis);
		input.Throttle = 1.0f;
		input.Steer = 1.0f;
		using var vehicleSystem = new VehicleSystem();
		using var rigidbodySystem = new RigidbodySystem();

		StepVehicle(world, vehicleSystem, rigidbodySystem, 90);

		var rotation = world.GetComponent<LocalTransform>(chassis).LocalRotation;
		Assert.That(MathF.Abs(rotation.Y), Is.GreaterThan(0.02f));
	}

	[Test]
	public void VehiclePhysicsUpdate_SuspensionKeepsChassisAboveGroundAndSyncsWheelVisuals()
	{
		var world = CreateVehicleWorldWithGround(out var chassis);
		using var vehicleSystem = new VehicleSystem();
		using var rigidbodySystem = new RigidbodySystem();

		StepVehicle(world, vehicleSystem, rigidbodySystem, 120);

		var chassisTransform = world.GetComponent<LocalTransform>(chassis);
		Assert.That(chassisTransform.LocalPosition.Y, Is.GreaterThan(0.3f));

		var vehicle = world.GetComponent<Vehicle>(chassis);
		Assert.That(world.GetComponent<LocalTransform>(vehicle.FrontLeft.VisualEntity).LocalPosition.Y, Is.LessThan(0.0f));
		Assert.That(world.GetComponent<LocalTransform>(vehicle.FrontRight.VisualEntity).LocalPosition.Y, Is.LessThan(0.0f));
		Assert.That(world.GetComponent<LocalTransform>(vehicle.RearLeft.VisualEntity).LocalPosition.Y, Is.LessThan(0.0f));
		Assert.That(world.GetComponent<LocalTransform>(vehicle.RearRight.VisualEntity).LocalPosition.Y, Is.LessThan(0.0f));
	}

	[Test]
	public void VehiclePhysicsUpdate_DestroyedWheelEntityDoesNotTearDownVehicle()
	{
		var world = CreateVehicleWorldWithGround(out var chassis);
		var vehicle = world.GetComponent<Vehicle>(chassis);
		using var vehicleSystem = new VehicleSystem();
		using var rigidbodySystem = new RigidbodySystem();

		StepVehicle(world, vehicleSystem, rigidbodySystem, 1);
		world.DestroyEntity(vehicle.FrontLeft.VisualEntity);
		StepVehicle(world, vehicleSystem, rigidbodySystem, 1);

		Assert.That(vehicleSystem.GetTrackedVehicleCount(world), Is.EqualTo(1));
		Assert.That(rigidbodySystem.TryGetTrackedBodyId(world, chassis, out _), Is.True);
	}

	[Test]
	public void VehiclePhysicsUpdate_DestroyedChassisRemovesVehicleRuntime()
	{
		var world = CreateVehicleWorldWithGround(out var chassis);
		using var vehicleSystem = new VehicleSystem();
		using var rigidbodySystem = new RigidbodySystem();

		StepVehicle(world, vehicleSystem, rigidbodySystem, 1);
		world.DestroyEntity(chassis);
		StepVehicle(world, vehicleSystem, rigidbodySystem, 1);

		Assert.That(vehicleSystem.GetTrackedVehicleCount(world), Is.EqualTo(0));
		Assert.That(rigidbodySystem.TryGetTrackedBodyId(world, chassis, out _), Is.False);
	}

	[Test]
	public void VehiclePhysicsUpdate_ChangingWheelRadiusRecreatesChassisBody()
	{
		var world = CreateVehicleWorldWithGround(out var chassis);
		using var vehicleSystem = new VehicleSystem();
		using var rigidbodySystem = new RigidbodySystem();

		StepVehicle(world, vehicleSystem, rigidbodySystem, 1);
		Assert.That(rigidbodySystem.TryGetTrackedBodyId(world, chassis, out var before), Is.True);

		ref var vehicle = ref world.GetComponent<Vehicle>(chassis);
		vehicle.FrontLeft.Radius += 0.1f;
		StepVehicle(world, vehicleSystem, rigidbodySystem, 1);

		Assert.That(rigidbodySystem.TryGetTrackedBodyId(world, chassis, out var after), Is.True);
		Assert.That(after, Is.Not.EqualTo(before));
	}

	[Test]
	public void VehiclePhysicsUpdate_ChangingInputDoesNotRecreateChassisBody()
	{
		var world = CreateVehicleWorldWithGround(out var chassis);
		using var vehicleSystem = new VehicleSystem();
		using var rigidbodySystem = new RigidbodySystem();

		StepVehicle(world, vehicleSystem, rigidbodySystem, 1);
		Assert.That(rigidbodySystem.TryGetTrackedBodyId(world, chassis, out var before), Is.True);

		ref var input = ref world.GetComponent<VehicleInput>(chassis);
		input.Throttle = 1.0f;
		StepVehicle(world, vehicleSystem, rigidbodySystem, 1);

		Assert.That(rigidbodySystem.TryGetTrackedBodyId(world, chassis, out var after), Is.True);
		Assert.That(after, Is.EqualTo(before));
	}

	[Test]
	public void VehiclePhysicsUpdate_ParentedChassisWritesBackLocalTransform()
	{
		var world = new World(WorldTag.Game);
		var parent = world.CreateEntity("Parent", Matrix4x4.CreateTranslation(new Vector3(10.0f, 0.0f, 0.0f)));
		var chassis = CreateVehicleChassis(world, Matrix4x4.CreateTranslation(new Vector3(1.0f, 2.0f, 0.0f)));
		world.SetParent(chassis, parent);
		CreateGround(world);
		ref var input = ref world.GetComponent<VehicleInput>(chassis);
		input.Throttle = 1.0f;
		using var vehicleSystem = new VehicleSystem();
		using var rigidbodySystem = new RigidbodySystem();

		StepVehicle(world, vehicleSystem, rigidbodySystem, 60);

		Assert.That(world.GetComponent<LocalTransform>(chassis).LocalPosition.Z, Is.GreaterThan(0.2f));
		Assert.That(world.GetComponent<LocalTransform>(chassis).LocalPosition.X, Is.EqualTo(1.0f).Within(0.5f));
	}

	private sealed class Vector3Comparer(float tolerance) : IEqualityComparer<Vector3>
	{
		public static Vector3Comparer Within(float tolerance)
		{
			return new Vector3Comparer(tolerance);
		}

		public bool Equals(Vector3 x, Vector3 y)
		{
			return Vector3.Distance(x, y) <= tolerance;
		}

		public int GetHashCode(Vector3 obj)
		{
			return obj.GetHashCode();
		}
	}

	private static MeshCollider CreateMeshCollider(Mesh mesh, Guid assetId)
	{
		return new MeshCollider
		{
			MeshAsset = new AssetRef<Mesh> { NodeId = assetId },
			Mesh = mesh
		};
	}

	private static TerrainComponent CreateTerrainComponent(Guid heightmapId)
	{
		return new TerrainComponent
		{
			TerrainAsset = new AssetRef<TerrainAsset> { NodeId = heightmapId },
			WorldSizeMeters = new Vector2(4.0f, 4.0f),
			HeightScaleMeters = 4.0f,
			ChunkSizeMeters = 4.0f,
			LodCount = 3,
			Lod0ResolutionInQuads = 4,
			LodDistancesMeters = [120.0f, 320.0f]
		};
	}

	private static Texture CreateHeightTexture(string name, int width, int height, byte normalizedHeight)
	{
		var samples = new byte[width * height];
		Array.Fill(samples, normalizedHeight);
		return CreateHeightTexture(name, width, height, samples);
	}

	private static Texture CreateHeightTexture(string name, int width, int height, byte[] normalizedHeights)
	{
		return new Texture(name, width, height, false, TextureFormat.Rgba8Unorm, CreateHeightMipLevels(width, height, normalizedHeights));
	}

	private static TerrainAsset CreateTerrainAssetFromHeightTexture(Texture heightTexture)
	{
		var topMip = heightTexture.MipLevels[0];
		var heightData = new byte[topMip.Width * topMip.Height * 2];
		for (var i = 0; i < topMip.Width * topMip.Height; i++)
		{
			var height = (ushort)(topMip.Data[i * 4] * 257);
			var offset = i * 2;
			heightData[offset] = (byte)(height & 0xFF);
			heightData[offset + 1] = (byte)(height >> 8);
		}

		var heightmap = new Texture(heightTexture.Name, topMip.Width, topMip.Height, false, TextureFormat.R16Unorm, [new TextureMipData(topMip.Width, topMip.Height, heightData)]);
		var indexData = new byte[topMip.Width * topMip.Height * 4];
		var weightData = new byte[topMip.Width * topMip.Height * 4];
		for (var i = 0; i < topMip.Width * topMip.Height; i++)
		{
			weightData[i * 4] = 255;
		}

		var layerMips = TerrainLayerMapUtility.GenerateLayerMipChain(
			new TextureMipData(topMip.Width, topMip.Height, indexData),
			new TextureMipData(topMip.Width, topMip.Height, weightData));
		var layerIndexMap = new Texture($"{heightTexture.Name}_layers", topMip.Width, topMip.Height, false, TextureFormat.Rgba8Uint, layerMips.Indices);
		var layerWeightMap = new Texture($"{heightTexture.Name}_weights", topMip.Width, topMip.Height, false, TextureFormat.Rgba8Unorm, layerMips.Weights);
		return new TerrainAsset(heightTexture.Name, heightmap, layerIndexMap, layerWeightMap);
	}

	private static TextureMipData[] CreateHeightMipLevels(int width, int height, byte normalizedHeight)
	{
		var samples = new byte[width * height];
		Array.Fill(samples, normalizedHeight);
		return CreateHeightMipLevels(width, height, samples);
	}

	private static TextureMipData[] CreateHeightMipLevels(int width, int height, byte[] normalizedHeights)
	{
		var data = new byte[width * height * 4];
		for (var i = 0; i < normalizedHeights.Length; i++)
		{
			var offset = i * 4;
			data[offset] = normalizedHeights[i];
			data[offset + 1] = 0;
			data[offset + 2] = 0;
			data[offset + 3] = 255;
		}

		return [new TextureMipData(width, height, data)];
	}

	private static Mesh CreateQuadMesh(float y = 0.0f)
	{
		return new Mesh(
			[
				new Vector4(-1.0f, y, -1.0f, 1.0f),
				new Vector4(1.0f, y, -1.0f, 1.0f),
				new Vector4(1.0f, y, 1.0f, 1.0f),
				new Vector4(-1.0f, y, 1.0f, 1.0f)
			],
			[0u, 1u, 2u, 0u, 2u, 3u]);
	}

	private static Mesh CreateRaisedQuadMesh()
	{
		return CreateQuadMesh(1.0f);
	}

	private static World CreateVehicleWorldWithGround(out Entity chassis)
	{
		var world = new World(WorldTag.Game);
		CreateGround(world);
		chassis = CreateVehicleChassis(world, Matrix4x4.CreateTranslation(0.0f, 2.0f, 0.0f));
		return world;
	}

	private static void CreateGround(World world)
	{
		var ground = world.CreateEntity("Ground", Matrix4x4.CreateTranslation(0.0f, -0.5f, 0.0f));
		var collider = BoxCollider.CreateDefault();
		collider.HalfExtents = new Vector3(50.0f, 0.5f, 50.0f);
		world.AddComponent(ground, collider);
	}

	private static Entity CreateVehicleChassis(World world, Matrix4x4 transform)
	{
		var chassis = world.CreateEntity("Vehicle", transform);
		var collider = BoxCollider.CreateDefault();
		collider.HalfExtents = new Vector3(0.9f, 0.35f, 1.4f);
		world.AddComponent(chassis, collider);
		var rigidbody = Rigidbody.CreateDefault();
		rigidbody.BodyType = RigidbodyBodyType.Dynamic;
		rigidbody.Mass = 1200.0f;
		world.AddComponent(chassis, rigidbody);
		var vehicle = Vehicle.CreateDefault();
		vehicle.LongitudinalFriction = 2.0f;
		vehicle.LateralFriction = 2.0f;
		vehicle.FrontLeft.VisualEntity = CreateWheelEntity(world, "FrontLeft");
		vehicle.FrontRight.VisualEntity = CreateWheelEntity(world, "FrontRight");
		vehicle.RearLeft.VisualEntity = CreateWheelEntity(world, "RearLeft");
		vehicle.RearRight.VisualEntity = CreateWheelEntity(world, "RearRight");
		world.AddComponent(chassis, vehicle);
		world.AddComponent(chassis, VehicleInput.CreateDefault());
		world.SetParent(vehicle.FrontLeft.VisualEntity, chassis);
		world.SetParent(vehicle.FrontRight.VisualEntity, chassis);
		world.SetParent(vehicle.RearLeft.VisualEntity, chassis);
		world.SetParent(vehicle.RearRight.VisualEntity, chassis);
		return chassis;
	}

	private static Entity CreateWheelEntity(World world, string name)
	{
		return world.CreateEntity(name, Matrix4x4.Identity);
	}

	private static void StepVehicle(World world, VehicleSystem vehicleSystem, RigidbodySystem rigidbodySystem, int steps)
	{
		for (var index = 0; index < steps; index++)
		{
			vehicleSystem.PhysicsUpdate(1.0f / 60.0f, world);
			rigidbodySystem.PhysicsUpdate(1.0f / 60.0f, world);
		}
	}

	private sealed class TestAssetRegistry : IAssetInstanceRegistry, IDisposable
	{
		private readonly Dictionary<Guid, object> _assets = new();

		public TestAssetRegistry()
		{
			AssetDatabase.SetInstanceRegistry(this);
		}

		public void Register(Guid assetId, object asset)
		{
			_assets[assetId] = asset;
		}

		public object? GetInstance(Guid assetId, Type expectedType)
		{
			if (_assets.TryGetValue(assetId, out var asset) == false)
			{
				return null;
			}

			if (expectedType.IsInstanceOfType(asset))
			{
				return asset;
			}

			return expectedType == typeof(TerrainAsset) && asset is Texture heightTexture
				? CreateTerrainAssetFromHeightTexture(heightTexture)
				: null;
		}

		public void RefreshProject(string projectRootPath, AssetDatabase database)
		{
		}

		public void InvalidateAssets(IEnumerable<Guid> assetIds)
		{
			foreach (var assetId in assetIds)
			{
				_assets.Remove(assetId);
			}
		}

		public void ClearCachedInstances()
		{
			_assets.Clear();
		}

		public void Clear()
		{
			_assets.Clear();
		}

		public void Dispose()
		{
			AssetDatabase.ClearInstanceRegistry();
		}
	}
}

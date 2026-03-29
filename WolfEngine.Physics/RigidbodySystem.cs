using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using JoltPhysicsSharp;
using WolfEngine.ECS;

namespace WolfEngine.Physics;

public sealed class RigidbodySystem : IPhysicsUpdate, IWorldRemovedListener, IDisposable
{
	private const uint StaticObjectLayerValue = 0;
	private const uint DynamicObjectLayerValue = 1;
	private const uint ObjectLayerCount = 2;
	private const uint BroadPhaseLayerCount = 2;
	private const int CollisionSteps = 1;

	private static readonly Vector3 DefaultGravity = new(0.0f, -9.81f, 0.0f);
	private static readonly Lock FoundationLock = new();
	private static int _foundationReferenceCount;
	private static bool _foundationHandlersConfigured;
	private readonly Dictionary<World, PhysicsWorldState> _worldStates = new();
	private bool _disposed;

	public RigidbodySystem()
	{
		AcquireFoundation();
	}

	public WorldTag GetTag() => WorldTag.Game;

	public void PhysicsUpdate(float fixedDeltaTime, World world)
	{
		ArgumentNullException.ThrowIfNull(world);
		var state = GetOrCreateWorldState(world);
		SynchronizeBodies(world, state);

		var updateError = state.PhysicsSystem.Update(fixedDeltaTime, CollisionSteps, state.JobSystem);
		if (updateError != PhysicsUpdateError.None)
		{
			throw new InvalidOperationException($"Jolt physics update failed: {updateError}.");
		}

		SyncDynamicBodiesBackToWorld(world, state);
	}

	public void OnWorldRemoved(World world)
	{
		if (world is null)
		{
			return;
		}

		if (_worldStates.Remove(world, out var state))
		{
			state.Dispose();
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		foreach (var state in _worldStates.Values)
		{
			state.Dispose();
		}

		_worldStates.Clear();
		ReleaseFoundation();
		_disposed = true;
	}

	internal int GetTrackedWorldCount() => _worldStates.Count;

	internal int GetTrackedBodyCount(World world)
	{
		return _worldStates.TryGetValue(world, out var state) ? state.BodiesByEntity.Count : 0;
	}

	internal bool TryGetBodyMotionType(World world, Entity entity, out MotionType motionType)
	{
		if (_worldStates.TryGetValue(world, out var state) &&
		    state.BodiesByEntity.TryGetValue(entity, out var bodyState))
		{
			motionType = state.BodyInterface.GetMotionType(bodyState.BodyId);
			return true;
		}

		motionType = MotionType.Static;
		return false;
	}

	private PhysicsWorldState GetOrCreateWorldState(World world)
	{
		if (_worldStates.TryGetValue(world, out var existingState))
		{
			return existingState;
		}

		var broadPhaseLayerInterface = new BroadPhaseLayerInterfaceTable(ObjectLayerCount, BroadPhaseLayerCount);
		var objectLayerPairFilter = new ObjectLayerPairFilterTable(ObjectLayerCount);
		var staticObjectLayer = new ObjectLayer(StaticObjectLayerValue);
		var dynamicObjectLayer = new ObjectLayer(DynamicObjectLayerValue);
		objectLayerPairFilter.EnableCollision(staticObjectLayer, dynamicObjectLayer);
		objectLayerPairFilter.EnableCollision(dynamicObjectLayer, dynamicObjectLayer);

		broadPhaseLayerInterface.MapObjectToBroadPhaseLayer(staticObjectLayer, new BroadPhaseLayer(0));
		broadPhaseLayerInterface.MapObjectToBroadPhaseLayer(dynamicObjectLayer, new BroadPhaseLayer(1));

		var objectVsBroadPhaseLayerFilter = new ObjectVsBroadPhaseLayerFilterTable(
			broadPhaseLayerInterface,
			BroadPhaseLayerCount,
			objectLayerPairFilter,
			ObjectLayerCount);

		var settings = new PhysicsSystemSettings
		{
			MaxBodies = 65536,
			MaxBodyPairs = 65536,
			MaxContactConstraints = 65536,
			NumBodyMutexes = 0,
			ObjectLayerPairFilter = objectLayerPairFilter,
			BroadPhaseLayerInterface = broadPhaseLayerInterface,
			ObjectVsBroadPhaseLayerFilter = objectVsBroadPhaseLayerFilter
		};

		var physicsSystem = new PhysicsSystem(settings)
		{
			Gravity = DefaultGravity
		};
		var jobSystem = new JobSystemThreadPool();
		var state = new PhysicsWorldState(
			physicsSystem,
			jobSystem,
			broadPhaseLayerInterface,
			objectLayerPairFilter,
			objectVsBroadPhaseLayerFilter);
		_worldStates.Add(world, state);
		return state;
	}

	private static void SynchronizeBodies(World world, PhysicsWorldState state)
	{
		var changed = false;
		var bodiesToRemove = new List<Entity>();
		foreach (var entry in state.BodiesByEntity)
		{
			if (world.IsAlive(entry.Key) == false || world.HasComponent<BoxCollider>(entry.Key) == false)
			{
				bodiesToRemove.Add(entry.Key);
				continue;
			}

			var currentDefinition = CreateDefinition(world, entry.Key);
			if (currentDefinition is null || currentDefinition.Value.Equals(entry.Value.Definition) == false)
			{
				bodiesToRemove.Add(entry.Key);
			}
		}

		for (var i = 0; i < bodiesToRemove.Count; i++)
		{
			RemoveBody(state, bodiesToRemove[i]);
			changed = true;
		}

		foreach (var entry in world.View<BoxCollider>())
		{
			var entity = entry.Entity;
			if (world.IsAlive(entity) == false)
			{
				continue;
			}

			if (state.BodiesByEntity.ContainsKey(entity))
			{
				continue;
			}

			var definition = CreateDefinition(world, entity);
			if (definition is null)
			{
				continue;
			}

			CreateBody(state, entity, definition.Value);
			changed = true;
		}

		if (changed)
		{
			state.PhysicsSystem.OptimizeBroadPhase();
		}
	}

	private static PhysicsBodyDefinition? CreateDefinition(World world, Entity entity)
	{
		if (world.IsAlive(entity) == false || world.HasComponent<BoxCollider>(entity) == false)
		{
			return null;
		}

		var collider = world.GetComponent<BoxCollider>(entity);
		var hasRigidbody = world.HasComponent<Rigidbody>(entity);
		var rigidbody = hasRigidbody ? world.GetComponent<Rigidbody>(entity) : CreateStaticFallback();

		var worldMatrix = ComputeWorldMatrix(world, entity);
		if (Matrix4x4.Decompose(worldMatrix, out var worldScale, out var worldRotation, out var worldPosition) == false)
		{
			worldScale = Vector3.One;
			worldRotation = Quaternion.Identity;
			worldPosition = Vector3.Zero;
		}

		worldScale = new Vector3(MathF.Abs(worldScale.X), MathF.Abs(worldScale.Y), MathF.Abs(worldScale.Z));
		var halfExtents = Vector3.Max(new Vector3(0.001f), Multiply(collider.HalfExtents, worldScale));
		var center = Multiply(collider.Center, worldScale);
		return new PhysicsBodyDefinition(
			halfExtents,
			center,
			worldPosition,
			Normalize(worldRotation),
			rigidbody.LinearVelocity,
			rigidbody.AngularVelocity,
			rigidbody.Mass,
			rigidbody.GravityFactor,
			rigidbody.StartActivated,
			rigidbody.AllowSleeping,
			rigidbody.UseManifoldReduction,
			rigidbody.IsSensor,
			GetMotionType(hasRigidbody, rigidbody));
	}

	private static void CreateBody(PhysicsWorldState state, Entity entity, PhysicsBodyDefinition definition)
	{
		var boxShape = new BoxShape(definition.HalfExtents);
		OffsetCenterOfMassShape? offsetShape = null;
		Shape shape = boxShape;
		if (definition.Center != Vector3.Zero)
		{
			offsetShape = new OffsetCenterOfMassShape(definition.Center, boxShape);
			shape = offsetShape;
		}

		using var bodySettings = new BodyCreationSettings(
			shape,
			definition.Position,
			definition.Rotation,
			definition.MotionType,
			GetObjectLayer(definition.MotionType));
		bodySettings.LinearVelocity = definition.LinearVelocity;
		bodySettings.AngularVelocity = definition.AngularVelocity;
		bodySettings.GravityFactor = definition.GravityFactor;
		bodySettings.AllowSleeping = definition.AllowSleeping;
		bodySettings.UseManifoldReduction = definition.UseManifoldReduction;
		bodySettings.IsSensor = definition.IsSensor;
		if (definition.MotionType != MotionType.Static)
		{
			bodySettings.OverrideMassProperties = OverrideMassProperties.CalculateInertia;
			bodySettings.MassPropertiesOverride = new MassProperties
			{
				Mass = MathF.Max(0.001f, definition.Mass)
			};
		}

		var bodyId = state.BodyInterface.CreateAndAddBody(
			bodySettings,
			definition.StartActivated ? Activation.Activate : Activation.DontActivate);
		state.BodiesByEntity.Add(entity, new PhysicsBodyState(bodyId, definition, boxShape, offsetShape));
	}

	private static void RemoveBody(PhysicsWorldState state, Entity entity)
	{
		if (state.BodiesByEntity.Remove(entity, out var bodyState))
		{
			state.BodyInterface.RemoveAndDestroyBody(bodyState.BodyId);
			bodyState.Dispose();
		}
	}

	private static void SyncDynamicBodiesBackToWorld(World world, PhysicsWorldState state)
	{
		foreach (var pair in state.BodiesByEntity)
		{
			if (pair.Value.Definition.MotionType != MotionType.Dynamic || world.HasComponent<LocalTransform>(pair.Key) == false)
			{
				continue;
			}

			var position = state.BodyInterface.GetPosition(pair.Value.BodyId);
			var rotation = Normalize(state.BodyInterface.GetRotation(pair.Value.BodyId));
			WriteWorldPose(world, pair.Key, position, rotation);

			if (world.HasComponent<Rigidbody>(pair.Key))
			{
				ref var rigidbody = ref world.GetComponent<Rigidbody>(pair.Key);
				rigidbody.LinearVelocity = state.BodyInterface.GetLinearVelocity(pair.Value.BodyId);
				rigidbody.AngularVelocity = state.BodyInterface.GetAngularVelocity(pair.Value.BodyId);
			}
		}
	}

	private static MotionType GetMotionType(bool hasRigidbody, Rigidbody rigidbody)
	{
		if (hasRigidbody == false)
		{
			return MotionType.Static;
		}

		return rigidbody.BodyType switch
		{
			RigidbodyBodyType.Static => MotionType.Static,
			RigidbodyBodyType.Kinematic => MotionType.Kinematic,
			_ => MotionType.Dynamic
		};
	}

	private static ObjectLayer GetObjectLayer(MotionType motionType)
	{
		return motionType == MotionType.Static
			? new ObjectLayer(StaticObjectLayerValue)
			: new ObjectLayer(DynamicObjectLayerValue);
	}

	private static Rigidbody CreateStaticFallback()
	{
		var rigidbody = Rigidbody.CreateDefault();
		rigidbody.BodyType = RigidbodyBodyType.Static;
		rigidbody.StartActivated = false;
		return rigidbody;
	}

	private static Matrix4x4 ComputeWorldMatrix(World world, Entity entity)
	{
		var localMatrix = world.HasComponent<LocalTransform>(entity)
			? world.GetComponent<LocalTransform>(entity).GetTransform()
			: Matrix4x4.Identity;
		if (world.HasComponent<Parent>(entity) == false)
		{
			return localMatrix;
		}

		var parent = world.GetComponent<Parent>(entity).Value;
		return localMatrix * ComputeWorldMatrix(world, parent);
	}

	private static Matrix4x4 ComputeParentWorldMatrix(World world, Entity entity)
	{
		if (world.HasComponent<Parent>(entity) == false)
		{
			return Matrix4x4.Identity;
		}

		return ComputeWorldMatrix(world, world.GetComponent<Parent>(entity).Value);
	}

	private static void WriteWorldPose(World world, Entity entity, Vector3 position, Quaternion rotation)
	{
		var parentWorld = ComputeParentWorldMatrix(world, entity);
		Matrix4x4.Invert(parentWorld, out var parentWorldInverse);
		var localTransform = world.GetComponent<LocalTransform>(entity);
		var worldMatrix = Matrix4x4.CreateScale(localTransform.LocalScale) *
		                  Matrix4x4.CreateFromQuaternion(rotation) *
		                  Matrix4x4.CreateTranslation(position);
		var localMatrix = worldMatrix * parentWorldInverse;
		if (Matrix4x4.Decompose(localMatrix, out var localScale, out var localRotation, out var localPosition) == false)
		{
			return;
		}

		world.SetLocalPosition(entity, localPosition);
		world.SetLocalRotation(entity, Normalize(localRotation));
		world.SetLocalScale(entity, localScale);
	}

	private static Vector3 Multiply(Vector3 left, Vector3 right)
	{
		return new Vector3(left.X * right.X, left.Y * right.Y, left.Z * right.Z);
	}

	private static Quaternion Normalize(Quaternion rotation)
	{
		return rotation.LengthSquared() > 0.0f ? Quaternion.Normalize(rotation) : Quaternion.Identity;
	}

	private static void AcquireFoundation()
	{
		lock (FoundationLock)
		{
			if (_foundationHandlersConfigured == false)
			{
				Foundation.SetTraceHandler(Console.WriteLine);
#if DEBUG
				Foundation.SetAssertFailureHandler((expression, message, file, line) =>
				{
					var failureMessage = message ?? expression ?? "Unknown Jolt assertion failure.";
					throw new InvalidOperationException($"[JoltPhysics] Assertion failure at {file}:{line}: {failureMessage}");
				});
#endif
				_foundationHandlersConfigured = true;
			}

			if (_foundationReferenceCount == 0 && Foundation.Init() == false)
			{
				throw new InvalidOperationException("Failed to initialize the Jolt physics foundation.");
			}

			_foundationReferenceCount++;
		}
	}

	private static void ReleaseFoundation()
	{
		lock (FoundationLock)
		{
			if (_foundationReferenceCount == 0)
			{
				return;
			}

			_foundationReferenceCount--;
			if (_foundationReferenceCount == 0)
			{
				Foundation.Shutdown();
			}
		}
	}

	private sealed class PhysicsWorldState : IDisposable
	{
		public PhysicsWorldState(
			PhysicsSystem physicsSystem,
			JobSystemThreadPool jobSystem,
			BroadPhaseLayerInterfaceTable broadPhaseLayerInterface,
			ObjectLayerPairFilterTable objectLayerPairFilter,
			ObjectVsBroadPhaseLayerFilterTable objectVsBroadPhaseLayerFilter)
		{
			PhysicsSystem = physicsSystem;
			JobSystem = jobSystem;
			BroadPhaseLayerInterface = broadPhaseLayerInterface;
			ObjectLayerPairFilter = objectLayerPairFilter;
			ObjectVsBroadPhaseLayerFilter = objectVsBroadPhaseLayerFilter;
			BodyInterface = physicsSystem.BodyInterface;
		}

		public PhysicsSystem PhysicsSystem { get; }
		public JobSystemThreadPool JobSystem { get; }
		public BroadPhaseLayerInterfaceTable BroadPhaseLayerInterface { get; }
		public ObjectLayerPairFilterTable ObjectLayerPairFilter { get; }
		public ObjectVsBroadPhaseLayerFilterTable ObjectVsBroadPhaseLayerFilter { get; }
		public BodyInterface BodyInterface { get; }
		public Dictionary<Entity, PhysicsBodyState> BodiesByEntity { get; } = new();

		public void Dispose()
		{
			foreach (var bodyState in BodiesByEntity.Values)
			{
				BodyInterface.RemoveAndDestroyBody(bodyState.BodyId);
				bodyState.Dispose();
			}

			BodiesByEntity.Clear();
			PhysicsSystem.Dispose();
			JobSystem.Dispose();
			ObjectVsBroadPhaseLayerFilter.Dispose();
			ObjectLayerPairFilter.Dispose();
			BroadPhaseLayerInterface.Dispose();
		}
	}

	private sealed class PhysicsBodyState : IDisposable
	{
		public PhysicsBodyState(
			BodyID bodyId,
			PhysicsBodyDefinition definition,
			BoxShape boxShape,
			OffsetCenterOfMassShape? offsetShape)
		{
			BodyId = bodyId;
			Definition = definition;
			BoxShape = boxShape;
			OffsetShape = offsetShape;
		}

		public BodyID BodyId { get; }
		public PhysicsBodyDefinition Definition { get; }
		public BoxShape BoxShape { get; }
		public OffsetCenterOfMassShape? OffsetShape { get; }

		public void Dispose()
		{
			OffsetShape?.Dispose();
			BoxShape.Dispose();
		}
	}

	private readonly record struct PhysicsBodyDefinition(
		Vector3 HalfExtents,
		Vector3 Center,
		Vector3 Position,
		Quaternion Rotation,
		Vector3 LinearVelocity,
		Vector3 AngularVelocity,
		float Mass,
		float GravityFactor,
		bool StartActivated,
		bool AllowSleeping,
		bool UseManifoldReduction,
		bool IsSensor,
		MotionType MotionType);
}

using System;
using System.Collections.Generic;
using System.Numerics;
using JoltPhysicsSharp;
using WolfEngine;
using WolfEngine.ECS;
using WolfEngine.Profiling;

namespace WolfEngine.Physics;

public sealed class RigidbodySystem : IPhysicsUpdate, IWorldRemovedListener, IDisposable
{
	private const int CollisionSteps = 1;
	private static readonly Vector3 QueryShapeScale = Vector3.One;
	private readonly PhysicsQueryBroadPhaseLayerFilter _broadPhaseLayerFilter = new();
	private readonly PhysicsQueryShapeFilter _shapeFilter = new();
	private bool _disposed;

	public RigidbodySystem()
	{
		PhysicsWorldRegistry.AcquireOwner();
	}

	public WorldTag GetTag() => WorldTag.Game;

	public void PhysicsUpdate(float fixedDeltaTime, World world)
	{
		ArgumentNullException.ThrowIfNull(world);

		using (FrameProfiler.Instance.Measure("Physics.Update"))
		{
			var state = PhysicsWorldRegistry.GetOrCreateWorldState(world);
			state.ContactEvents.Clear();
			SynchronizeBodies(world, state, fixedDeltaTime);

			using (FrameProfiler.Instance.Measure("Physics.Step"))
			{
				var updateError = state.PhysicsSystem.Update(fixedDeltaTime, CollisionSteps, state.JobSystem);
				if (updateError != PhysicsUpdateError.None)
				{
					throw new InvalidOperationException($"Jolt physics update failed: {updateError}.");
				}
			}

			SyncDynamicBodiesBackToWorld(world, state);
			VehicleSystem.SyncVehicleVisuals(world, state);
		}
	}

	public void OnWorldRemoved(World world)
	{
		if (world is null)
		{
			return;
		}

		PhysicsWorldRegistry.RemoveWorld(world);
	}

	public bool TryMoveKinematicBody(
		World world,
		Entity entity,
		Vector3 worldPosition,
		Quaternion worldRotation,
		float fixedDeltaTime)
	{
		ArgumentNullException.ThrowIfNull(world);
		if (PhysicsWorldRegistry.TryGetWorldState(world, out var state) == false ||
		    state.BodiesByEntity.TryGetValue(entity, out var bodyState) == false ||
		    bodyState.Definition.MotionType != MotionType.Kinematic)
		{
			return false;
		}

		worldRotation = Normalize(worldRotation);
		state.BodyInterface.MoveKinematic(bodyState.BodyId, worldPosition, worldRotation, fixedDeltaTime);
		world.ApplyPhysicsWorldPose(entity, worldPosition, worldRotation);

		var updatedDefinition = bodyState.Definition with
		{
			Position = worldPosition,
			Rotation = worldRotation
		};
		bodyState.Definition = updatedDefinition;
		return true;
	}

	public bool TryRaycast(
		World world,
		Vector3 origin,
		Vector3 direction,
		out PhysicsRaycastHit hit,
		uint layerMask = uint.MaxValue,
		Entity ignoredEntity = default)
	{
		ArgumentNullException.ThrowIfNull(world);
		hit = default;

		if (PhysicsWorldRegistry.TryGetWorldState(world, out var state) == false || direction.LengthSquared() <= 0.0f)
		{
			return false;
		}

		using (FrameProfiler.Instance.Measure("Physics.Query.Raycast"))
		{
			using var objectLayerFilter = new PhysicsQueryObjectLayerFilter(layerMask);
			using var bodyFilter = new PhysicsQueryBodyFilter(GetIgnoredBodyId(state, ignoredEntity));
			var ray = new Ray(origin, direction);
			if (state.PhysicsSystem.NarrowPhaseQuery.CastRay(
				    ray,
				    out var rayHit,
				    _broadPhaseLayerFilter,
				    objectLayerFilter,
				    bodyFilter) == false)
			{
				return false;
			}

			if (TryCreateBodyQueryHit(state, rayHit.BodyID, out var entity, out var isSensor, out var layer) == false)
			{
				return false;
			}

			var point = origin + direction * rayHit.Fraction;
			var bodyShape = state.BodyInterface.GetTransformedShape(state.PhysicsSystem.BodyLockInterfaceNoLock, rayHit.BodyID);
			var subShapeId = new SubShapeID(rayHit.subShapeID2);
			var normal = NormalizeDirection(bodyShape.GetWorldSpaceSurfaceNormal(subShapeId, point));
			hit = new PhysicsRaycastHit(entity, point, normal, rayHit.Fraction, isSensor, layer);
			return true;
		}
	}

	public bool TryCastCapsule(
		World world,
		Vector3 position,
		Quaternion rotation,
		CapsuleCollider capsule,
		Vector3 direction,
		out PhysicsShapeCastHit hit,
		uint layerMask = uint.MaxValue,
		Entity ignoredEntity = default)
	{
		ArgumentNullException.ThrowIfNull(world);
		hit = default;

		if (PhysicsWorldRegistry.TryGetWorldState(world, out var state) == false || direction.LengthSquared() <= 0.0f)
		{
			return false;
		}

		using (FrameProfiler.Instance.Measure("Physics.Query.CastCapsule"))
		{
			var queryShapeDefinition = CreateCapsuleShapeDefinition(capsule, Vector3.One) with { Center = Vector3.Zero };
			var shapeHandle = CreateShape(queryShapeDefinition);
			try
			{
				var normalizedRotation = Normalize(rotation);
				var centerOffset = Vector3.Transform(capsule.Center, normalizedRotation);
				var shapeTransform = Matrix4x4.CreateFromQuaternion(normalizedRotation) *
				                     Matrix4x4.CreateTranslation(position + centerOffset);
				using var objectLayerFilter = new PhysicsQueryObjectLayerFilter(layerMask);
				using var bodyFilter = new PhysicsQueryBodyFilter(GetIgnoredBodyId(state, ignoredEntity));
				var hits = new List<ShapeCastResult>(capacity: 8);
				if (state.PhysicsSystem.NarrowPhaseQuery.CastShape(
					    shapeHandle.Shape,
					    shapeTransform,
					    QueryShapeScale,
					    direction,
					    CollisionCollectorType.ClosestHit,
					    hits,
					    _broadPhaseLayerFilter,
					    objectLayerFilter,
					    bodyFilter,
					    _shapeFilter) == false ||
				    hits.Count == 0)
				{
					return false;
				}

				var shapeHit = hits[0];
				if (TryCreateBodyQueryHit(state, shapeHit.BodyID2, out var entity, out var isSensor, out var layer) == false)
				{
					return false;
				}

				hit = new PhysicsShapeCastHit(
					entity,
					shapeHit.ContactPointOn2,
					-NormalizeDirection(shapeHit.PenetrationAxis),
					Math.Clamp(shapeHit.Fraction / direction.Length(), 0.0f, 1.0f),
					shapeHit.PenetrationDepth,
					isSensor,
					layer);
				return true;
			}
			finally
			{
				shapeHandle.Dispose();
			}
		}
	}

	public int OverlapCapsule(
		World world,
		Vector3 position,
		Quaternion rotation,
		CapsuleCollider capsule,
		ICollection<PhysicsOverlapHit> hits,
		uint layerMask = uint.MaxValue,
		Entity ignoredEntity = default)
	{
		ArgumentNullException.ThrowIfNull(world);
		ArgumentNullException.ThrowIfNull(hits);
		hits.Clear();

		if (PhysicsWorldRegistry.TryGetWorldState(world, out var state) == false)
		{
			return 0;
		}

		using (FrameProfiler.Instance.Measure("Physics.Query.OverlapCapsule"))
		{
			var queryShapeDefinition = CreateCapsuleShapeDefinition(capsule, Vector3.One) with { Center = Vector3.Zero };
			var shapeHandle = CreateShape(queryShapeDefinition);
			try
			{
				var normalizedRotation = Normalize(rotation);
				var centerOffset = Vector3.Transform(capsule.Center, normalizedRotation);
				var shapeTransform = Matrix4x4.CreateFromQuaternion(normalizedRotation) *
				                     Matrix4x4.CreateTranslation(position + centerOffset);
				var baseOffset = Vector3.Zero;
				using var objectLayerFilter = new PhysicsQueryObjectLayerFilter(layerMask);
				using var bodyFilter = new PhysicsQueryBodyFilter(GetIgnoredBodyId(state, ignoredEntity));
				var overlapHits = new List<CollideShapeResult>(capacity: 8);
				if (state.PhysicsSystem.NarrowPhaseQuery.CollideShape(
					    shapeHandle.Shape,
					    baseOffset,
					    shapeTransform,
					    baseOffset,
					    CollisionCollectorType.AllHit,
					    overlapHits,
					    _broadPhaseLayerFilter,
					    objectLayerFilter,
					    bodyFilter,
					    _shapeFilter) == false)
				{
					return 0;
				}

				for (var i = 0; i < overlapHits.Count; i++)
				{
					var overlapHit = overlapHits[i];
					if (TryCreateBodyQueryHit(state, overlapHit.BodyID2, out var entity, out var isSensor, out var layer) == false)
					{
						continue;
					}

					hits.Add(new PhysicsOverlapHit(
						entity,
						overlapHit.ContactPointOn2,
						-NormalizeDirection(overlapHit.PenetrationAxis),
						overlapHit.PenetrationDepth,
						isSensor,
						layer));
				}

				return hits.Count;
			}
			finally
			{
				shapeHandle.Dispose();
			}
		}
	}

	public IReadOnlyList<PhysicsContactEvent> GetContactEvents(World world)
	{
		ArgumentNullException.ThrowIfNull(world);
		return PhysicsWorldRegistry.TryGetWorldState(world, out var state) ? state.ContactEvents : Array.Empty<PhysicsContactEvent>();
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_broadPhaseLayerFilter.Dispose();
		_shapeFilter.Dispose();
		PhysicsWorldRegistry.ReleaseOwner();
		_disposed = true;
	}

	internal int GetTrackedWorldCount() => PhysicsWorldRegistry.TryGetWorldStateCount(out var count) ? count : 0;

	internal int GetTrackedBodyCount(World world)
	{
		return PhysicsWorldRegistry.TryGetWorldState(world, out var state) ? state.BodiesByEntity.Count : 0;
	}

	internal bool TryGetBodyMotionType(World world, Entity entity, out MotionType motionType)
	{
		if (PhysicsWorldRegistry.TryGetWorldState(world, out var state) &&
		    state.BodiesByEntity.TryGetValue(entity, out var bodyState))
		{
			motionType = state.BodyInterface.GetMotionType(bodyState.BodyId);
			return true;
		}

		motionType = MotionType.Static;
		return false;
	}

	internal bool TryGetTrackedBodyId(World world, Entity entity, out BodyID bodyId)
	{
		if (PhysicsWorldRegistry.TryGetWorldState(world, out var state) &&
		    state.BodiesByEntity.TryGetValue(entity, out var bodyState))
		{
			bodyId = bodyState.BodyId;
			return true;
		}

		bodyId = BodyID.Invalid;
		return false;
	}

	private static void SynchronizeBodies(World world, PhysicsWorldState state, float fixedDeltaTime)
	{
		using (FrameProfiler.Instance.Measure("Physics.SyncBodies"))
		{
			var changed = false;
			var bodiesToRemove = new List<Entity>();
			var totalColliderCount = world.GetComponentCount<BoxCollider>() +
			                         world.GetComponentCount<CapsuleCollider>() +
			                         world.GetComponentCount<MeshCollider>();

			using (FrameProfiler.Instance.Measure("Physics.SyncBodies.ApplyChanges"))
			{
				foreach (var entry in state.BodiesByEntity)
				{
					var entity = entry.Key;
					var bodyState = entry.Value;
					if (bodyState.Owner != PhysicsBodyOwner.Rigidbody)
					{
						continue;
					}

					var currentDefinition = CreateDefinition(world, entity, bodyState.Definition);
					if (currentDefinition is null)
					{
						bodiesToRemove.Add(entity);
						continue;
					}

					if (RequiresBodyRecreation(bodyState.Definition, currentDefinition.Value))
					{
						bodiesToRemove.Add(entity);
						continue;
					}

					ApplyBodyChanges(state, bodyState, currentDefinition.Value, fixedDeltaTime);
				}
			}

			using (FrameProfiler.Instance.Measure("Physics.SyncBodies.Remove"))
			{
				for (var i = 0; i < bodiesToRemove.Count; i++)
				{
					RemoveBody(state, bodiesToRemove[i]);
					changed = true;
				}
			}

			using (FrameProfiler.Instance.Measure("Physics.SyncBodies.CreateBox"))
			{
				if (state.LastBoxColliderCount != world.GetComponentCount<BoxCollider>() ||
				    state.BodiesByEntity.Count < totalColliderCount)
				{
					CreateBodiesForView(world, state, world.View<BoxCollider>(), ref changed);
				}

				state.LastBoxColliderCount = world.GetComponentCount<BoxCollider>();
			}

			using (FrameProfiler.Instance.Measure("Physics.SyncBodies.CreateCapsule"))
			{
				if (state.LastCapsuleColliderCount != world.GetComponentCount<CapsuleCollider>() ||
				    state.BodiesByEntity.Count < totalColliderCount)
				{
					CreateBodiesForView(world, state, world.View<CapsuleCollider>(), ref changed);
				}

				state.LastCapsuleColliderCount = world.GetComponentCount<CapsuleCollider>();
			}

			using (FrameProfiler.Instance.Measure("Physics.SyncBodies.CreateMesh"))
			{
				if (state.LastMeshColliderCount != world.GetComponentCount<MeshCollider>() ||
				    state.BodiesByEntity.Count < totalColliderCount)
				{
					CreateBodiesForView(world, state, world.View<MeshCollider>(), ref changed);
				}

				state.LastMeshColliderCount = world.GetComponentCount<MeshCollider>();
			}

			if (changed)
			{
				using (FrameProfiler.Instance.Measure("Physics.SyncBodies.OptimizeBroadPhase"))
				{
					state.PhysicsSystem.OptimizeBroadPhase();
				}
			}
		}
	}

	private static void CreateBodiesForView<TCollider>(
		World world,
		PhysicsWorldState state,
		View<TCollider> view,
		ref bool changed)
		where TCollider : struct, IEntityComponent
	{
		foreach (var entry in view)
		{
			var entity = entry.Entity;
			if (world.IsAlive(entity) == false || state.BodiesByEntity.ContainsKey(entity))
			{
				continue;
			}

			var definition = CreateDefinition(world, entity);
			if (definition is null)
			{
				continue;
			}

			CreateBody(state, entity, definition.Value, PhysicsBodyOwner.Rigidbody);
			changed = true;
		}
	}

	private static PhysicsBodyDefinition? CreateDefinition(
		World world,
		Entity entity,
		PhysicsBodyDefinition? previousDefinition = null)
	{
		if (world.IsAlive(entity) == false || TryGetColliderKind(world, entity, out var colliderKind) == false)
		{
			return null;
		}

		if (world.HasComponent<Vehicle>(entity))
		{
			return null;
		}

		var hasRigidbody = world.HasComponent<Rigidbody>(entity);
		var rigidbody = hasRigidbody ? world.GetComponent<Rigidbody>(entity) : CreateStaticFallback();
		var collisionFilter = world.HasComponent<CollisionFilter>(entity)
			? world.GetComponent<CollisionFilter>(entity)
			: CollisionFilter.CreateDefault();
		var transformDirty = world.HasComponent<LocalTransform>(entity) && world.GetComponent<LocalTransform>(entity).IsDirty;
		var rigidbodyChanged = HasRigidbodyChanged(previousDefinition, hasRigidbody, rigidbody);
		var shapeChanged = HasShapeChanged(world, entity, colliderKind);
		var collisionFilterChanged = HasCollisionFilterChanged(world, entity, colliderKind, collisionFilter);
		if (previousDefinition is { } cachedDefinition &&
		    transformDirty == false &&
		    rigidbodyChanged == false &&
		    shapeChanged == false &&
		    collisionFilterChanged == false)
		{
			return cachedDefinition;
		}

		var worldPosition = Vector3.Zero;
		var worldRotation = Quaternion.Identity;
		var worldScale = Vector3.One;
		world.TryGetWorldPoseAndScale(entity, out worldPosition, out worldRotation, out worldScale);

		if (colliderKind == PhysicsColliderKind.Mesh &&
		    IsMeshColliderBodySupported(hasRigidbody, rigidbody) == false)
		{
			return null;
		}

		if (TryCreateShapeDefinition(world, entity, colliderKind, worldScale, collisionFilter, out var shapeDefinition) == false)
		{
			return null;
		}

		CacheRigidbodyState(world, entity, hasRigidbody, rigidbody);

		return new PhysicsBodyDefinition(
			shapeDefinition,
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
			ClampLayer(collisionFilter.Layer),
			collisionFilter.CollidesWith,
			GetMotionType(hasRigidbody, rigidbody));
	}

	private static bool TryCreateShapeDefinition(
		World world,
		Entity entity,
		PhysicsColliderKind colliderKind,
		Vector3 worldScale,
		CollisionFilter collisionFilter,
		out PhysicsShapeDefinition shapeDefinition)
	{
		if (colliderKind == PhysicsColliderKind.Box)
		{
			ref var collider = ref world.GetComponent<BoxCollider>(entity);
			UpdateBoxColliderCache(ref collider, worldScale, collisionFilter);
			shapeDefinition = PhysicsShapeDefinition.CreateBox(collider.CachedScaledHalfExtents, collider.CachedScaledCenter);
			return true;
		}

		if (colliderKind == PhysicsColliderKind.Capsule)
		{
			ref var collider = ref world.GetComponent<CapsuleCollider>(entity);
			UpdateCapsuleColliderCache(ref collider, worldScale, collisionFilter);
			shapeDefinition = PhysicsShapeDefinition.CreateCapsule(collider.CachedScaledHalfHeight, collider.CachedScaledRadius, collider.CachedScaledCenter);
			return true;
		}

		if (colliderKind == PhysicsColliderKind.Mesh)
		{
			ref var collider = ref world.GetComponent<MeshCollider>(entity);
			if (collider.TryValidate() == false)
			{
				shapeDefinition = default;
				return false;
			}

			UpdateMeshColliderCache(ref collider, collisionFilter);
			shapeDefinition = PhysicsShapeDefinition.CreateMesh(collider.Mesh!, SanitizeMeshScale(worldScale));
			return true;
		}

		shapeDefinition = default;
		return false;
	}

	private static PhysicsShapeDefinition CreateBoxShapeDefinition(BoxCollider collider, Vector3 worldScale)
	{
		var halfExtents = Vector3.Max(new Vector3(0.001f), Multiply(collider.HalfExtents, worldScale));
		var center = Multiply(collider.Center, worldScale);
		return PhysicsShapeDefinition.CreateBox(halfExtents, center);
	}

	private static PhysicsShapeDefinition CreateCapsuleShapeDefinition(CapsuleCollider collider, Vector3 worldScale)
	{
		var halfHeight = MathF.Max(0.001f, MathF.Abs(collider.HalfHeight * worldScale.Y));
		var radiusScale = MathF.Max(MathF.Abs(worldScale.X), MathF.Abs(worldScale.Z));
		var radius = MathF.Max(0.001f, MathF.Abs(collider.Radius * radiusScale));
		var center = Multiply(collider.Center, worldScale);
		return PhysicsShapeDefinition.CreateCapsule(halfHeight, radius, center);
	}

	internal static void CreateBody(PhysicsWorldState state, Entity entity, PhysicsBodyDefinition definition, PhysicsBodyOwner owner)
	{
		var shapeHandle = CreateShape(definition.Shape);
		using var bodySettings = new BodyCreationSettings(
			shapeHandle.Shape,
			definition.Position,
			definition.Rotation,
			definition.MotionType,
			new ObjectLayer(definition.Layer));
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
		var bodyState = new PhysicsBodyState(entity, bodyId, definition, shapeHandle.BaseShape, shapeHandle.TranslatedShape, owner);
		state.BodiesByEntity.Add(entity, bodyState);
		state.BodiesByBodyId.Add(bodyId, bodyState);
	}

	internal static void RemoveBody(PhysicsWorldState state, Entity entity)
	{
		if (state.BodiesByEntity.Remove(entity, out var bodyState))
		{
			state.BodiesByBodyId.Remove(bodyState.BodyId);
			state.BodyInterface.RemoveAndDestroyBody(bodyState.BodyId);
			bodyState.Dispose();
		}
	}

	private static void ApplyBodyChanges(
		PhysicsWorldState state,
		PhysicsBodyState bodyState,
		PhysicsBodyDefinition currentDefinition,
		float fixedDeltaTime)
	{
		var previousDefinition = bodyState.Definition;
		var bodyId = bodyState.BodyId;
		var activation = currentDefinition.StartActivated ? Activation.Activate : Activation.DontActivate;

		if (previousDefinition.Position != currentDefinition.Position ||
		    previousDefinition.Rotation != currentDefinition.Rotation)
		{
			if (currentDefinition.MotionType == MotionType.Kinematic && fixedDeltaTime > 0.0f)
			{
				state.BodyInterface.MoveKinematic(
					bodyId,
					currentDefinition.Position,
					currentDefinition.Rotation,
					fixedDeltaTime);
			}
			else
			{
				state.BodyInterface.SetPositionAndRotationWhenChanged(
					bodyId,
					currentDefinition.Position,
					currentDefinition.Rotation,
					activation);
			}
		}

		if (previousDefinition.LinearVelocity != currentDefinition.LinearVelocity ||
		    previousDefinition.AngularVelocity != currentDefinition.AngularVelocity)
		{
			state.BodyInterface.SetLinearAndAngularVelocity(
				bodyId,
				currentDefinition.LinearVelocity,
				currentDefinition.AngularVelocity);
		}

		if (MathF.Abs(previousDefinition.GravityFactor - currentDefinition.GravityFactor) > 0.0001f)
		{
			state.BodyInterface.SetGravityFactor(bodyId, currentDefinition.GravityFactor);
		}

		if (previousDefinition.AllowSleeping != currentDefinition.AllowSleeping)
		{
			if (currentDefinition.AllowSleeping == false)
			{
				state.BodyInterface.ResetSleepTimer(bodyId);
				state.BodyInterface.ActivateBody(bodyId);
			}
		}

		if (previousDefinition.UseManifoldReduction != currentDefinition.UseManifoldReduction)
		{
			state.BodyInterface.SetUseManifoldReduction(bodyId, currentDefinition.UseManifoldReduction);
		}

		if (previousDefinition.IsSensor != currentDefinition.IsSensor)
		{
			state.BodyInterface.SetIsSensor(bodyId, currentDefinition.IsSensor);
		}

		if (previousDefinition.Layer != currentDefinition.Layer)
		{
			state.BodyInterface.SetObjectLayer(bodyId, new ObjectLayer(currentDefinition.Layer));
		}

		if (previousDefinition.StartActivated != currentDefinition.StartActivated)
		{
			if (currentDefinition.StartActivated)
			{
				state.BodyInterface.ActivateBody(bodyId);
			}
			else
			{
				state.BodyInterface.DeactivateBody(bodyId);
			}
		}

		bodyState.Definition = currentDefinition;
	}

	private static void SyncDynamicBodiesBackToWorld(World world, PhysicsWorldState state)
	{
		using (FrameProfiler.Instance.Measure("Physics.SyncBack"))
		{
			var changedPoses = new List<PhysicsWorldPoseSyncItem>();
			foreach (var pair in state.BodiesByEntity)
			{
				var bodyState = pair.Value;
				if (bodyState.Definition.MotionType != MotionType.Dynamic || world.HasComponent<LocalTransform>(pair.Key) == false)
				{
					continue;
				}

				Vector3 position;
				Quaternion rotation;
				using (FrameProfiler.Instance.Measure("Physics.SyncBack.ReadPose"))
				{
					position = state.BodyInterface.GetPosition(bodyState.BodyId);
					rotation = Normalize(state.BodyInterface.GetRotation(bodyState.BodyId));
				}

				var worldPosition = position;
				if (bodyState.Owner == PhysicsBodyOwner.Vehicle &&
				    state.VehiclesByEntity.TryGetValue(pair.Key, out var vehicleState))
				{
					worldPosition = vehicleState.Definition.GetEntityPosition(position, rotation);
				}

				if (HasWorldPoseChanged(bodyState.Definition.Position, bodyState.Definition.Rotation, position, rotation))
				{
					changedPoses.Add(new PhysicsWorldPoseSyncItem(pair.Key, worldPosition, rotation));
				}

				var linearVelocity = bodyState.Definition.LinearVelocity;
				var angularVelocity = bodyState.Definition.AngularVelocity;
				if (world.HasComponent<Rigidbody>(pair.Key))
				{
					using (FrameProfiler.Instance.Measure("Physics.SyncBack.ReadVelocity"))
					{
						ref var rigidbody = ref world.GetComponent<Rigidbody>(pair.Key);
						rigidbody.LinearVelocity = state.BodyInterface.GetLinearVelocity(bodyState.BodyId);
						rigidbody.AngularVelocity = state.BodyInterface.GetAngularVelocity(bodyState.BodyId);
						linearVelocity = rigidbody.LinearVelocity;
						angularVelocity = rigidbody.AngularVelocity;
						CacheRigidbodyState(ref rigidbody);
					}
				}

				bodyState.Definition = bodyState.Definition with
				{
					Position = position,
					Rotation = rotation,
					LinearVelocity = linearVelocity,
					AngularVelocity = angularVelocity
				};
			}

			if (changedPoses.Count > 0)
			{
				using (FrameProfiler.Instance.Measure("Physics.SyncBack.WritePose"))
				{
					world.ApplyPhysicsWorldPoses(changedPoses);
				}
			}
		}
	}

	private static bool TryCreateBodyQueryHit(
		PhysicsWorldState state,
		BodyID bodyId,
		out Entity entity,
		out bool isSensor,
		out uint layer)
	{
		if (state.BodiesByBodyId.TryGetValue(bodyId, out var bodyState))
		{
			entity = bodyState.Entity;
			isSensor = bodyState.Definition.IsSensor;
			layer = bodyState.Definition.Layer;
			return true;
		}

		entity = default;
		isSensor = false;
		layer = CollisionFilter.DefaultLayer;
		return false;
	}

	private static BodyID GetIgnoredBodyId(PhysicsWorldState state, Entity ignoredEntity)
	{
		if (ignoredEntity.IsValid &&
		    state.BodiesByEntity.TryGetValue(ignoredEntity, out var ignoredBodyState))
		{
			return ignoredBodyState.BodyId;
		}

		return BodyID.Invalid;
	}

	private static bool RequiresBodyRecreation(
		PhysicsBodyDefinition previousDefinition,
		PhysicsBodyDefinition currentDefinition)
	{
		return previousDefinition.MotionType != currentDefinition.MotionType ||
		       previousDefinition.Shape != currentDefinition.Shape;
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

	private static Rigidbody CreateStaticFallback()
	{
		var rigidbody = Rigidbody.CreateDefault();
		rigidbody.BodyType = RigidbodyBodyType.Static;
		rigidbody.StartActivated = false;
		return rigidbody;
	}

	private static bool TryGetColliderKind(World world, Entity entity, out PhysicsColliderKind colliderKind)
	{
		if (world.HasComponent<BoxCollider>(entity))
		{
			colliderKind = PhysicsColliderKind.Box;
			return true;
		}

		if (world.HasComponent<CapsuleCollider>(entity))
		{
			colliderKind = PhysicsColliderKind.Capsule;
			return true;
		}

		if (world.HasComponent<MeshCollider>(entity))
		{
			colliderKind = PhysicsColliderKind.Mesh;
			return true;
		}

		colliderKind = default;
		return false;
	}

	private static bool HasRigidbodyChanged(PhysicsBodyDefinition? previousDefinition, bool hasRigidbody, Rigidbody rigidbody)
	{
		if (hasRigidbody == false)
		{
			return previousDefinition is { MotionType: not MotionType.Static };
		}

		return rigidbody.PhysicsCacheValid == false ||
		       rigidbody.CachedBodyType != rigidbody.BodyType ||
		       MathF.Abs(rigidbody.CachedMass - rigidbody.Mass) > 0.0001f ||
		       rigidbody.CachedLinearVelocity != rigidbody.LinearVelocity ||
		       rigidbody.CachedAngularVelocity != rigidbody.AngularVelocity ||
		       MathF.Abs(rigidbody.CachedGravityFactor - rigidbody.GravityFactor) > 0.0001f ||
		       rigidbody.CachedStartActivated != rigidbody.StartActivated ||
		       rigidbody.CachedAllowSleeping != rigidbody.AllowSleeping ||
		       rigidbody.CachedUseManifoldReduction != rigidbody.UseManifoldReduction ||
		       rigidbody.CachedIsSensor != rigidbody.IsSensor;
	}

	private static bool HasShapeChanged(World world, Entity entity, PhysicsColliderKind colliderKind)
	{
		if (colliderKind == PhysicsColliderKind.Box)
		{
			ref var collider = ref world.GetComponent<BoxCollider>(entity);
			return collider.PhysicsCacheValid == false ||
			       collider.CachedHalfExtents != collider.HalfExtents ||
			       collider.CachedCenter != collider.Center;
		}

		if (colliderKind == PhysicsColliderKind.Mesh)
		{
			ref var collider = ref world.GetComponent<MeshCollider>(entity);
			return collider.PhysicsCacheValid == false ||
			       collider.CachedMeshAssetId != collider.MeshAsset.NodeId ||
			       ReferenceEquals(collider.CachedMesh, collider.Mesh) == false;
		}

		ref var capsule = ref world.GetComponent<CapsuleCollider>(entity);
		return capsule.PhysicsCacheValid == false ||
		       MathF.Abs(capsule.CachedRadius - capsule.Radius) > 0.0001f ||
		       MathF.Abs(capsule.CachedHalfHeight - capsule.HalfHeight) > 0.0001f ||
		       capsule.CachedCenter != capsule.Center;
	}

	private static PhysicsShapeHandle CreateShape(PhysicsShapeDefinition definition)
	{
		Shape baseShape = definition.Kind switch
		{
			PhysicsColliderKind.Box => new BoxShape(definition.BoxHalfExtents),
			PhysicsColliderKind.Capsule => new CapsuleShape(definition.CapsuleHalfHeight, definition.CapsuleRadius),
			PhysicsColliderKind.Mesh => CreateMeshShape(definition.Mesh!, definition.MeshScale),
			_ => throw new InvalidOperationException($"Unsupported physics shape kind '{definition.Kind}'.")
		};

		if (definition.Center == Vector3.Zero)
		{
			return new PhysicsShapeHandle(baseShape, null, baseShape);
		}

		var translatedShape = new RotatedTranslatedShape(definition.Center, Quaternion.Identity, baseShape);
		return new PhysicsShapeHandle(baseShape, translatedShape, translatedShape);
	}

	private static Vector3 Multiply(Vector3 left, Vector3 right)
	{
		return new Vector3(left.X * right.X, left.Y * right.Y, left.Z * right.Z);
	}

	private static Quaternion Normalize(Quaternion rotation)
	{
		return rotation.LengthSquared() > 0.0f ? Quaternion.Normalize(rotation) : Quaternion.Identity;
	}

	private static Vector3 NormalizeDirection(Vector3 value)
	{
		return value.LengthSquared() > 0.0f ? Vector3.Normalize(value) : Vector3.UnitY;
	}

	private static uint ClampLayer(uint layer)
	{
		return layer <= CollisionFilter.MaxLayer ? layer : CollisionFilter.MaxLayer;
	}

	private static bool HasCollisionFilterChanged(
		World world,
		Entity entity,
		PhysicsColliderKind colliderKind,
		CollisionFilter collisionFilter)
	{
		var layer = ClampLayer(collisionFilter.Layer);
		return colliderKind switch
		{
			PhysicsColliderKind.Box => world.GetComponent<BoxCollider>(entity).PhysicsCacheValid == false ||
			                           world.GetComponent<BoxCollider>(entity).CachedLayer != layer ||
			                           world.GetComponent<BoxCollider>(entity).CachedCollidesWith != collisionFilter.CollidesWith,
			PhysicsColliderKind.Capsule => world.GetComponent<CapsuleCollider>(entity).PhysicsCacheValid == false ||
			                               world.GetComponent<CapsuleCollider>(entity).CachedLayer != layer ||
			                               world.GetComponent<CapsuleCollider>(entity).CachedCollidesWith != collisionFilter.CollidesWith,
			PhysicsColliderKind.Mesh => world.GetComponent<MeshCollider>(entity).PhysicsCacheValid == false ||
			                            world.GetComponent<MeshCollider>(entity).CachedLayer != layer ||
			                            world.GetComponent<MeshCollider>(entity).CachedCollidesWith != collisionFilter.CollidesWith,
			_ => true
		};
	}

	private static void UpdateBoxColliderCache(ref BoxCollider collider, Vector3 worldScale, CollisionFilter collisionFilter)
	{
		collider.CachedHalfExtents = collider.HalfExtents;
		collider.CachedCenter = collider.Center;
		collider.CachedWorldScale = worldScale;
		collider.CachedScaledHalfExtents = Vector3.Max(new Vector3(0.001f), Multiply(collider.HalfExtents, worldScale));
		collider.CachedScaledCenter = Multiply(collider.Center, worldScale);
		collider.CachedLayer = ClampLayer(collisionFilter.Layer);
		collider.CachedCollidesWith = collisionFilter.CollidesWith;
		collider.PhysicsCacheValid = true;
	}

	private static void UpdateCapsuleColliderCache(ref CapsuleCollider collider, Vector3 worldScale, CollisionFilter collisionFilter)
	{
		collider.CachedRadius = collider.Radius;
		collider.CachedHalfHeight = collider.HalfHeight;
		collider.CachedCenter = collider.Center;
		collider.CachedWorldScale = worldScale;
		collider.CachedScaledHalfHeight = MathF.Max(0.001f, MathF.Abs(collider.HalfHeight * worldScale.Y));
		var radiusScale = MathF.Max(MathF.Abs(worldScale.X), MathF.Abs(worldScale.Z));
		collider.CachedScaledRadius = MathF.Max(0.001f, MathF.Abs(collider.Radius * radiusScale));
		collider.CachedScaledCenter = Multiply(collider.Center, worldScale);
		collider.CachedLayer = ClampLayer(collisionFilter.Layer);
		collider.CachedCollidesWith = collisionFilter.CollidesWith;
		collider.PhysicsCacheValid = true;
	}

	private static void UpdateMeshColliderCache(ref MeshCollider collider, CollisionFilter collisionFilter)
	{
		collider.CachedMeshAssetId = collider.MeshAsset.NodeId;
		collider.CachedMesh = collider.Mesh;
		collider.CachedLayer = ClampLayer(collisionFilter.Layer);
		collider.CachedCollidesWith = collisionFilter.CollidesWith;
		collider.PhysicsCacheValid = true;
	}

	private static void CacheRigidbodyState(World world, Entity entity, bool hasRigidbody, Rigidbody rigidbody)
	{
		if (hasRigidbody == false)
		{
			return;
		}

		ref var current = ref world.GetComponent<Rigidbody>(entity);
		current = rigidbody;
		CacheRigidbodyState(ref current);
	}

	private static void CacheRigidbodyState(ref Rigidbody rigidbody)
	{
		rigidbody.CachedBodyType = rigidbody.BodyType;
		rigidbody.CachedMass = rigidbody.Mass;
		rigidbody.CachedLinearVelocity = rigidbody.LinearVelocity;
		rigidbody.CachedAngularVelocity = rigidbody.AngularVelocity;
		rigidbody.CachedGravityFactor = rigidbody.GravityFactor;
		rigidbody.CachedStartActivated = rigidbody.StartActivated;
		rigidbody.CachedAllowSleeping = rigidbody.AllowSleeping;
		rigidbody.CachedUseManifoldReduction = rigidbody.UseManifoldReduction;
		rigidbody.CachedIsSensor = rigidbody.IsSensor;
		rigidbody.PhysicsCacheValid = true;
	}

	private static bool IsMeshColliderBodySupported(bool hasRigidbody, Rigidbody rigidbody)
	{
		return hasRigidbody == false || rigidbody.BodyType == RigidbodyBodyType.Static;
	}

	private static Shape CreateMeshShape(Mesh mesh, Vector3 worldScale)
	{
		var vertices = CreateMeshVertices(mesh, worldScale);
		var triangles = CreateMeshTriangles(mesh);
		using var settings = new MeshShapeSettings(vertices, triangles);
		settings.Sanitize();
		return new MeshShape(settings);
	}

	private static Vector3[] CreateMeshVertices(Mesh mesh, Vector3 worldScale)
	{
		var scale = SanitizeMeshScale(worldScale);
		var vertices = new Vector3[mesh.Vertices.Length];
		for (var i = 0; i < mesh.Vertices.Length; i++)
		{
			var vertex = mesh.Vertices[i];
			vertices[i] = new Vector3(vertex.X * scale.X, vertex.Y * scale.Y, vertex.Z * scale.Z);
		}

		return vertices;
	}

	private static IndexedTriangle[] CreateMeshTriangles(Mesh mesh)
	{
		var triangleCount = mesh.Indices.Length / 3;
		var triangles = new IndexedTriangle[triangleCount];
		for (var i = 0; i < triangleCount; i++)
		{
			var baseIndex = i * 3;
			triangles[i] = new IndexedTriangle(
				mesh.Indices[baseIndex],
				mesh.Indices[baseIndex + 1],
				mesh.Indices[baseIndex + 2]);
		}

		return triangles;
	}

	private static Vector3 SanitizeMeshScale(Vector3 scale)
	{
		return new Vector3(
			SanitizeMeshScaleComponent(scale.X),
			SanitizeMeshScaleComponent(scale.Y),
			SanitizeMeshScaleComponent(scale.Z));
	}

	private static float SanitizeMeshScaleComponent(float value)
	{
		if (MathF.Abs(value) >= 0.001f)
		{
			return value;
		}

		return value < 0.0f ? -0.001f : 0.001f;
	}

	private static bool HasWorldPoseChanged(
		Vector3 previousPosition,
		Quaternion previousRotation,
		Vector3 currentPosition,
		Quaternion currentRotation)
	{
		return Vector3.DistanceSquared(previousPosition, currentPosition) > 0.000001f ||
		       MathF.Abs(Quaternion.Dot(previousRotation, currentRotation)) < 0.999999f;
	}

	private sealed class PhysicsQueryBroadPhaseLayerFilter : BroadPhaseLayerFilter
	{
		protected override bool ShouldCollide(BroadPhaseLayer layer)
		{
			return true;
		}
	}

	private sealed class PhysicsQueryObjectLayerFilter : ObjectLayerFilter
	{
		private readonly uint _layerMask;

		public PhysicsQueryObjectLayerFilter(uint layerMask)
		{
			_layerMask = layerMask;
		}

		protected override bool ShouldCollide(ObjectLayer layer)
		{
			return (layer.Value <= CollisionFilter.MaxLayer) &&
			       ((_layerMask & (1u << (int)layer.Value)) != 0);
		}
	}

	private sealed class PhysicsQueryShapeFilter : ShapeFilter
	{
		protected override bool ShouldCollide(Shape shape2, in SubShapeID subShapeIdOfShape2)
		{
			return true;
		}

		protected override bool ShouldCollide(
			Shape shape1,
			in SubShapeID subShapeIdOfShape1,
			Shape shape2,
			in SubShapeID subShapeIdOfShape2)
		{
			return true;
		}
	}

	private sealed class PhysicsQueryBodyFilter : BodyFilter
	{
		private readonly BodyID _ignoredBodyId;

		public PhysicsQueryBodyFilter(BodyID ignoredBodyId)
		{
			_ignoredBodyId = ignoredBodyId;
		}

		protected override bool ShouldCollide(BodyID bodyId)
		{
			return _ignoredBodyId.IsInvalid || bodyId != _ignoredBodyId;
		}

		protected override bool ShouldCollideLocked(Body body)
		{
			return _ignoredBodyId.IsInvalid || body.ID != _ignoredBodyId;
		}
	}
}

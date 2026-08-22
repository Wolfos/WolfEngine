using System.Numerics;
using JoltPhysicsSharp;
using WolfEngine.ECS;

namespace WolfEngine.Physics;

internal static class PhysicsWorldRegistry
{
	private const uint PhysicsObjectLayerCount = CollisionFilter.MaxLayer + 1;
	private const uint BroadPhaseLayerCount = 1;

	private static readonly Lock SyncRoot = new();
	private static readonly Dictionary<World, PhysicsWorldState> WorldStates = new();
	private static int _ownerCount;
	private static bool _foundationHandlersConfigured;

	public static void AcquireOwner()
	{
		lock (SyncRoot)
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

			if (_ownerCount == 0 && Foundation.Init() == false)
			{
				throw new InvalidOperationException("Failed to initialize the Jolt physics foundation.");
			}

			_ownerCount++;
		}
	}

	public static void ReleaseOwner()
	{
		lock (SyncRoot)
		{
			if (_ownerCount == 0)
			{
				return;
			}

			_ownerCount--;
			if (_ownerCount != 0)
			{
				return;
			}

			foreach (var state in WorldStates.Values)
			{
				state.Dispose();
			}

			WorldStates.Clear();
			Foundation.Shutdown();
		}
	}

	public static PhysicsWorldState GetOrCreateWorldState(World world)
	{
		lock (SyncRoot)
		{
			if (WorldStates.TryGetValue(world, out var existingState))
			{
				return existingState;
			}

			var broadPhaseLayerInterface = new BroadPhaseLayerInterfaceTable(PhysicsObjectLayerCount, BroadPhaseLayerCount);
			var objectLayerPairFilter = new ObjectLayerPairFilterTable(PhysicsObjectLayerCount);
			var broadPhaseLayer = new BroadPhaseLayer(0);
			for (var i = 0u; i < PhysicsObjectLayerCount; i++)
			{
				var objectLayer = new ObjectLayer(i);
				broadPhaseLayerInterface.MapObjectToBroadPhaseLayer(objectLayer, broadPhaseLayer);
				for (var j = 0u; j < PhysicsObjectLayerCount; j++)
				{
					objectLayerPairFilter.EnableCollision(objectLayer, new ObjectLayer(j));
				}
			}

			var objectVsBroadPhaseLayerFilter = new ObjectVsBroadPhaseLayerFilterTable(
				broadPhaseLayerInterface,
				BroadPhaseLayerCount,
				objectLayerPairFilter,
				PhysicsObjectLayerCount);

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
				Gravity = new Vector3(0.0f, -9.81f, 0.0f)
			};

			var state = new PhysicsWorldState(
				physicsSystem,
				new JobSystemThreadPool(),
				broadPhaseLayerInterface,
				objectLayerPairFilter,
				objectVsBroadPhaseLayerFilter);
			WorldStates.Add(world, state);
			return state;
		}
	}

	public static bool TryGetWorldState(World world, out PhysicsWorldState state)
	{
		lock (SyncRoot)
		{
			return WorldStates.TryGetValue(world, out state!);
		}
	}

	public static bool TryGetWorldStateCount(out int count)
	{
		lock (SyncRoot)
		{
			count = WorldStates.Count;
			return true;
		}
	}

	public static void RemoveWorld(World world)
	{
		lock (SyncRoot)
		{
			if (WorldStates.Remove(world, out var state))
			{
				state.Dispose();
			}
		}
	}
}

internal enum PhysicsBodyOwner
{
	Rigidbody,
	Vehicle
}

internal sealed class PhysicsWorldState : IDisposable
{
	private readonly Lock _contactEventsLock = new();

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

		PhysicsSystem.OnContactValidate += OnContactValidate;
		PhysicsSystem.OnContactAdded += OnContactAdded;
		PhysicsSystem.OnContactPersisted += OnContactPersisted;
		PhysicsSystem.OnContactRemoved += OnContactRemoved;
	}

	public PhysicsSystem PhysicsSystem { get; }
	public JobSystemThreadPool JobSystem { get; }
	public BroadPhaseLayerInterfaceTable BroadPhaseLayerInterface { get; }
	public ObjectLayerPairFilterTable ObjectLayerPairFilter { get; }
	public ObjectVsBroadPhaseLayerFilterTable ObjectVsBroadPhaseLayerFilter { get; }
	public BodyInterface BodyInterface { get; }
	public Dictionary<Entity, PhysicsBodyState> BodiesByEntity { get; } = new();
	public Dictionary<BodyID, PhysicsBodyState> BodiesByBodyId { get; } = new();
	public Dictionary<Entity, PhysicsVehicleState> VehiclesByEntity { get; } = new();
	public List<PhysicsContactEvent> ContactEvents { get; } = new();
	public PhysicsInterpolationClock InterpolationClock { get; } = new();

	public List<PhysicsWorldPoseSyncItem> InterpolationPoseBuffer { get; } = new();
	public int LastBoxColliderCount { get; set; } = -1;
	public int LastSphereColliderCount { get; set; } = -1;
	public int LastCapsuleColliderCount { get; set; } = -1;
	public int LastMeshColliderCount { get; set; } = -1;
	public int LastTerrainColliderCount { get; set; } = -1;

	public void Dispose()
	{
		foreach (var vehicleState in VehiclesByEntity.Values)
		{
			vehicleState.Dispose(this);
		}

		VehiclesByEntity.Clear();

		foreach (var bodyState in BodiesByEntity.Values)
		{
			BodyInterface.RemoveAndDestroyBody(bodyState.BodyId);
			bodyState.Dispose();
		}

		BodiesByEntity.Clear();
		BodiesByBodyId.Clear();
		ContactEvents.Clear();
		PhysicsSystem.Dispose();
		JobSystem.Dispose();
		ObjectVsBroadPhaseLayerFilter.Dispose();
		ObjectLayerPairFilter.Dispose();
		BroadPhaseLayerInterface.Dispose();
	}

	private ValidateResult OnContactValidate(
		PhysicsSystem _,
		in Body bodyA,
		in Body bodyB,
		RVector3 __,
		in CollideShapeResult ___)
	{
		if (BodiesByBodyId.TryGetValue(bodyA.ID, out var bodyStateA) == false ||
		    BodiesByBodyId.TryGetValue(bodyB.ID, out var bodyStateB) == false)
		{
			return ValidateResult.AcceptContact;
		}

		return CanBodiesCollide(bodyStateA.Definition, bodyStateB.Definition)
			? ValidateResult.AcceptContact
			: ValidateResult.RejectContact;
	}

	private void OnContactAdded(
		PhysicsSystem _,
		in Body bodyA,
		in Body bodyB,
		in ContactManifold manifold,
		ref ContactSettings settings)
	{
		AddContactEvent(PhysicsContactEventType.Added, bodyA, bodyB, manifold, settings);
	}

	private void OnContactPersisted(
		PhysicsSystem _,
		in Body bodyA,
		in Body bodyB,
		in ContactManifold manifold,
		ref ContactSettings settings)
	{
		AddContactEvent(PhysicsContactEventType.Persisted, bodyA, bodyB, manifold, settings);
	}

	private void OnContactRemoved(PhysicsSystem _, ref SubShapeIDPair subShapePair)
	{
		AddRemovedContactEvent(subShapePair);
	}

	private void AddContactEvent(
		PhysicsContactEventType eventType,
		in Body bodyA,
		in Body bodyB,
		in ContactManifold manifold,
		ContactSettings settings)
	{
		if (BodiesByBodyId.TryGetValue(bodyA.ID, out var bodyStateA) == false ||
		    BodiesByBodyId.TryGetValue(bodyB.ID, out var bodyStateB) == false)
		{
			return;
		}

		var pointOnA = manifold.PointCount > 0
			? manifold.GetWorldSpaceContactPointOn1(0)
			: Vector3.Zero;
		var pointOnB = manifold.PointCount > 0
			? manifold.GetWorldSpaceContactPointOn2(0)
			: Vector3.Zero;
		var contactEvent = new PhysicsContactEvent(
			eventType,
			bodyStateA.Entity,
			bodyStateB.Entity,
			NormalizeDirection(manifold.WorldSpaceNormal),
			pointOnA,
			pointOnB,
			manifold.PenetrationDepth,
			settings.IsSensor);

		lock (_contactEventsLock)
		{
			ContactEvents.Add(contactEvent);
		}
	}

	private void AddRemovedContactEvent(SubShapeIDPair subShapePair)
	{
		if (BodiesByBodyId.TryGetValue(subShapePair.Body1ID, out var bodyStateA) == false ||
		    BodiesByBodyId.TryGetValue(subShapePair.Body2ID, out var bodyStateB) == false)
		{
			return;
		}

		lock (_contactEventsLock)
		{
			ContactEvents.Add(new PhysicsContactEvent(
				PhysicsContactEventType.Removed,
				bodyStateA.Entity,
				bodyStateB.Entity,
				Vector3.Zero,
				Vector3.Zero,
				Vector3.Zero,
				0.0f,
				false));
		}
	}

	private static bool CanBodiesCollide(PhysicsBodyDefinition bodyA, PhysicsBodyDefinition bodyB)
	{
		var layerABit = 1u << (int)bodyA.Layer;
		var layerBBit = 1u << (int)bodyB.Layer;
		return (bodyA.CollidesWith & layerBBit) != 0 &&
		       (bodyB.CollidesWith & layerABit) != 0;
	}

	private static Vector3 NormalizeDirection(Vector3 value)
	{
		return value.LengthSquared() > 0.0f ? Vector3.Normalize(value) : Vector3.UnitY;
	}
}

internal sealed class PhysicsBodyState : IDisposable
{
	public PhysicsBodyState(
		Entity entity,
		BodyID bodyId,
		PhysicsBodyDefinition definition,
		Shape baseShape,
		RotatedTranslatedShape? translatedShape,
		PhysicsBodyOwner owner)
	{
		Entity = entity;
		BodyId = bodyId;
		Definition = definition;
		BaseShape = baseShape;
		TranslatedShape = translatedShape;
		Owner = owner;
	}

	public Entity Entity { get; }
	public BodyID BodyId { get; }
	public PhysicsBodyDefinition Definition { get; set; }
	public Shape BaseShape { get; }
	public RotatedTranslatedShape? TranslatedShape { get; }
	public PhysicsBodyOwner Owner { get; }

	public PhysicsBodyInterpolationState Interpolation;

	public void Dispose()
	{
		TranslatedShape?.Dispose();
		BaseShape.Dispose();
	}
}

/// <summary>Fixed-step samples used to render an interpolated physics pose.</summary>
internal struct PhysicsBodyInterpolationState
{
	private const float PositionEpsilonSquared = 0.000001f;
	private const float RotationDotEpsilon = 0.999999f;

	public bool HasHistory;
	public Vector3 PreviousPosition;
	public Quaternion PreviousRotation;
	public Vector3 CurrentPosition;
	public Quaternion CurrentRotation;
	public Vector3 LinearVelocity;
	public Vector3 AngularVelocity;
	private bool _hasAppliedPose;
	private Vector3 _appliedPosition;
	private Quaternion _appliedRotation;

	/// <summary>Records the latest fixed-step pose.</summary>
	public void PushSample(Vector3 position, Quaternion rotation, Vector3 linearVelocity, Vector3 angularVelocity)
	{
		if (HasHistory)
		{
			PreviousPosition = CurrentPosition;
			PreviousRotation = CurrentRotation;
		}
		else
		{
			PreviousPosition = position;
			PreviousRotation = rotation;
			HasHistory = true;
		}

		CurrentPosition = position;
		CurrentRotation = rotation;
		LinearVelocity = linearVelocity;
		AngularVelocity = angularVelocity;
	}

	/// <summary>Records a pose and derives its velocity from sample history.</summary>
	public void PushDerivedSample(Vector3 position, Quaternion rotation, float fixedDeltaTime)
	{
		var hadHistory = HasHistory;
		var lastPosition = CurrentPosition;
		var lastRotation = CurrentRotation;
		PushSample(position, rotation, Vector3.Zero, Vector3.Zero);
		if (hadHistory == false || fixedDeltaTime <= 0.0f)
		{
			return;
		}

		LinearVelocity = (position - lastPosition) / fixedDeltaTime;
		AngularVelocity = DeriveAngularVelocity(lastRotation, rotation, fixedDeltaTime);
	}

	/// <summary>Clears sample history.</summary>
	public void Invalidate()
	{
		HasHistory = false;
		_hasAppliedPose = false;
	}

	public readonly void Evaluate(
		RigidbodyInterpolation mode,
		float alpha,
		float timeSinceLastStep,
		out Vector3 position,
		out Quaternion rotation)
	{
		if (mode == RigidbodyInterpolation.Extrapolate)
		{
			position = CurrentPosition + LinearVelocity * timeSinceLastStep;
			rotation = IntegrateRotation(CurrentRotation, AngularVelocity, timeSinceLastStep);
			return;
		}

		position = Vector3.Lerp(PreviousPosition, CurrentPosition, alpha);
		rotation = Quaternion.Slerp(PreviousRotation, CurrentRotation, alpha);
	}

	/// <summary>Returns whether this pose differs from the last pose applied to the transform.</summary>
	public bool TryTrackAppliedPose(Vector3 position, Quaternion rotation)
	{
		if (_hasAppliedPose &&
		    Vector3.DistanceSquared(_appliedPosition, position) <= PositionEpsilonSquared &&
		    MathF.Abs(Quaternion.Dot(_appliedRotation, rotation)) >= RotationDotEpsilon)
		{
			return false;
		}

		_hasAppliedPose = true;
		_appliedPosition = position;
		_appliedRotation = rotation;
		return true;
	}

	private static Vector3 DeriveAngularVelocity(Quaternion from, Quaternion to, float deltaTime)
	{
		var delta = to * Quaternion.Conjugate(from);
		if (delta.W < 0.0f)
		{
			delta = -delta;
		}

		var axis = new Vector3(delta.X, delta.Y, delta.Z);
		var sinHalfAngle = axis.Length();
		if (sinHalfAngle <= 0.000001f)
		{
			return Vector3.Zero;
		}

		var angle = 2.0f * MathF.Atan2(sinHalfAngle, Math.Clamp(delta.W, -1.0f, 1.0f));
		return axis * (angle / (sinHalfAngle * deltaTime));
	}

	private static Quaternion IntegrateRotation(Quaternion rotation, Vector3 angularVelocity, float deltaTime)
	{
		var angularSpeed = angularVelocity.Length();
		var angle = angularSpeed * deltaTime;
		if (angle <= 0.000001f)
		{
			return rotation;
		}

		var delta = Quaternion.CreateFromAxisAngle(angularVelocity / angularSpeed, angle);
		return Quaternion.Normalize(delta * rotation);
	}
}

internal sealed class PhysicsShapeHandle : IDisposable
{
	public PhysicsShapeHandle(Shape baseShape, RotatedTranslatedShape? translatedShape, Shape shape)
	{
		BaseShape = baseShape;
		TranslatedShape = translatedShape;
		Shape = shape;
	}

	public Shape BaseShape { get; }
	public RotatedTranslatedShape? TranslatedShape { get; }
	public Shape Shape { get; }

	public void Dispose()
	{
		TranslatedShape?.Dispose();
		BaseShape.Dispose();
	}
}

internal readonly record struct PhysicsShapeDefinition(
	PhysicsColliderKind Kind,
	Vector3 BoxHalfExtents,
	float CapsuleHalfHeight,
	float CapsuleRadius,
	float SphereRadius,
	Vector3 Center,
	Mesh? Mesh,
	Vector3 MeshScale,
	float[]? HeightSamples,
	Vector3 HeightfieldOffset,
	Vector3 HeightfieldScale,
	uint HeightfieldSampleCount)
{
	public static PhysicsShapeDefinition CreateBox(Vector3 halfExtents, Vector3 center)
	{
		return new PhysicsShapeDefinition(PhysicsColliderKind.Box, halfExtents, 0.0f, 0.0f, 0.0f, center, null, Vector3.One, null, Vector3.Zero, Vector3.One, 0);
	}

	public static PhysicsShapeDefinition CreateCapsule(float halfHeight, float radius, Vector3 center)
	{
		return new PhysicsShapeDefinition(PhysicsColliderKind.Capsule, Vector3.Zero, halfHeight, radius, 0.0f, center, null, Vector3.One, null, Vector3.Zero, Vector3.One, 0);
	}

	public static PhysicsShapeDefinition CreateSphere(float radius, Vector3 center)
	{
		return new PhysicsShapeDefinition(PhysicsColliderKind.Sphere, Vector3.Zero, 0.0f, 0.0f, radius, center, null, Vector3.One, null, Vector3.Zero, Vector3.One, 0);
	}

	public static PhysicsShapeDefinition CreateMesh(Mesh mesh, Vector3 meshScale)
	{
		return new PhysicsShapeDefinition(PhysicsColliderKind.Mesh, Vector3.Zero, 0.0f, 0.0f, 0.0f, Vector3.Zero, mesh, meshScale, null, Vector3.Zero, Vector3.One, 0);
	}

	public static PhysicsShapeDefinition CreateTerrain(
		float[] heightSamples,
		Vector3 heightfieldOffset,
		Vector3 heightfieldScale,
		uint heightfieldSampleCount)
	{
		return new PhysicsShapeDefinition(
			PhysicsColliderKind.Terrain,
			Vector3.Zero,
			0.0f,
			0.0f,
			0.0f,
			Vector3.Zero,
			null,
			Vector3.One,
			heightSamples,
			heightfieldOffset,
			heightfieldScale,
			heightfieldSampleCount);
	}
}

internal readonly record struct PhysicsBodyDefinition(
	PhysicsShapeDefinition Shape,
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
	uint Layer,
	uint CollidesWith,
	MotionType MotionType);

internal enum PhysicsColliderKind
{
	Terrain,
	Box,
	Sphere,
	Capsule,
	Mesh
}

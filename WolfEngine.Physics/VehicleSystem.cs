using System;
using System.Collections.Generic;
using System.Numerics;
using JoltPhysicsSharp;
using WolfEngine.ECS;

namespace WolfEngine.Physics;

public sealed class VehicleSystem : IPhysicsUpdate, IWorldRemovedListener, IDisposable
{
	private bool _disposed;

	public VehicleSystem()
	{
		PhysicsWorldRegistry.AcquireOwner();
	}

	public WorldTag GetTag() => WorldTag.Game;

	public void PhysicsUpdate(float fixedDeltaTime, World world)
	{
		ArgumentNullException.ThrowIfNull(world);
		var state = PhysicsWorldRegistry.GetOrCreateWorldState(world);
		SynchronizeVehiclesBeforeStep(world, state, fixedDeltaTime);
	}

	public void OnWorldRemoved(World world)
	{
		if (world is null)
		{
			return;
		}

		PhysicsWorldRegistry.RemoveWorld(world);
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		PhysicsWorldRegistry.ReleaseOwner();
		_disposed = true;
	}

	internal static void SynchronizeVehiclesBeforeStep(World world, PhysicsWorldState state, float fixedDeltaTime)
	{
		var toRemove = new List<Entity>();
		foreach (var pair in state.VehiclesByEntity)
		{
			var definition = CreateDefinition(world, pair.Key);
			if (definition is null)
			{
				toRemove.Add(pair.Key);
				continue;
			}

			var vehicleState = pair.Value;
			if (HasStructuralChanges(vehicleState.Definition, definition.Value))
			{
				toRemove.Add(pair.Key);
			}
			else
			{
				ApplyRuntimeInput(world, vehicleState, fixedDeltaTime);
			}
		}

		for (var index = 0; index < toRemove.Count; index++)
		{
			RemoveVehicle(state, toRemove[index]);
		}

		foreach (var entry in world.View<Vehicle>())
		{
			var entity = entry.Entity;
			if (state.VehiclesByEntity.ContainsKey(entity))
			{
				continue;
			}

			var definition = CreateDefinition(world, entity);
			if (definition is null)
			{
				continue;
			}

			CreateVehicle(world, state, entity, definition.Value, fixedDeltaTime);
		}
	}

	internal static void SyncVehicleVisuals(World world, PhysicsWorldState state)
	{
		foreach (var pair in state.VehiclesByEntity)
		{
			var vehicleState = pair.Value;
			for (var wheelIndex = 0; wheelIndex < vehicleState.Definition.Wheels.Length; wheelIndex++)
			{
				var wheelDefinition = vehicleState.Definition.Wheels[wheelIndex];
				if (wheelDefinition.VisualEntity.IsValid == false ||
				    world.IsAlive(wheelDefinition.VisualEntity) == false ||
				    world.HasComponent<LocalTransform>(wheelDefinition.VisualEntity) == false ||
				    world.HasComponent<WorldTransform>(wheelDefinition.VisualEntity) == false)
				{
					continue;
				}

				var wheelRight = Vector3.Zero;
				var wheelUp = Vector3.Zero;
				var wheelMatrix = vehicleState.Constraint.GetWheelWorldTransform(wheelIndex, in wheelRight, in wheelUp);
				if (Matrix4x4.Decompose(wheelMatrix, out _, out var rotation, out var position) == false)
				{
					continue;
				}

				world.ApplyPhysicsWorldPose(wheelDefinition.VisualEntity, position, rotation);
			}
		}
	}

	internal int GetTrackedVehicleCount(World world)
	{
		return PhysicsWorldRegistry.TryGetWorldState(world, out var state) ? state.VehiclesByEntity.Count : 0;
	}

	private static void CreateVehicle(
		World world,
		PhysicsWorldState state,
		Entity entity,
		PhysicsVehicleDefinition definition,
		float fixedDeltaTime)
	{
		var shapeHandle = CreateChassisShape(definition);
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
		bodySettings.OverrideMassProperties = OverrideMassProperties.CalculateInertia;
		bodySettings.MassPropertiesOverride = new MassProperties
		{
			Mass = MathF.Max(0.001f, definition.Mass)
		};

		var body = state.BodyInterface.CreateBody(bodySettings);
		state.BodyInterface.AddBody(body, definition.StartActivated ? Activation.Activate : Activation.DontActivate);
		var bodyId = body.ID;
		var bodyDefinition = definition.ToPhysicsBodyDefinition();
		var bodyState = new PhysicsBodyState(entity, bodyId, bodyDefinition, shapeHandle.BaseShape, shapeHandle.TranslatedShape, PhysicsBodyOwner.Vehicle);
		state.BodiesByEntity.Add(entity, bodyState);
		state.BodiesByBodyId.Add(bodyId, bodyState);

		var controllerSettings = new WheeledVehicleControllerSettings
		{
			Engine = CreateEngineSettings(definition),
			Transmission = CreateTransmissionSettings(definition),
			DifferentialsCount = 1,
			DifferentialLimitedSlipRatio = definition.DifferentialLimitedSlipRatio
		};
		controllerSettings.SetDifferential(0, new VehicleDifferentialSettings
		{
			LeftWheel = 0,
			RightWheel = 1,
			DifferentialRatio = definition.DifferentialRatio,
			LeftRightSplit = definition.DifferentialLeftRightSplit,
			LimitedSlipRatio = definition.DifferentialLimitedSlipRatio,
			EngineTorqueRatio = definition.DifferentialEngineTorqueRatio
		});

		var constraintSettings = new VehicleConstraintSettings
		{
			Up = definition.Up,
			Forward = definition.Forward,
			MaxPitchRollAngle = definition.MaxPitchRollAngle,
			Wheels = CreateWheelSettings(definition),
			Controller = controllerSettings
		};

		var constraint = new VehicleConstraint(body, constraintSettings);
		constraint.SetVehicleCollisionTester(new VehicleCollisionTesterRay(new ObjectLayer(definition.Layer)));
		state.PhysicsSystem.AddConstraint(constraint);
		state.PhysicsSystem.AddStepListener(constraint);

		var controller = constraint.GetController<WheeledVehicleController>();
		var vehicleState = new PhysicsVehicleState(entity, bodyId, bodyState, constraint, controller, definition);
		state.VehiclesByEntity.Add(entity, vehicleState);
		ApplyRuntimeInput(world, vehicleState, fixedDeltaTime);
	}

	private static void RemoveVehicle(PhysicsWorldState state, Entity entity)
	{
		if (state.VehiclesByEntity.Remove(entity, out var vehicleState) == false)
		{
			return;
		}

		vehicleState.Dispose(state);
	}

	private static void ApplyRuntimeInput(World world, PhysicsVehicleState vehicleState, float fixedDeltaTime)
	{
		var input = world.HasComponent<VehicleInput>(vehicleState.Entity)
			? world.GetComponent<VehicleInput>(vehicleState.Entity)
			: VehicleInput.CreateDefault();
		var forwardInput = Math.Clamp(input.Throttle - input.Brake, -1.0f, 1.0f);
		var rightInput = Math.Clamp(input.Steer, -1.0f, 1.0f);
		var brakeInput = Math.Clamp(input.Brake, 0.0f, 1.0f);
		var handBrakeInput = Math.Clamp(input.HandBrake, 0.0f, 1.0f);
		vehicleState.Controller.SetDriverInput(forwardInput, rightInput, brakeInput, handBrakeInput);

		if (fixedDeltaTime > 0.0f)
		{
			vehicleState.Controller.Engine.ApplyDamping(fixedDeltaTime);
		}

		var driveTorque = MathF.Max(0.0f, forwardInput) * vehicleState.Definition.EngineMaxTorque;
		for (var wheelIndex = 0; wheelIndex < vehicleState.Definition.Wheels.Length; wheelIndex++)
		{
			var wheelDefinition = vehicleState.Definition.Wheels[wheelIndex];
			var wheel = vehicleState.Constraint.GetWheel<WheelWV>(wheelIndex);
			wheel.SteerAngle = wheelDefinition.Steer ? rightInput * wheelDefinition.MaxSteerAngle : 0.0f;
			var wheelBrake = brakeInput * wheelDefinition.MaxBrakeTorque +
			                 handBrakeInput * (wheelDefinition.HandBrake ? wheelDefinition.MaxHandBrakeTorque : 0.0f);
			wheel.ApplyTorque(wheelDefinition.Drive ? driveTorque : 0.0f, wheelBrake);
		}
	}

	private static bool HasStructuralChanges(PhysicsVehicleDefinition previous, PhysicsVehicleDefinition current)
	{
		return previous.BoxHalfExtents != current.BoxHalfExtents ||
		       previous.BoxCenter != current.BoxCenter ||
		       previous.Mass != current.Mass ||
		       MathF.Abs(previous.GravityFactor - current.GravityFactor) > 0.0001f ||
		       previous.StartActivated != current.StartActivated ||
		       previous.AllowSleeping != current.AllowSleeping ||
		       previous.UseManifoldReduction != current.UseManifoldReduction ||
		       previous.IsSensor != current.IsSensor ||
		       previous.Layer != current.Layer ||
		       previous.CollidesWith != current.CollidesWith ||
		       previous.Up != current.Up ||
		       previous.Forward != current.Forward ||
		       MathF.Abs(previous.MaxPitchRollAngle - current.MaxPitchRollAngle) > 0.0001f ||
		       MathF.Abs(previous.DifferentialLimitedSlipRatio - current.DifferentialLimitedSlipRatio) > 0.0001f ||
		       MathF.Abs(previous.DifferentialRatio - current.DifferentialRatio) > 0.0001f ||
		       MathF.Abs(previous.DifferentialLeftRightSplit - current.DifferentialLeftRightSplit) > 0.0001f ||
		       MathF.Abs(previous.DifferentialEngineTorqueRatio - current.DifferentialEngineTorqueRatio) > 0.0001f ||
		       MathF.Abs(previous.EngineMaxTorque - current.EngineMaxTorque) > 0.0001f ||
		       MathF.Abs(previous.EngineMinRpm - current.EngineMinRpm) > 0.0001f ||
		       MathF.Abs(previous.EngineMaxRpm - current.EngineMaxRpm) > 0.0001f ||
		       MathF.Abs(previous.EngineInertia - current.EngineInertia) > 0.0001f ||
		       MathF.Abs(previous.EngineAngularDamping - current.EngineAngularDamping) > 0.0001f ||
		       MathF.Abs(previous.TransmissionShiftUpRpm - current.TransmissionShiftUpRpm) > 0.0001f ||
		       MathF.Abs(previous.TransmissionShiftDownRpm - current.TransmissionShiftDownRpm) > 0.0001f ||
		       MathF.Abs(previous.TransmissionSwitchTime - current.TransmissionSwitchTime) > 0.0001f ||
		       MathF.Abs(previous.TransmissionClutchReleaseTime - current.TransmissionClutchReleaseTime) > 0.0001f ||
		       MathF.Abs(previous.TransmissionSwitchLatency - current.TransmissionSwitchLatency) > 0.0001f ||
		       MathF.Abs(previous.TransmissionClutchStrength - current.TransmissionClutchStrength) > 0.0001f ||
		       MathF.Abs(previous.TransmissionForwardGearRatio - current.TransmissionForwardGearRatio) > 0.0001f ||
		       MathF.Abs(previous.TransmissionReverseGearRatio - current.TransmissionReverseGearRatio) > 0.0001f ||
		       previous.Wheels.AsSpan().SequenceEqual(current.Wheels.AsSpan()) == false;
	}

	private static PhysicsVehicleDefinition? CreateDefinition(World world, Entity entity)
	{
		if (world.IsAlive(entity) == false ||
		    world.HasComponent<Vehicle>(entity) == false ||
		    world.HasComponent<Rigidbody>(entity) == false ||
		    world.HasComponent<BoxCollider>(entity) == false ||
		    world.HasComponent<LocalTransform>(entity) == false ||
		    world.HasComponent<WorldTransform>(entity) == false)
		{
			return null;
		}

		var rigidbody = world.GetComponent<Rigidbody>(entity);
		if (rigidbody.BodyType != RigidbodyBodyType.Dynamic)
		{
			return null;
		}

		var boxCollider = world.GetComponent<BoxCollider>(entity);
		var vehicle = world.GetComponent<Vehicle>(entity);
		var collisionFilter = world.HasComponent<CollisionFilter>(entity)
			? world.GetComponent<CollisionFilter>(entity)
			: CollisionFilter.CreateDefault();

		if (TryNormalize(world, entity, out var position, out var rotation, out var worldScale) == false)
		{
			return null;
		}

		var wheels = new[]
		{
			CreateWheelDefinition(vehicle.FrontLeft, worldScale),
			CreateWheelDefinition(vehicle.FrontRight, worldScale),
			CreateWheelDefinition(vehicle.RearLeft, worldScale),
			CreateWheelDefinition(vehicle.RearRight, worldScale)
		};

		return new PhysicsVehicleDefinition(
			position,
			rotation,
			Vector3.Max(new Vector3(0.001f), Multiply(boxCollider.HalfExtents, worldScale)),
			Multiply(boxCollider.Center, worldScale),
			rigidbody.LinearVelocity,
			rigidbody.AngularVelocity,
			MathF.Max(0.001f, rigidbody.Mass),
			rigidbody.GravityFactor,
			rigidbody.StartActivated,
			rigidbody.AllowSleeping,
			rigidbody.UseManifoldReduction,
			rigidbody.IsSensor,
			ClampLayer(collisionFilter.Layer),
			collisionFilter.CollidesWith,
			Normalize(vehicle.Up, Vector3.UnitY),
			Normalize(vehicle.Forward, Vector3.UnitZ),
			MathF.Max(0.0f, vehicle.MaxPitchRollAngle),
			MathF.Max(0.01f, vehicle.DifferentialLimitedSlipRatio),
			vehicle.DifferentialRatio,
			vehicle.DifferentialLeftRightSplit,
			vehicle.DifferentialEngineTorqueRatio,
			vehicle.EngineMaxTorque,
			vehicle.EngineMinRpm,
			vehicle.EngineMaxRpm,
			vehicle.EngineInertia,
			vehicle.EngineAngularDamping,
			vehicle.TransmissionShiftUpRpm,
			vehicle.TransmissionShiftDownRpm,
			vehicle.TransmissionSwitchTime,
			vehicle.TransmissionClutchReleaseTime,
			vehicle.TransmissionSwitchLatency,
			vehicle.TransmissionClutchStrength,
			vehicle.TransmissionForwardGearRatio,
			vehicle.TransmissionReverseGearRatio,
			wheels);
	}

	private static bool TryNormalize(World world, Entity entity, out Vector3 position, out Quaternion rotation, out Vector3 scale)
	{
		position = Vector3.Zero;
		rotation = Quaternion.Identity;
		scale = Vector3.One;
		if (world.TryGetWorldPoseAndScale(entity, out position, out rotation, out scale) == false)
		{
			return false;
		}

		rotation = rotation.LengthSquared() > 0.0f ? Quaternion.Normalize(rotation) : Quaternion.Identity;
		return true;
	}

	private static PhysicsVehicleWheelDefinition CreateWheelDefinition(VehicleWheel wheel, Vector3 worldScale)
	{
		var radiusScale = MathF.Max(MathF.Abs(worldScale.X), MathF.Abs(worldScale.Z));
		return new PhysicsVehicleWheelDefinition(
			wheel.VisualEntity,
			Multiply(wheel.Position, worldScale),
			Multiply(wheel.SuspensionForcePoint, worldScale),
			Normalize(wheel.SuspensionDirection, -Vector3.UnitY),
			Normalize(wheel.SteeringAxis, Vector3.UnitY),
			Normalize(wheel.WheelUp, Vector3.UnitY),
			Normalize(wheel.WheelForward, Vector3.UnitZ),
			MathF.Max(0.001f, MathF.Abs(wheel.SuspensionMinLength * worldScale.Y)),
			MathF.Max(0.001f, MathF.Abs(wheel.SuspensionMaxLength * worldScale.Y)),
			MathF.Max(0.0f, MathF.Abs(wheel.SuspensionPreloadLength * worldScale.Y)),
			wheel.SuspensionMode,
			MathF.Max(0.001f, wheel.SuspensionFrequencyOrStiffness),
			MathF.Max(0.0f, wheel.SuspensionDamping),
			MathF.Max(0.001f, MathF.Abs(wheel.Radius * radiusScale)),
			MathF.Max(0.001f, MathF.Abs(wheel.Width * worldScale.X)),
			MathF.Max(0.001f, wheel.Inertia),
			MathF.Max(0.0f, wheel.MaxSteerAngle),
			MathF.Max(0.0f, wheel.MaxBrakeTorque),
			MathF.Max(0.0f, wheel.MaxHandBrakeTorque),
			wheel.EnableSuspensionForcePoint,
			wheel.Steer,
			wheel.Drive,
			wheel.HandBrake);
	}

	private static VehicleEngineSettings CreateEngineSettings(PhysicsVehicleDefinition definition)
	{
		return new VehicleEngineSettings
		{
			MaxTorque = definition.EngineMaxTorque,
			MinRPM = definition.EngineMinRpm,
			MaxRPM = definition.EngineMaxRpm,
			Inertia = definition.EngineInertia,
			AngularDamping = definition.EngineAngularDamping,
			NormalizedTorque = CreateFlatCurve()
		};
	}

	private static VehicleTransmissionSettings CreateTransmissionSettings(PhysicsVehicleDefinition definition)
	{
		var settings = new VehicleTransmissionSettings
		{
			Mode = TransmissionMode.Auto,
			SwitchTime = definition.TransmissionSwitchTime,
			ClutchReleaseTime = definition.TransmissionClutchReleaseTime,
			SwitchLatency = definition.TransmissionSwitchLatency,
			ShiftUpRPM = definition.TransmissionShiftUpRpm,
			ShiftDownRPM = definition.TransmissionShiftDownRpm,
			ClutchStrength = definition.TransmissionClutchStrength
		};
		settings.SetGearRatio(0, definition.TransmissionForwardGearRatio);
		settings.SetReverseGearRatio(0, definition.TransmissionReverseGearRatio);
		return settings;
	}

	private static WheelSettings[] CreateWheelSettings(PhysicsVehicleDefinition definition)
	{
		var wheels = new WheelSettings[definition.Wheels.Length];
		for (var index = 0; index < definition.Wheels.Length; index++)
		{
			var wheel = definition.Wheels[index];
			var wheelSettings = new WheelSettingsWV
			{
				Position = wheel.Position,
				SuspensionForcePoint = wheel.SuspensionForcePoint,
				SuspensionDirection = wheel.SuspensionDirection,
				SteeringAxis = wheel.SteeringAxis,
				WheelUp = wheel.WheelUp,
				WheelForward = wheel.WheelForward,
				SuspensionMinLength = wheel.SuspensionMinLength,
				SuspensionMaxLength = wheel.SuspensionMaxLength,
				SuspensionPreloadLength = wheel.SuspensionPreloadLength,
				SuspensionSpring = new SpringSettings
				{
					Mode = wheel.SuspensionMode,
					FrequencyOrStiffness = wheel.SuspensionFrequencyOrStiffness,
					Damping = wheel.SuspensionDamping
				},
				Radius = wheel.Radius,
				Width = wheel.Width,
				EnableSuspensionForcePoint = wheel.EnableSuspensionForcePoint,
				Inertia = wheel.Inertia,
				MaxSteerAngle = wheel.Steer ? wheel.MaxSteerAngle : 0.0f,
				MaxBrakeTorque = wheel.MaxBrakeTorque,
				MaxHandBrakeTorque = wheel.HandBrake ? wheel.MaxHandBrakeTorque : 0.0f,
				LongitudinalFriction = CreateFlatCurve(),
				LateralFriction = CreateFlatCurve()
			};
			wheels[index] = wheelSettings;
		}

		return wheels;
	}

	private static LinearCurve CreateFlatCurve()
	{
		var curve = new LinearCurve();
		curve.AddPoint(0.0f, 1.0f);
		curve.AddPoint(1.0f, 1.0f);
		return curve;
	}

	private static PhysicsShapeHandle CreateChassisShape(PhysicsVehicleDefinition definition)
	{
		Shape baseShape = new BoxShape(definition.BoxHalfExtents);
		if (definition.BoxCenter == Vector3.Zero)
		{
			return new PhysicsShapeHandle(baseShape, null, baseShape);
		}

		var translatedShape = new RotatedTranslatedShape(definition.BoxCenter, Quaternion.Identity, baseShape);
		return new PhysicsShapeHandle(baseShape, translatedShape, translatedShape);
	}

	private static Vector3 Multiply(Vector3 left, Vector3 right)
	{
		return new Vector3(left.X * right.X, left.Y * right.Y, left.Z * right.Z);
	}

	private static Vector3 Normalize(Vector3 value, Vector3 fallback)
	{
		return value.LengthSquared() > 0.0f ? Vector3.Normalize(value) : fallback;
	}

	private static uint ClampLayer(uint layer)
	{
		return layer <= CollisionFilter.MaxLayer ? layer : CollisionFilter.MaxLayer;
	}
}

internal sealed class PhysicsVehicleState
{
	public PhysicsVehicleState(
		Entity entity,
		BodyID bodyId,
		PhysicsBodyState bodyState,
		VehicleConstraint constraint,
		WheeledVehicleController controller,
		PhysicsVehicleDefinition definition)
	{
		Entity = entity;
		BodyId = bodyId;
		BodyState = bodyState;
		Constraint = constraint;
		Controller = controller;
		Definition = definition;
	}

	public Entity Entity { get; }
	public BodyID BodyId { get; }
	public PhysicsBodyState BodyState { get; }
	public VehicleConstraint Constraint { get; }
	public WheeledVehicleController Controller { get; }
	public PhysicsVehicleDefinition Definition { get; }

	public void Dispose(PhysicsWorldState state)
	{
		state.PhysicsSystem.RemoveStepListener(Constraint);
		state.PhysicsSystem.RemoveConstraint(Constraint);
		state.BodiesByEntity.Remove(Entity);
		state.BodiesByBodyId.Remove(BodyId);
		state.BodyInterface.RemoveAndDestroyBody(BodyId);
		Constraint.Dispose();
		BodyState.Dispose();
	}
}

internal readonly record struct PhysicsVehicleDefinition(
	Vector3 Position,
	Quaternion Rotation,
	Vector3 BoxHalfExtents,
	Vector3 BoxCenter,
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
	Vector3 Up,
	Vector3 Forward,
	float MaxPitchRollAngle,
	float DifferentialLimitedSlipRatio,
	float DifferentialRatio,
	float DifferentialLeftRightSplit,
	float DifferentialEngineTorqueRatio,
	float EngineMaxTorque,
	float EngineMinRpm,
	float EngineMaxRpm,
	float EngineInertia,
	float EngineAngularDamping,
	float TransmissionShiftUpRpm,
	float TransmissionShiftDownRpm,
	float TransmissionSwitchTime,
	float TransmissionClutchReleaseTime,
	float TransmissionSwitchLatency,
	float TransmissionClutchStrength,
	float TransmissionForwardGearRatio,
	float TransmissionReverseGearRatio,
	PhysicsVehicleWheelDefinition[] Wheels)
{
	public MotionType MotionType => MotionType.Dynamic;

	public PhysicsBodyDefinition ToPhysicsBodyDefinition()
	{
		return new PhysicsBodyDefinition(
			PhysicsShapeDefinition.CreateBox(BoxHalfExtents, BoxCenter),
			Position,
			Rotation,
			LinearVelocity,
			AngularVelocity,
			Mass,
			GravityFactor,
			StartActivated,
			AllowSleeping,
			UseManifoldReduction,
			IsSensor,
			Layer,
			CollidesWith,
			MotionType.Dynamic);
	}
}

internal readonly record struct PhysicsVehicleWheelDefinition(
	Entity VisualEntity,
	Vector3 Position,
	Vector3 SuspensionForcePoint,
	Vector3 SuspensionDirection,
	Vector3 SteeringAxis,
	Vector3 WheelUp,
	Vector3 WheelForward,
	float SuspensionMinLength,
	float SuspensionMaxLength,
	float SuspensionPreloadLength,
	SpringMode SuspensionMode,
	float SuspensionFrequencyOrStiffness,
	float SuspensionDamping,
	float Radius,
	float Width,
	float Inertia,
	float MaxSteerAngle,
	float MaxBrakeTorque,
	float MaxHandBrakeTorque,
	bool EnableSuspensionForcePoint,
	bool Steer,
	bool Drive,
	bool HandBrake);

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
				ApplyRuntimeChanges(state, vehicleState, definition.Value);
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

	/// <summary>Records wheel poses after each fixed step.</summary>
	internal static void SyncVehicleVisuals(World world, PhysicsWorldState state, float fixedDeltaTime)
	{
		foreach (var pair in state.VehiclesByEntity)
		{
			var vehicleState = pair.Value;
			var interpolation = GetInterpolationMode(world, pair.Key);
			for (var wheelIndex = 0; wheelIndex < vehicleState.Definition.Wheels.Length; wheelIndex++)
			{
				var wheelDefinition = vehicleState.Definition.Wheels[wheelIndex];
				if (TryGetWheelVisualPose(world, vehicleState, wheelIndex, wheelDefinition, out var position, out var rotation) == false)
				{
					vehicleState.WheelInterpolation[wheelIndex].Invalidate();
					continue;
				}

				vehicleState.WheelInterpolation[wheelIndex].PushDerivedSample(position, rotation, fixedDeltaTime);
				if (interpolation == RigidbodyInterpolation.None)
				{
					world.ApplyPhysicsWorldPose(wheelDefinition.VisualEntity, position, rotation);
				}
			}
		}
	}

	/// <summary>Adds interpolated wheel poses to the frame's transform batch.</summary>
	internal static void CollectInterpolatedWheelPoses(
		World world,
		PhysicsWorldState state,
		float alpha,
		float timeSinceLastStep,
		List<PhysicsWorldPoseSyncItem> poses)
	{
		foreach (var pair in state.VehiclesByEntity)
		{
			var vehicleState = pair.Value;
			var interpolation = GetInterpolationMode(world, pair.Key);
			if (interpolation == RigidbodyInterpolation.None)
			{
				continue;
			}

			for (var wheelIndex = 0; wheelIndex < vehicleState.Definition.Wheels.Length; wheelIndex++)
			{
				ref var wheelInterpolation = ref vehicleState.WheelInterpolation[wheelIndex];
				var visualEntity = vehicleState.Definition.Wheels[wheelIndex].VisualEntity;
				if (wheelInterpolation.HasHistory == false ||
				    visualEntity.IsValid == false ||
				    world.IsAlive(visualEntity) == false ||
				    world.HasComponent<LocalTransform>(visualEntity) == false ||
				    world.HasComponent<WorldTransform>(visualEntity) == false)
				{
					continue;
				}

				wheelInterpolation.Evaluate(interpolation, alpha, timeSinceLastStep, out var position, out var rotation);
				if (wheelInterpolation.TryTrackAppliedPose(position, rotation) == false)
				{
					continue;
				}

				poses.Add(new PhysicsWorldPoseSyncItem(visualEntity, position, rotation));
			}
		}
	}

	private static bool TryGetWheelVisualPose(
		World world,
		PhysicsVehicleState vehicleState,
		int wheelIndex,
		PhysicsVehicleWheelDefinition wheelDefinition,
		out Vector3 position,
		out Quaternion rotation)
	{
		position = Vector3.Zero;
		rotation = Quaternion.Identity;
		if (wheelDefinition.VisualEntity.IsValid == false ||
		    world.IsAlive(wheelDefinition.VisualEntity) == false ||
		    world.HasComponent<LocalTransform>(wheelDefinition.VisualEntity) == false ||
		    world.HasComponent<WorldTransform>(wheelDefinition.VisualEntity) == false)
		{
			return false;
		}

		var wheelUp = Normalize(wheelDefinition.WheelUp, Vector3.UnitY);
		var wheelRight = Vector3.Cross(
			Normalize(wheelDefinition.WheelForward, Vector3.UnitZ),
			wheelUp);
		wheelRight = Normalize(wheelRight, Vector3.UnitX);
		var wheelMatrix = vehicleState.Constraint.GetWheelWorldTransform(wheelIndex, in wheelRight, in wheelUp);
		return TryDecomposeWheelTransform(wheelMatrix, out rotation, out position);
	}

	private static RigidbodyInterpolation GetInterpolationMode(World world, Entity entity)
	{
		return world.HasComponent<Rigidbody>(entity)
			? world.GetComponent<Rigidbody>(entity).Interpolation
			: RigidbodyInterpolation.None;
	}

	internal int GetTrackedVehicleCount(World world)
	{
		return PhysicsWorldRegistry.TryGetWorldState(world, out var state) ? state.VehiclesByEntity.Count : 0;
	}

	internal bool TryGetDriverInput(World world, Entity entity, out float throttle, out float brake)
	{
		if (PhysicsWorldRegistry.TryGetWorldState(world, out var state) &&
		    state.VehiclesByEntity.TryGetValue(entity, out var vehicleState))
		{
			throttle = vehicleState.Controller.ForwardInput;
			brake = vehicleState.Controller.BrakeInput;
			return true;
		}

		throttle = 0.0f;
		brake = 0.0f;
		return false;
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
		var collisionTester = new VehicleCollisionTesterRay(
			new ObjectLayer(definition.Layer),
			Normalize(definition.Up, Vector3.UnitY),
			0.95f);
		constraint.SetVehicleCollisionTester(collisionTester);
		constraint.SetMaxPitchRollAngle(definition.MaxPitchRollAngle);
		state.PhysicsSystem.AddConstraint(constraint);
		state.PhysicsSystem.AddStepListener(constraint);

		var controller = constraint.GetController<WheeledVehicleController>();
		var vehicleState = new PhysicsVehicleState(entity, bodyId, bodyState, constraint, collisionTester, controller, definition);
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
		var forwardInput = Math.Clamp(input.Throttle, 0.0f, 1.0f);
		var rightInput = Math.Clamp(input.Steer, -1.0f, 1.0f);
		var brakeInput = Math.Clamp(input.Brake, 0.0f, 1.0f);
		var handBrakeInput = Math.Clamp(input.HandBrake, 0.0f, 1.0f);
		vehicleState.Controller.SetDriverInput(forwardInput, -rightInput, brakeInput, handBrakeInput);

		if (fixedDeltaTime > 0.0f)
		{
			vehicleState.Controller.Engine.ApplyDamping(fixedDeltaTime);
		}

		for (var wheelIndex = 0; wheelIndex < vehicleState.Definition.Wheels.Length; wheelIndex++)
		{
			var wheelDefinition = vehicleState.Definition.Wheels[wheelIndex];
			var wheel = vehicleState.Constraint.GetWheel<WheelWV>(wheelIndex);
			wheel.SteerAngle = wheelDefinition.Steer ? rightInput * wheelDefinition.MaxSteerAngle : 0.0f;
		}
	}


	private static bool HasStructuralChanges(PhysicsVehicleDefinition previous, PhysicsVehicleDefinition current)
	{
		return NearEqual(previous.BoxHalfExtents, current.BoxHalfExtents) == false ||
		       NearEqual(previous.BoxCenter, current.BoxCenter) == false ||
		       NearEqual(previous.CenterOfMassOffset, current.CenterOfMassOffset) == false ||
		       NearEqual(previous.Mass, current.Mass) == false ||
		       previous.AllowSleeping != current.AllowSleeping ||
		       NearEqual(previous.Up, current.Up) == false ||
		       NearEqual(previous.Forward, current.Forward) == false ||
		       NearEqual(previous.DifferentialLimitedSlipRatio, current.DifferentialLimitedSlipRatio) == false ||
		       NearEqual(previous.DifferentialRatio, current.DifferentialRatio) == false ||
		       NearEqual(previous.DifferentialLeftRightSplit, current.DifferentialLeftRightSplit) == false ||
		       NearEqual(previous.DifferentialEngineTorqueRatio, current.DifferentialEngineTorqueRatio) == false ||
		       NearEqual(previous.EngineMaxTorque, current.EngineMaxTorque) == false ||
		       NearEqual(previous.EngineMinRpm, current.EngineMinRpm) == false ||
		       NearEqual(previous.EngineMaxRpm, current.EngineMaxRpm) == false ||
		       NearEqual(previous.EngineInertia, current.EngineInertia) == false ||
		       NearEqual(previous.EngineAngularDamping, current.EngineAngularDamping) == false ||
		       NearEqual(previous.TransmissionShiftUpRpm, current.TransmissionShiftUpRpm) == false ||
		       NearEqual(previous.TransmissionShiftDownRpm, current.TransmissionShiftDownRpm) == false ||
		       NearEqual(previous.TransmissionSwitchTime, current.TransmissionSwitchTime) == false ||
		       NearEqual(previous.TransmissionClutchReleaseTime, current.TransmissionClutchReleaseTime) == false ||
		       NearEqual(previous.TransmissionSwitchLatency, current.TransmissionSwitchLatency) == false ||
		       NearEqual(previous.TransmissionClutchStrength, current.TransmissionClutchStrength) == false ||
		       NearEqual(previous.TransmissionForwardGearRatio, current.TransmissionForwardGearRatio) == false ||
		       NearEqual(previous.TransmissionReverseGearRatio, current.TransmissionReverseGearRatio) == false ||
		       WheelsEqual(previous.Wheels, current.Wheels) == false;
	}

	private static void ApplyRuntimeChanges(
		PhysicsWorldState state,
		PhysicsVehicleState vehicleState,
		PhysicsVehicleDefinition current)
	{
		var previous = vehicleState.Definition;
		var bodyId = vehicleState.BodyId;
		if (NearEqual(previous.GravityFactor, current.GravityFactor) == false)
		{
			state.BodyInterface.SetGravityFactor(bodyId, current.GravityFactor);
		}

		if (previous.UseManifoldReduction != current.UseManifoldReduction)
		{
			state.BodyInterface.SetUseManifoldReduction(bodyId, current.UseManifoldReduction);
		}

		if (previous.IsSensor != current.IsSensor)
		{
			state.BodyInterface.SetIsSensor(bodyId, current.IsSensor);
		}

		if (previous.Layer != current.Layer)
		{
			state.BodyInterface.SetObjectLayer(bodyId, new ObjectLayer(current.Layer));
			vehicleState.CollisionTester.objectLayer = new ObjectLayer(current.Layer);
		}

		if (NearEqual(previous.MaxPitchRollAngle, current.MaxPitchRollAngle) == false)
		{
			vehicleState.Constraint.SetMaxPitchRollAngle(current.MaxPitchRollAngle);
		}

		if (previous.StartActivated != current.StartActivated)
		{
			if (current.StartActivated)
			{
				state.BodyInterface.ActivateBody(bodyId);
			}
			else
			{
				state.BodyInterface.DeactivateBody(bodyId);
			}
		}

		vehicleState.Definition = current;
		vehicleState.BodyState.Definition = current.ToPhysicsBodyDefinition();
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

		var scaledCenterOfMassOffset = Multiply(vehicle.CenterOfMassOffset, worldScale);
		var wheels = new[]
		{
			CreateWheelDefinition(vehicle.FrontLeft, worldScale, scaledCenterOfMassOffset),
			CreateWheelDefinition(vehicle.FrontRight, worldScale, scaledCenterOfMassOffset),
			CreateWheelDefinition(vehicle.RearLeft, worldScale, scaledCenterOfMassOffset),
			CreateWheelDefinition(vehicle.RearRight, worldScale, scaledCenterOfMassOffset)
		};

		return new PhysicsVehicleDefinition(
			position + Vector3.Transform(scaledCenterOfMassOffset, rotation),
			rotation,
			Vector3.Max(new Vector3(0.001f), Multiply(boxCollider.HalfExtents, worldScale)),
			Multiply(boxCollider.Center, worldScale) - scaledCenterOfMassOffset,
			scaledCenterOfMassOffset,
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
			vehicle.LongitudinalFriction,
			vehicle.LateralFriction,
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
		return CreateWheelDefinition(wheel, worldScale, Vector3.Zero);
	}

	private static PhysicsVehicleWheelDefinition CreateWheelDefinition(VehicleWheel wheel, Vector3 worldScale, Vector3 centerOfMassOffset)
	{
		var radiusScale = MathF.Max(MathF.Abs(worldScale.X), MathF.Abs(worldScale.Z));
		return new PhysicsVehicleWheelDefinition(
			wheel.VisualEntity,
			Multiply(wheel.Position, worldScale) - centerOfMassOffset,
			Multiply(wheel.SuspensionForcePoint, worldScale) - centerOfMassOffset,
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
			NormalizedTorque = CreateFlatCurve(1.0f)
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
				LongitudinalFriction = CreateFlatCurve(definition.LongitudinalFriction),
				LateralFriction = CreateFlatCurve(definition.LateralFriction)
			};
			wheels[index] = wheelSettings;
		}

		return wheels;
	}

	private static LinearCurve CreateFlatCurve(float value)
	{
		var curve = new LinearCurve();
		curve.AddPoint(0.0f, value);
		curve.AddPoint(1.0f, value);
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

	private static bool TryDecomposeWheelTransform(Matrix4x4 wheelMatrix, out Quaternion rotation, out Vector3 position)
	{
		if (Matrix4x4.Decompose(wheelMatrix, out _, out rotation, out position) &&
		    (position != Vector3.Zero || HasRowTranslation(wheelMatrix)))
		{
			return true;
		}

		var transposed = Matrix4x4.Transpose(wheelMatrix);
		if (Matrix4x4.Decompose(transposed, out _, out rotation, out position))
		{
			return true;
		}

		rotation = Quaternion.Identity;
		position = Vector3.Zero;
		return false;
	}

	private static bool HasRowTranslation(Matrix4x4 matrix)
	{
		return MathF.Abs(matrix.M41) > 0.0001f ||
		       MathF.Abs(matrix.M42) > 0.0001f ||
		       MathF.Abs(matrix.M43) > 0.0001f;
	}

	private static bool WheelsEqual(PhysicsVehicleWheelDefinition[] left, PhysicsVehicleWheelDefinition[] right)
	{
		if (ReferenceEquals(left, right))
		{
			return true;
		}

		if (left.Length != right.Length)
		{
			return false;
		}

		for (var index = 0; index < left.Length; index++)
		{
			if (WheelEqual(left[index], right[index]) == false)
			{
				return false;
			}
		}

		return true;
	}

	private static bool WheelEqual(PhysicsVehicleWheelDefinition left, PhysicsVehicleWheelDefinition right)
	{
		return left.VisualEntity == right.VisualEntity &&
		       NearEqual(left.Position, right.Position) &&
		       NearEqual(left.SuspensionForcePoint, right.SuspensionForcePoint) &&
		       NearEqual(left.SuspensionDirection, right.SuspensionDirection) &&
		       NearEqual(left.SteeringAxis, right.SteeringAxis) &&
		       NearEqual(left.WheelUp, right.WheelUp) &&
		       NearEqual(left.WheelForward, right.WheelForward) &&
		       NearEqual(left.SuspensionMinLength, right.SuspensionMinLength) &&
		       NearEqual(left.SuspensionMaxLength, right.SuspensionMaxLength) &&
		       NearEqual(left.SuspensionPreloadLength, right.SuspensionPreloadLength) &&
		       left.SuspensionMode == right.SuspensionMode &&
		       NearEqual(left.SuspensionFrequencyOrStiffness, right.SuspensionFrequencyOrStiffness) &&
		       NearEqual(left.SuspensionDamping, right.SuspensionDamping) &&
		       NearEqual(left.Radius, right.Radius) &&
		       NearEqual(left.Width, right.Width) &&
		       NearEqual(left.Inertia, right.Inertia) &&
		       NearEqual(left.MaxSteerAngle, right.MaxSteerAngle) &&
		       NearEqual(left.MaxBrakeTorque, right.MaxBrakeTorque) &&
		       NearEqual(left.MaxHandBrakeTorque, right.MaxHandBrakeTorque) &&
		       left.EnableSuspensionForcePoint == right.EnableSuspensionForcePoint &&
		       left.Steer == right.Steer &&
		       left.Drive == right.Drive &&
		       left.HandBrake == right.HandBrake;
	}

	private static bool NearEqual(Vector3 left, Vector3 right, float tolerance = 0.0001f)
	{
		return MathF.Abs(left.X - right.X) <= tolerance &&
		       MathF.Abs(left.Y - right.Y) <= tolerance &&
		       MathF.Abs(left.Z - right.Z) <= tolerance;
	}

	private static bool NearEqual(float left, float right, float tolerance = 0.0001f)
	{
		return MathF.Abs(left - right) <= tolerance;
	}
}

internal sealed class PhysicsVehicleState
{
	public PhysicsVehicleState(
		Entity entity,
		BodyID bodyId,
		PhysicsBodyState bodyState,
		VehicleConstraint constraint,
		VehicleCollisionTester collisionTester,
		WheeledVehicleController controller,
		PhysicsVehicleDefinition definition)
	{
		Entity = entity;
		BodyId = bodyId;
		BodyState = bodyState;
		Constraint = constraint;
		CollisionTester = collisionTester;
		Controller = controller;
		Definition = definition;
		WheelInterpolation = new PhysicsBodyInterpolationState[definition.Wheels.Length];
	}

	public Entity Entity { get; }
	public BodyID BodyId { get; }
	public PhysicsBodyState BodyState { get; }
	public VehicleConstraint Constraint { get; }
	public VehicleCollisionTester CollisionTester { get; }
	public WheeledVehicleController Controller { get; }
	public PhysicsVehicleDefinition Definition { get; set; }
	public float LogTimer { get; set; }

	public PhysicsBodyInterpolationState[] WheelInterpolation { get; }

	public void Dispose(PhysicsWorldState state)
	{
		state.PhysicsSystem.RemoveStepListener(Constraint);
		state.PhysicsSystem.RemoveConstraint(Constraint);
		state.BodiesByEntity.Remove(Entity);
		state.BodiesByBodyId.Remove(BodyId);
		state.BodyInterface.RemoveAndDestroyBody(BodyId);
		Constraint.Dispose();
		CollisionTester.Dispose();
		BodyState.Dispose();
	}
}

internal readonly record struct PhysicsVehicleDefinition(
	Vector3 Position,
	Quaternion Rotation,
	Vector3 BoxHalfExtents,
	Vector3 BoxCenter,
	Vector3 CenterOfMassOffset,
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
	float LongitudinalFriction,
	float LateralFriction,
	PhysicsVehicleWheelDefinition[] Wheels)
{
	public MotionType MotionType => MotionType.Dynamic;

	public Vector3 GetEntityPosition()
	{
		return GetEntityPosition(Position, Rotation);
	}

	public Vector3 GetEntityPosition(Vector3 bodyPosition, Quaternion bodyRotation)
	{
		return bodyPosition - Vector3.Transform(CenterOfMassOffset, bodyRotation);
	}

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

using System.Numerics;
using JoltPhysicsSharp;
using WolfEngine.ECS;

namespace WolfEngine.Physics;

public struct VehicleWheel
{
	public Entity VisualEntity;
	public Vector3 Position;
	public Vector3 SuspensionDirection;
	public Vector3 SuspensionForcePoint;
	public Vector3 SteeringAxis;
	public Vector3 WheelUp;
	public Vector3 WheelForward;
	public float SuspensionMinLength;
	public float SuspensionMaxLength;
	public float SuspensionPreloadLength;
	public SpringMode SuspensionMode;
	public float SuspensionFrequencyOrStiffness;
	public float SuspensionDamping;
	public float Radius;
	public float Width;
	public float Inertia;
	public float MaxSteerAngle;
	public float MaxBrakeTorque;
	public float MaxHandBrakeTorque;
	public bool EnableSuspensionForcePoint;
	public bool Steer;
	public bool Drive;
	public bool HandBrake;

	public static VehicleWheel CreateDefault(Vector3 position, bool steer, bool drive, bool handBrake)
	{
		return new VehicleWheel
		{
			VisualEntity = default,
			Position = position,
			SuspensionDirection = -Vector3.UnitY,
			SuspensionForcePoint = position,
			SteeringAxis = Vector3.UnitY,
			WheelUp = Vector3.UnitY,
			WheelForward = Vector3.UnitZ,
			SuspensionMinLength = 0.05f,
			SuspensionMaxLength = 0.35f,
			SuspensionPreloadLength = 0.15f,
			SuspensionMode = SpringMode.FrequencyAndDamping,
			SuspensionFrequencyOrStiffness = 2.0f,
			SuspensionDamping = 0.5f,
			Radius = 0.35f,
			Width = 0.2f,
			Inertia = 1.0f,
			MaxSteerAngle = steer ? 0.6f : 0.0f,
			MaxBrakeTorque = 1500.0f,
			MaxHandBrakeTorque = handBrake ? 3000.0f : 0.0f,
			EnableSuspensionForcePoint = false,
			Steer = steer,
			Drive = drive,
			HandBrake = handBrake
		};
	}
}

public struct Vehicle : IEntityComponent
{
	public Vector3 CenterOfMassOffset;
	public Vector3 Up;
	public Vector3 Forward;
	public float MaxPitchRollAngle;
	public float DifferentialLimitedSlipRatio;
	public float DifferentialRatio;
	public float DifferentialLeftRightSplit;
	public float DifferentialEngineTorqueRatio;
	public float EngineMaxTorque;
	public float EngineMinRpm;
	public float EngineMaxRpm;
	public float EngineInertia;
	public float EngineAngularDamping;
	public float TransmissionShiftUpRpm;
	public float TransmissionShiftDownRpm;
	public float TransmissionSwitchTime;
	public float TransmissionClutchReleaseTime;
	public float TransmissionSwitchLatency;
	public float TransmissionClutchStrength;
	public float TransmissionForwardGearRatio;
	public float TransmissionReverseGearRatio;
	public VehicleWheel FrontLeft;
	public VehicleWheel FrontRight;
	public VehicleWheel RearLeft;
	public VehicleWheel RearRight;

	public void ApplyDefaultValues(World world, Entity entity)
	{
		CenterOfMassOffset = new Vector3(0.0f, -0.35f, 0.0f);
		Up = Vector3.UnitY;
		Forward = Vector3.UnitZ;
		MaxPitchRollAngle = 0.6f;
		DifferentialLimitedSlipRatio = 1.4f;
		DifferentialRatio = 3.42f;
		DifferentialLeftRightSplit = 0.5f;
		DifferentialEngineTorqueRatio = 1.0f;
		EngineMaxTorque = 800.0f;
		EngineMinRpm = 900.0f;
		EngineMaxRpm = 6000.0f;
		EngineInertia = 0.5f;
		EngineAngularDamping = 0.2f;
		TransmissionShiftUpRpm = 4500.0f;
		TransmissionShiftDownRpm = 1500.0f;
		TransmissionSwitchTime = 0.2f;
		TransmissionClutchReleaseTime = 0.15f;
		TransmissionSwitchLatency = 0.0f;
		TransmissionClutchStrength = 10.0f;
		TransmissionForwardGearRatio = 3.6f;
		TransmissionReverseGearRatio = 3.2f;
		FrontLeft = VehicleWheel.CreateDefault(new Vector3(-0.9f, 0.0f, 1.2f), steer: true, drive: true, handBrake: false);
		FrontRight = VehicleWheel.CreateDefault(new Vector3(0.9f, 0.0f, 1.2f), steer: true, drive: true, handBrake: false);
		RearLeft = VehicleWheel.CreateDefault(new Vector3(-0.9f, 0.0f, -1.2f), steer: false, drive: false, handBrake: true);
		RearRight = VehicleWheel.CreateDefault(new Vector3(0.9f, 0.0f, -1.2f), steer: false, drive: false, handBrake: true);
	}

	public static Vehicle CreateDefault()
	{
		var vehicle = new Vehicle();
		vehicle.ApplyDefaultValues(null!, default);
		return vehicle;
	}
}

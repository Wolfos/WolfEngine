using System.Numerics;
using WolfEngine.ECS;

namespace WolfEngine.Physics;

public enum RigidbodyBodyType
{
	Static,
	Kinematic,
	Dynamic
}

public struct Rigidbody : IEntityComponent
{
	public RigidbodyBodyType BodyType;
	public float Mass;
	public Vector3 LinearVelocity;
	public Vector3 AngularVelocity;
	public float GravityFactor;
	public bool StartActivated;
	public bool AllowSleeping;
	public bool UseManifoldReduction;
	public bool IsSensor;
	
	public void ApplyDefaultValues()
	{
		BodyType = RigidbodyBodyType.Dynamic;
		Mass = 1.0f;
		LinearVelocity = Vector3.Zero;
		AngularVelocity = Vector3.Zero;
		GravityFactor = 1.0f;
		StartActivated = true;
		AllowSleeping = true;
		UseManifoldReduction = false;
		IsSensor = false;
	}

	public static Rigidbody CreateDefault()
	{
		var rb = new Rigidbody();
		rb.ApplyDefaultValues();
		return rb;
	}
}

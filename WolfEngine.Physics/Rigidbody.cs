using System.Numerics;
using WolfEngine.ECS;

namespace WolfEngine.Physics;

public enum RigidbodyBodyType
{
	Static,
	Kinematic,
	Dynamic
}

/// <summary>Controls how dynamic bodies render between fixed steps.</summary>
public enum RigidbodyInterpolation
{
	None,

	/// <summary>Blends the last two fixed-step poses.</summary>
	Interpolate,

	/// <summary>Advances the latest pose using its velocity.</summary>
	Extrapolate
}

public struct Rigidbody : IEntityComponent
{
	public RigidbodyBodyType BodyType;
	public float Mass;
	[NotSerialized]
	public Vector3 LinearVelocity;
	[NotSerialized]
	public Vector3 AngularVelocity;
	public float GravityFactor;
	public bool StartActivated;
	public bool AllowSleeping;
	public bool UseManifoldReduction;
	public bool IsSensor;
	public RigidbodyInterpolation Interpolation;
	[NotSerialized]
	[HideFromEditor]
	internal bool PhysicsCacheValid;
	[NotSerialized]
	[HideFromEditor]
	internal RigidbodyBodyType CachedBodyType;
	[NotSerialized]
	[HideFromEditor]
	internal float CachedMass;
	[NotSerialized]
	[HideFromEditor]
	internal Vector3 CachedLinearVelocity;
	[NotSerialized]
	[HideFromEditor]
	internal Vector3 CachedAngularVelocity;
	[NotSerialized]
	[HideFromEditor]
	internal float CachedGravityFactor;
	[NotSerialized]
	[HideFromEditor]
	internal bool CachedStartActivated;
	[NotSerialized]
	[HideFromEditor]
	internal bool CachedAllowSleeping;
	[NotSerialized]
	[HideFromEditor]
	internal bool CachedUseManifoldReduction;
	[NotSerialized]
	[HideFromEditor]
	internal bool CachedIsSensor;
	
	public void ApplyDefaultValues(World world, Entity entity)
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
		Interpolation = RigidbodyInterpolation.None;
		PhysicsCacheValid = false;
	}

	public static Rigidbody CreateDefault()
	{
		var rb = new Rigidbody();
		rb.ApplyDefaultValues(null!, new Entity());
		return rb;
	}
}

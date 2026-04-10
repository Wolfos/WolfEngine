using WolfEngine.ECS;

namespace WolfEngine.Physics;

public struct VehicleInput : IEntityComponent
{
	public float Throttle;
	public float Brake;
	public float Steer;
	public float HandBrake;

	public void ApplyDefaultValues(World world, Entity entity)
	{
		Throttle = 0.0f;
		Brake = 0.0f;
		Steer = 0.0f;
		HandBrake = 0.0f;
	}

	public static VehicleInput CreateDefault()
	{
		var input = new VehicleInput();
		input.ApplyDefaultValues(null!, default);
		return input;
	}
}

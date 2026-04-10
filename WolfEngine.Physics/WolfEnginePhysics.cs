using WolfEngine.ECS;

namespace WolfEngine.Physics;

public static class WolfEnginePhysics
{
	public static void AddDefaultSystems(IWorldManager worldManager)
	{
		worldManager.AddSystem(new VehicleSystem(), SystemExecutionGroup.Gameplay);
		worldManager.AddSystem(new RigidbodySystem(), SystemExecutionGroup.Gameplay);
	}
}

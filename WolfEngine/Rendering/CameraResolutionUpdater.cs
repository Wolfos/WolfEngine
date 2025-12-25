using WolfEngine.ECS;

namespace WolfEngine.Rendering;

public class CameraResolutionUpdater: IUpdateable
{
	private readonly World _world;

	public CameraResolutionUpdater(World world)
	{
		_world = world;
	}

	public void Update(float deltaTime)
	{
		var screenResolution = Screen.CurrentResolution;
		foreach (var entry in _world.View<Transform, Camera>())
		{
			ref var camera = ref entry.Second;

			if (camera.ScreenResolution == screenResolution) continue;
			
			camera.ScreenResolution = screenResolution;
			camera.SetPerspective(camera.Fov);
		}
	}
}
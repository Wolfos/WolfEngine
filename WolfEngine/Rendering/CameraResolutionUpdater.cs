using WolfEngine.ECS;

namespace WolfEngine.Rendering;

public class CameraResolutionUpdater: IUpdateable
{
	public void Update(float deltaTime, World world)
	{
		var screenResolution = Screen.CurrentResolution;
		foreach (var entry in world.View<Camera>())
		{
			ref var camera = ref entry.First;
			if (camera.AutoResolution == false)
			{
				continue;
			}

			if (camera.ScreenResolution == screenResolution) continue;
			
			camera.ScreenResolution = screenResolution;
			camera.SetPerspective(camera.Fov);
		}
	}

	public WorldTag GetTag() => WorldTag.All;
}

using Microsoft.Extensions.DependencyInjection;

namespace WolfEngine.ECS;

public class Ecs
{
	public static void ConfigureServices(IServiceCollection services)
	{
		services.AddSingleton<IWorldManager, WorldManager>();
	}
}
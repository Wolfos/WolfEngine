using Microsoft.Extensions.DependencyInjection;
using WolfEngine.ECS;

namespace WolfEngine.Editor;

public static class Program
{
	public static void Main()
	{
		var services = new ServiceCollection();
		WolfEngine.ConfigureServices(services);
		
		ConfigureServices(services);
		
		var provider = services.BuildServiceProvider();
		
		// TODO: This probably won't work before ImGUI is loaded and if we place it after, in Main, it will never return
		EditorPreferences.Load();
	}

	public static void ConfigureServices(ServiceCollection services)
	{
		services.AddSingleton<WolfEngineEditor>();
	}
}
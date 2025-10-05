using Microsoft.Extensions.DependencyInjection;

namespace WolfEngine;

class Program
{
    private const int ScreenWidth = 1280;
    private const int ScreenHeight = 720;

    static void Main(string[] args)
    {
        var services = new ServiceCollection();
        ConfigureServices(services);
        using var provider = services.BuildServiceProvider();

		if (OperatingSystem.IsMacOS())
		{
			var renderer = ActivatorUtilities.CreateInstance<WolfRendererMetal>(provider, ScreenWidth, ScreenHeight);
			var game = new Game(renderer);
			game.Run();
        }
		else if (OperatingSystem.IsWindows())
		{
			_ = ActivatorUtilities.CreateInstance<WolfRendererD3D>(provider, ScreenWidth, ScreenHeight);
		}
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IShaderCompiler, ShaderCompiler>();
    }
}

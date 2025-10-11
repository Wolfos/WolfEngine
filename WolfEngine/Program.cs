using Microsoft.Extensions.DependencyInjection;

namespace WolfEngine;

class Program
{

    static void Main(string[] args)
    {
        var services = new ServiceCollection();
        ConfigureServices(services);

        var provider = services.BuildServiceProvider();
        provider.GetService<Game>();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IShaderCompiler, ShaderCompiler>();
        services.AddSingleton<IMaterialFactory, MaterialFactory>();
        
        if (OperatingSystem.IsMacOS())
        {
            services.AddSingleton<IRenderer, WolfRendererMetal>();
            services.AddSingleton<Game>();
        }
        else if (OperatingSystem.IsWindows())
        {
            _ = ActivatorUtilities.CreateInstance<WolfRendererD3D>(services.BuildServiceProvider());
        }
    }
}

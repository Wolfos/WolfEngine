using Microsoft.Extensions.DependencyInjection;
using WolfEngine.Rendering;

namespace WolfEngine;

class Program
{
    private static void Main()
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
        services.AddSingleton<IArenaAllocator, ArenaAllocator>();
        services.AddSingleton<IRenderCommandFactory, RenderCommandFactory>();

        services.AddSingleton<RenderGraphResourceRegistry>();
        services.AddSingleton<RenderGraph>();
        services.AddSingleton<RenderGraphFrameBuilder>();
        
        if (OperatingSystem.IsMacOS())
        {
            services.AddSingleton<IRenderer, WolfRendererMetal>();
            services.AddSingleton<Game>();
        }
        else if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<IRenderer, WolfRendererD3D>();
            services.AddSingleton<Game>();
        }
    }
}

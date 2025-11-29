using Microsoft.Extensions.DependencyInjection;
using WolfEngine.Importing;
using WolfEngine.Input;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Passes;

namespace WolfEngine;

class Program
{
    private static void Main()
    {
        var services = new ServiceCollection();
        ConfigureServices(services);

        var provider = services.BuildServiceProvider();
        // Toggle D3D12 debug layer here when needed
        GraphicsConfig.EnableD3DDebugLayer = true;

        var game = provider.GetRequiredService<Game>();
        game.Run();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IShaderCompiler, ShaderCompiler>();
        services.AddSingleton<IMaterialFactory, MaterialFactory>();
        services.AddSingleton<IThreeDFileImporter, ThreeDFileImporter>();
        services.AddSingleton<IArenaAllocator, ArenaAllocator>();
        services.AddSingleton<IRenderCommandFactory, RenderCommandFactory>();
        services.AddSingleton<IInputSystem, InputSystem>();
        services.AddSingleton<DeferredLightingPass>();

        services.AddSingleton<RenderGraphResourceRegistry>();
        services.AddSingleton<RenderGraph>();
        
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

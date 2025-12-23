using Microsoft.Extensions.DependencyInjection;
using WolfEngine.Importing;
using WolfEngine.Input;
using WolfEngine.Platform;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Passes;
using WolfEngine.Rendering.UI;

namespace WolfEngine;

class Program
{
    private static void Main()
    {
        var services = new ServiceCollection();
        ConfigureServices(services);

        var provider = services.BuildServiceProvider();
        // Toggle D3D12 debug layer here when needed
        GraphicsConfig.EnableD3DDebugLayer = false;

        var game = provider.GetRequiredService<Game>();
        game.Run();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IShaderCompiler, ShaderCompiler>();
        services.AddSingleton<IImageLoader, StbImageLoader>();
        services.AddSingleton<ITextureFactory, TextureFactory>();
        services.AddSingleton<IMaterialFactory, MaterialFactory>();
        services.AddSingleton<IThreeDFileImporter, ThreeDFileImporter>();
        services.AddSingleton<IArenaAllocator, ArenaAllocator>();
        services.AddSingleton<IInputSystem, InputSystem>();
        services.AddSingleton<DeferredLightingPass>();
        services.AddSingleton<ImGuiUiSystem>();
        services.AddSingleton<IImGuiInputSink>(sp => sp.GetRequiredService<ImGuiUiSystem>());
        services.AddSingleton<IUiFrameProvider>(sp => sp.GetRequiredService<ImGuiUiSystem>());

        services.AddSingleton<RenderGraphResourceRegistry>();
        services.AddSingleton<RenderGraph>();
        services.AddSingleton<SkyboxRenderer>();
        
        if (OperatingSystem.IsMacOS())
        {
            services.AddSingleton<IMacOSInputHandler, MacOsInputHandlerHandler>();
            services.AddSingleton<IImGuiRenderer, MetalImGuiRenderer>();
            services.AddSingleton<IRenderer, WolfRendererMetal>();
            services.AddSingleton<Game>();
        }
        else if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<IImGuiRenderer, D3D12ImGuiRenderer>();
            services.AddSingleton<IRenderer, WolfRendererD3D>();
            services.AddSingleton<Game>();
        }
    }
}

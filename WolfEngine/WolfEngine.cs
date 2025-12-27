using Microsoft.Extensions.DependencyInjection;
using WolfEngine.ECS;
using WolfEngine.Importing;
using WolfEngine.Input;
using WolfEngine.Platform;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Passes;
using WolfEngine.Rendering.UI;

namespace WolfEngine;

public static class WolfEngine
{
	public static Action<float> OnUpdate;
	public static Action<float> OnPreRender;
	public static void ConfigureServices(IServiceCollection services)
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
			services.AddSingleton<RenderPipeline>();
		}
		else if (OperatingSystem.IsWindows())
		{
			services.AddSingleton<IImGuiRenderer, D3D12ImGuiRenderer>();
			services.AddSingleton<IRenderer, WolfRendererD3D>();
			services.AddSingleton<RenderPipeline>();
		}
		
		Ecs.ConfigureServices(services);
		var provider = services.BuildServiceProvider();
		AddDefaultSystems(provider.GetRequiredService<IWorldManager>());
	}
	public static void AddDefaultSystems(IWorldManager worldManager)
	{
		worldManager.AddSystem<CameraResolutionUpdater>();
		worldManager.AddSystem<TransformSystem>();
	}

	public static void GameLoop(float deltaTime)
	{
		
	}
}
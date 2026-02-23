using Microsoft.Extensions.DependencyInjection;
using WolfEngine.ECS;
using WolfEngine.Importing;
using WolfEngine.Input;
using WolfEngine.Platform;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Passes;
using WolfEngine.Rendering.UI;
using WolfEngine.Utility;

namespace WolfEngine;

public static class WolfEngine
{
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
		services.AddSingleton<TransparentForwardPass>();
		services.AddSingleton<ShadowMapPass>();
		services.AddSingleton<GpuDrawPass>();
		services.AddSingleton<ImGuiUiSystem>();
		services.AddSingleton<IImGuiInputSink>(sp => sp.GetRequiredService<ImGuiUiSystem>());
		services.AddSingleton<IUiFrameProvider>(sp => sp.GetRequiredService<ImGuiUiSystem>());
		services.AddSingleton<IMainThreadDispatcher, MainThreadDispatcher>();
		services.AddSingleton<IFileDialogService, FileDialogService>();
		services.AddSingleton<GpuDrawResources>();
		services.AddSingleton<GpuDrawDatabase>();
		services.AddSingleton<GpuDrawHardeningStats>();
		services.AddSingleton<BindlessResourceRegistry>();

		services.AddSingleton<RenderGraphResourceRegistry>();
		services.AddSingleton<RenderGraph>();
		services.AddSingleton<SkyboxRenderer>();
		
		services.AddSingleton<ISceneBuilder, SceneBuilder>();
		
		if (OperatingSystem.IsMacOS())
		{
			services.AddSingleton<IMacOSInputHandler, MacOsInputHandlerHandler>();
			services.AddSingleton<IImGuiRenderer, MetalImGuiRenderer>();
			services.AddSingleton<IRenderer, WolfRendererMetal>();
			services.AddSingleton<IRenderPipeline, RenderPipeline>();
		}
		else if (OperatingSystem.IsWindows())
		{
			services.AddSingleton<IImGuiRenderer, D3D12ImGuiRenderer>();
			services.AddSingleton<IRenderer, WolfRendererD3D>();
			services.AddSingleton<IRenderPipeline, RenderPipeline>();
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
}

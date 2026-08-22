using Microsoft.Extensions.DependencyInjection;
using WolfEngine.Animation;
using WolfEngine.ECS;
using WolfEngine.AssetPipeline;
using WolfEngine.Importing;
using WolfEngine.Input;
using WolfEngine.Platform;
using WolfEngine.Profiling;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Backend.D3D12;
using WolfEngine.Rendering.Backend.Metal;
using WolfEngine.Rendering.Passes;
using WolfEngine.Rendering.UI;
using WolfEngine.Rendering.Shaders;
using WolfEngine.Utility;

namespace WolfEngine;

public static class WolfEngine
{
	public static void ConfigureServices(IServiceCollection services)
	{
		services.AddSingleton<EngineShaderCatalog>();
		services.AddSingleton<IImageLoader, StbImageLoader>();
		services.AddSingleton<ITextureFactory, TextureFactory>();
		services.AddSingleton<IMaterialFactory, MaterialFactory>();
		services.AddSingleton<IAssetPipelineIndex, AssetPipelineIndex>();
		services.AddSingleton<IAssetMetadataStore, AssetMetadataStore>();
		services.AddSingleton<IRuntimeArtifactTargetProvider, RuntimeArtifactTargetProvider>();
		services.AddSingleton<IArenaAllocator, ArenaAllocator>();
		services.AddSingleton<IInputSystem, InputSystem>();
		services.AddSingleton<ImGuiUiSystem>();
		services.AddSingleton<IImGuiInputSink>(sp => sp.GetRequiredService<ImGuiUiSystem>());
		services.AddSingleton<IUiFrameProvider>(sp => sp.GetRequiredService<ImGuiUiSystem>());
		services.AddSingleton<IGameplayUiFrameProvider>(NullGameplayUiFrameProvider.Instance);
		services.AddSingleton<GameplayUiGpuRenderer>();
		services.AddSingleton<EditorViewportStateBus>();
		services.AddSingleton(new RenderPresentationOptions());
		services.AddSingleton<IMainThreadDispatcher, MainThreadDispatcher>();
		services.AddSingleton<EditorFrameCoordinator>();
		services.AddSingleton<RenderFrameCoordinator>();
		services.AddSingleton<IFileDialogService, FileDialogService>();
		services.AddSingleton<IFileManagerService, FileManagerService>();
		services.AddSingleton<GpuDrawResources>();
		services.AddSingleton<GpuDrawHardeningStats>();
		services.AddSingleton<GpuProfiler>();
		services.AddSingleton<BindlessResourceRegistry>();
		services.AddSingleton<RenderGraphResourceRegistry>();
		services.AddSingleton<RenderGraph>();
		
#pragma warning disable CA1416
		services.AddSingleton<WindowChromeController>();
		services.AddSingleton<IWindowChromeController>(sp => sp.GetRequiredService<WindowChromeController>());
#pragma warning restore CA1416

		
		services.AddSingleton<ISceneBuilder, SceneBuilder>();
		
		if (OperatingSystem.IsMacOS())
		{
			services.AddSingleton<IMacOSInputHandler, MacOsInputHandler>();
			services.AddSingleton<IGpuDrawBackendBridge, MetalGpuDrawBackendBridge>();
			services.AddSingleton<IImGuiRenderer, MetalUiRenderer>();
			services.AddSingleton<IRenderer, WolfRendererMetal>();
			services.AddSingleton<IRenderPipeline, RenderPipeline>();
		}
		else if (OperatingSystem.IsWindows())
		{
			services.AddSingleton<IGpuDrawBackendBridge, D3D12GpuDrawBackendBridge>();
			services.AddSingleton<IImGuiRenderer, D3D12UiRenderer>();
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
		// Before TransformSystem, so exposed bone sockets propagate in the frame they are posed.
		worldManager.AddSystem<AnimationSystem>();
		worldManager.AddSystem<TransformSystem>();
	}
}

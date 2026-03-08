using Microsoft.Extensions.DependencyInjection;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;
using WolfEngine.Rendering.UI;
using WolfEngine.Editor.UI;

namespace WolfEngine.Editor;

public static class Program
{
	public static void Main()
	{
		var services = new ServiceCollection();
		WolfEngine.ConfigureServices(services);
		
		ConfigureServices(services);
		
		var provider = services.BuildServiceProvider();
		
		provider.GetRequiredService<IUiFrameProvider>();
		provider.GetRequiredService<IIconManager>();
		EditorPreferences.Load();
		var projectService = provider.GetRequiredService<IEditorProjectService>();
		var lastProjectPath = EditorPreferences.GetLastProjectPath();
		if (string.IsNullOrWhiteSpace(lastProjectPath) == false)
		{
			projectService.OpenProject(lastProjectPath, out _);
		}
		
		var editor = provider.GetRequiredService<WolfEngineEditor>();
		var editorThread = new Thread(editor.Run) { IsBackground = true, Name = "EditorThread" };
		editorThread.Start();
		
		var renderPipeline = provider.GetRequiredService<IRenderPipeline>();
		renderPipeline.Run();
		
		editor.Stop();
		editorThread.Join();
	}

	private static void ConfigureServices(IServiceCollection services)
	{
		services.AddSingleton<WolfEngineEditor>();
		services.AddSingleton<EditorCameraContext>();
		services.AddSingleton<FramerateTool>();
		services.AddSingleton<IMaterialTypeRegistry, MaterialTypeRegistry>();
		services.AddSingleton<IEditorProjectService, EditorProjectService>();
		services.AddSingleton<IAssetSelectionService, AssetSelectionService>();
		services.AddSingleton<ITextureAssetMetaStore, TextureAssetMetaStore>();
		services.AddSingleton<IMaterialAssetStore, MaterialAssetStore>();
		services.AddSingleton<IMaterialAssetCreator, MaterialAssetCreator>();
		services.AddSingleton<IMaterialAssetRuntimeBuilder, MaterialAssetRuntimeBuilder>();
		services.AddSingleton<ITextureAssetImporter, TextureAssetImporter>();
		services.AddSingleton<IMenuBar, MenuBar>();
		services.AddSingleton<IImageLoader, ImageLoader>();
		services.AddSingleton<IIconManager, IconManager>();
		services.AddSingleton<TransformGizmoController>();
		services.AddSingleton<EditorGui>();
		
		ConfigureEditorWindows(services);
	}

	private static void ConfigureEditorWindows(IServiceCollection services)
	{
		services.AddSingleton<IComponentEditor, ComponentsWindow>();
		services.AddTransient<EntitiesWindow>();
		services.AddTransient<AssetsWindow>();
		services.AddTransient<AssetEditorWindow>();
		services.AddTransient<ProfilerWindow>();
		services.AddTransient<SceneWindow>();
	}
}

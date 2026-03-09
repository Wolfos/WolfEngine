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
		
		AssetDatabase.SetInstanceRegistry(provider.GetRequiredService<IAssetInstanceRegistry>());
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
		AssetDatabase.ClearInstanceRegistry();
	}

	private static void ConfigureServices(IServiceCollection services)
	{
		services.AddSingleton<WolfEngineEditor>();
		services.AddSingleton<EditorCameraContext>();
		services.AddSingleton<FramerateTool>();
		services.AddSingleton<IMaterialTypeRegistry, MaterialTypeRegistry>();
		services.AddSingleton<IDataAssetTypeRegistry, DataAssetTypeRegistry>();
		services.AddSingleton<IAssetInstanceRegistry, EditorAssetInstanceRegistry>();
		services.AddSingleton<IEditorProjectService, EditorProjectService>();
		services.AddSingleton<IAssetSelectionService, AssetSelectionService>();
		services.AddSingleton<ITextureAssetStore, TextureAssetStore>();
		services.AddSingleton<IMaterialAssetStore, MaterialAssetStore>();
		services.AddSingleton<IDataAssetStore, DataAssetStore>();
		services.AddSingleton<IMaterialAssetCreator, MaterialAssetCreator>();
		services.AddSingleton<IDataAssetCreator, DataAssetCreator>();
		services.AddSingleton<ITextureAssetImporter, TextureAssetImporter>();
		services.AddSingleton<IDataAssetRuntimeResolver, DataAssetRuntimeResolver>();
		services.AddSingleton<IMaterialRuntimeAssetResolver, MaterialRuntimeAssetResolver>();
		services.AddSingleton<ITextureRuntimeAssetResolver, TextureRuntimeAssetResolver>();
		services.AddSingleton<IPropertyDrawerRegistry, PropertyDrawerRegistry>();
		services.AddSingleton<TextureAssetEditor>();
		services.AddSingleton<MaterialAssetEditor>();
		services.AddSingleton<DataAssetEditor>();
		services.AddSingleton<IEditorAssetHandler, TextureEditorAssetHandler>();
		services.AddSingleton<IEditorAssetHandler, MaterialEditorAssetHandler>();
		services.AddSingleton<IEditorAssetHandler, DataEditorAssetHandler>();
		services.AddSingleton<IEditorAssetHandlerRegistry, EditorAssetHandlerRegistry>();
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

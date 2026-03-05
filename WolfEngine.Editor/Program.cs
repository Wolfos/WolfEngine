using Microsoft.Extensions.DependencyInjection;
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
		services.AddTransient<ProfilerWindow>();
		services.AddTransient<SceneWindow>();
	}
}

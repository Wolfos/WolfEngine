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
		EditorPreferences.Load();
		
		var editor = provider.GetRequiredService<WolfEngineEditor>();
		var editorThread = new Thread(editor.Run) { IsBackground = true, Name = "EditorThread" };
		editorThread.Start();
		
		var renderPipeline = provider.GetRequiredService<IRenderPipeline>();
		renderPipeline.Run();
		
		editor.Stop();
		editorThread.Join();
	}

	public static void ConfigureServices(ServiceCollection services)
	{
		services.AddSingleton<WolfEngineEditor>();
		services.AddSingleton<IComponentEditor, ComponentEditor>();
		services.AddSingleton<FramerateTool>();
		services.AddSingleton<IMenuBar, MenuBar>();
		services.AddSingleton<EditorGui>();
	}
}

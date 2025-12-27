using Microsoft.Extensions.DependencyInjection;
using System.Threading;
using WolfEngine;
using WolfEngine.Rendering.UI;
using WolfEngine.ECS;

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
	}
}

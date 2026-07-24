using Microsoft.Extensions.DependencyInjection;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Automation;
using WolfEngine.Editor.Tooling;
using WolfEngine.Editor.Projects;
using WolfEngine.Editor.UI;
using WolfEngine.ECS;
using WolfEngine.Physics;
using WolfEngine.Rendering.UI;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Editor;

/// <summary>Reusable host for the editor's service graph, update thread, and render loop.</summary>
public sealed class EditorApplication : IDisposable
{
	private readonly ServiceProvider _services;
	private bool _disposed;

	private EditorApplication(ServiceProvider services) => _services = services;

	public static EditorApplication Create(string? engineContentRoot = null)
	{
		var services = new ServiceCollection();
		engineContentRoot ??= Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "WolfEngine"));
		WolfEngine.ConfigureServices(services);
		services.AddEditorToolingShaders(new EngineShaderOptions { EngineContentRoot = engineContentRoot });
		services.AddEditorToolingImporter();
		Program.ConfigureServices(services);
		return new EditorApplication(services.BuildServiceProvider());
	}

	public EditorAutomationController CreateCaptureController(EditorAutomationOptions options) =>
		ActivatorUtilities.CreateInstance<EditorAutomationController>(_services, options);

	public EditorRemoteAutomationController CreateAutomationController(string projectPath) =>
		ActivatorUtilities.CreateInstance<EditorRemoteAutomationController>(_services, Path.GetFullPath(projectPath));

	public void Run(EditorAutomationController? captureController = null, EditorRemoteAutomationController? automationController = null)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		var worldManager = _services.GetRequiredService<IWorldManager>();
		worldManager.AddSystem(new VehicleSystem(), SystemExecutionGroup.Gameplay);
		worldManager.AddSystem(_services.GetRequiredService<RigidbodySystem>(), SystemExecutionGroup.Gameplay);
		AssetDatabase.SetInstanceRegistry(_services.GetRequiredService<IAssetInstanceRegistry>());
		_services.GetRequiredService<IUiFrameProvider>();
		_services.GetRequiredService<IIconManager>();

		var editor = _services.GetRequiredService<WolfEngineEditor>();
		if (captureController is not null)
		{
			_services.GetRequiredService<IRenderer>().SetWindowSize(captureController.Resolution);
			editor.SetAutomationController(captureController);
		}
		if (automationController is not null) editor.SetRemoteAutomationController(automationController);

		var lastProjectPath = captureController is null && automationController is null ? LoadLastProjectPath() : null;
		// Arm the loading state before the editor thread starts so its very first
		// submitted frame is the loading screen. The worker waits until the renderer
		// has created its graphics device because asset import may dispatch GPU work.
		var rendererReady = new ManualResetEventSlim(false);
		if (string.IsNullOrWhiteSpace(lastProjectPath) == false)
		{
			var projects = _services.GetRequiredService<IEditorProjectService>();
			var gameplay = _services.GetRequiredService<IGameplayAssemblyHost>();
			var notifications = _services.GetRequiredService<IEditorNotificationService>();
			_services.GetRequiredService<IEditorOperationService>().TryStart(
				"Opening project",
				progress =>
				{
					rendererReady.Wait();
					progress.Report("Loading project assets...");
					if (projects.OpenProject(lastProjectPath, out var error) == false) throw new InvalidOperationException(error);
					progress.Report("Loading gameplay assembly...");
					gameplay.EnsureLoaded();
				},
				failed: exception => notifications.ReportError($"Failed to open project: {exception.Message}"));
		}
		var editorThread = new Thread(editor.Run) { IsBackground = true, Name = "EditorThread" };
		editorThread.Start();
		try
		{
			_services.GetRequiredService<IRenderPipeline>().Run(() =>
			{
				rendererReady.Set();
			});
		}
		finally
		{
			editor.Stop();
			editorThread.Join();
			AssetDatabase.ClearInstanceRegistry();
			automationController?.NotifyStopped();
		}
	}

	private static string? LoadLastProjectPath()
	{
		EditorPreferences.Load();
		return EditorPreferences.GetLastProjectPath();
	}

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		_services.Dispose();
	}
}

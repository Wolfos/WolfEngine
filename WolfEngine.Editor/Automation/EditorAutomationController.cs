using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Editor.Projects;
using WolfEngine.Mathematics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor.Automation;

public sealed class EditorAutomationController
{
	private const float FixedDeltaTime = 1.0f / 60.0f;
	private readonly EditorAutomationOptions _options;
	private readonly IEditorProjectService _projectService;
	private readonly IEditorSceneWorkspace _sceneWorkspace;
	private readonly IEditorPlaySession _playSession;
	private readonly IRenderer _renderer;
	private readonly EditorViewportStateBus _viewportStateBus;
	private readonly IGameplayAssemblyHost _gameplayAssemblyHost;
	private Task<FrameCapture>? _captureTask;
	private DateTime _captureDeadlineUtc;
	private int _completedFrames;
	private bool _initialized;

	public EditorAutomationController(
		EditorAutomationOptions options,
		IEditorProjectService projectService,
		IEditorSceneWorkspace sceneWorkspace,
		IEditorPlaySession playSession,
		IRenderer renderer,
		EditorViewportStateBus viewportStateBus,
		IGameplayAssemblyHost gameplayAssemblyHost)
	{
		_options = options;
		_projectService = projectService;
		_sceneWorkspace = sceneWorkspace;
		_playSession = playSession;
		_renderer = renderer;
		_viewportStateBus = viewportStateBus;
		_gameplayAssemblyHost = gameplayAssemblyHost;
	}

	public bool IsComplete { get; private set; }
	public int ExitCode { get; private set; }
	public float DeltaTime => FixedDeltaTime;
	public bool IsEnabled => true;
	public Int2 Resolution => _options.Resolution;

	public void Initialize()
	{
		if (_initialized) return;
		_initialized = true;
		try
		{
			if (_projectService.OpenProject(_options.ProjectPath, out var projectError) == false)
			{
				Fail(2, projectError);
				return;
			}
			var gameplay = _gameplayAssemblyHost.EnsureLoaded();
			if (gameplay.Generation == 0)
			{
				Fail(3, "Gameplay assembly could not be loaded.");
				return;
			}

			var relativeScenePath = NormalizeProjectPath(_options.ScenePath);
			var scene = _projectService.CurrentAssetDatabase.Assets.SingleOrDefault(asset =>
				asset.Type == AssetType.Scene &&
				string.Equals(Normalize(asset.RelativeAssetPath), relativeScenePath, StringComparison.OrdinalIgnoreCase));
			if (scene is null)
			{
				Fail(2, $"Scene '{_options.ScenePath}' was not found in the project's asset database.");
				return;
			}

			_sceneWorkspace.LoadScene(scene.Id);
			if (_playSession.EnterPlay() == false)
			{
				Fail(3, "Failed to enter Play mode.");
				return;
			}
			if (HasRuntimeCamera(_playSession.RuntimeScene?.World) == false)
			{
				Fail(3, "The Play-mode scene has no active camera.");
				return;
			}

			_viewportStateBus.PublishUiState(new SceneViewportUiState(
				visible: true,
				contentSizePixels: _options.Resolution,
				resolutionScale: 1.0f,
				requestedDebugViewId: SceneDebugViewIds.FinalColor,
				hovered: false, focused: false,
				pointerAvailable: false, pointerCaptured: false,
				rightMousePressStartedHere: false,
				imageMin: System.Numerics.Vector2.Zero,
				imageMax: new System.Numerics.Vector2(_options.Resolution.X, _options.Resolution.Y)));
		}
		catch (Exception exception)
		{
			Fail(2, exception.Message);
		}
	}

	public void OnFrameCompleted()
	{
		if (IsComplete || _initialized == false || _playSession.State != EditorPlayState.Playing) return;
		if (_captureTask is null && ++_completedFrames >= _options.Frames)
		{
			try
			{
				_captureTask = _renderer.CaptureNextFrameAsync();
				_captureDeadlineUtc = DateTime.UtcNow.AddSeconds(30);
			}
			catch (Exception exception) { Fail(4, exception.Message); }
			return;
		}

		if (_captureTask is not null && _captureTask.IsCompleted == false && DateTime.UtcNow > _captureDeadlineUtc)
		{
			Fail(4, "Timed out waiting for the renderer to complete the frame capture.");
			return;
		}

		if (_captureTask is { IsCompleted: true })
		{
			try
			{
				var capture = _captureTask.GetAwaiter().GetResult();
				Directory.CreateDirectory(Path.GetDirectoryName(GetCapturePath())!);
				using var image = Image.LoadPixelData<Rgba32>(capture.Rgba8, capture.Width, capture.Height);
				image.SaveAsPng(GetCapturePath());
				Console.WriteLine($"capture success scene={_options.ScenePath} frames={_completedFrames} resolution={capture.Width}x{capture.Height} path={GetCapturePath()}");
				Complete(0);
			}
			catch (Exception exception) { Fail(5, exception.Message); }
		}
	}

	private string NormalizeProjectPath(string scenePath)
	{
		var fullPath = Path.GetFullPath(Path.IsPathRooted(scenePath) ? scenePath : Path.Combine(_options.ProjectPath, scenePath));
		if (File.Exists(fullPath) == false || fullPath.EndsWith(EditorSceneAssetFile.FileExtension, StringComparison.OrdinalIgnoreCase) == false)
		{
			throw new InvalidOperationException($"Scene path '{scenePath}' must reference an existing {EditorSceneAssetFile.FileExtension} file.");
		}
		return Normalize(Path.GetRelativePath(_options.ProjectPath, fullPath));
	}

	private string GetCapturePath() => Path.GetFullPath(Path.IsPathRooted(_options.CapturePath)
		? _options.CapturePath : Path.Combine(_options.ProjectPath, _options.CapturePath));
	private static string Normalize(string path) => path.Replace('\\', '/');
	private static bool HasRuntimeCamera(World? world)
	{
		if (world is null) return false;
		foreach (var entry in world.View<Camera>()) if (world.IsEnabled(entry.Entity)) return true;
		return false;
	}
	private void Complete(int exitCode)
	{
		ExitCode = exitCode;
		IsComplete = true;
		_playSession.Stop();
		_renderer.RequestShutdown();
	}
	private void Fail(int exitCode, string message)
	{
		Console.Error.WriteLine($"capture failed: {message}");
		Complete(exitCode);
	}
}

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Editor.Projects;
using WolfEngine.Mathematics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.UI;
using WolfEngine.Profiling;
using System.Text.Json;

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
	private readonly GpuProfiler _gpuProfiler;
	private Task<FrameCapture>? _captureTask;
	private Task<IReadOnlyList<GpuProfileFrame>>? _profileTask;
	private DateTime _profileDeadlineUtc;
	private bool _warmupComplete;
	private bool _profileComplete;
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
		IGameplayAssemblyHost gameplayAssemblyHost,
		GpuProfiler gpuProfiler)
	{
		_options = options;
		_projectService = projectService;
		_sceneWorkspace = sceneWorkspace;
		_playSession = playSession;
		_renderer = renderer;
		_viewportStateBus = viewportStateBus;
		_gameplayAssemblyHost = gameplayAssemblyHost;
		_gpuProfiler = gpuProfiler;
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
		if (!_warmupComplete)
		{
			_completedFrames++;
			_warmupComplete = _completedFrames >= _options.Frames;
			if (!_warmupComplete) return;
		}

		if (_options.ProfileFrames > 0 && !_profileComplete)
		{
			if (_profileTask is null)
			{
				var marker = _gpuProfiler.BeginCollection();
				_profileTask = _gpuProfiler.CollectCompletedFramesAsync(marker, _options.ProfileFrames);
				_profileDeadlineUtc = DateTime.UtcNow.AddMinutes(3);
				return;
			}
			if (!_profileTask.IsCompleted)
			{
				if (DateTime.UtcNow > _profileDeadlineUtc) Fail(4, "Timed out waiting for GPU profile frames.");
				return;
			}
			try
			{
				var frames = _profileTask.GetAwaiter().GetResult();
				WriteProfile(frames);
				_gpuProfiler.Enabled = false;
				_profileComplete = true;
			}
			catch (Exception exception) { Fail(5, exception.Message); return; }
		}

		if (_captureTask is null && _captureCompleted == false)
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
				_captureTask = null;
				_captureCompleted = true;
			}
			catch (Exception exception) { Fail(5, exception.Message); return; }
		}

		if (_captureCompleted == false)
		{
			return;
		}

		Complete(0);
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

	private string ResolveProjectPath(string path) => Path.GetFullPath(Path.IsPathRooted(path)
		? path : Path.Combine(_options.ProjectPath, path));

	private bool _captureCompleted;

	private string GetCapturePath() => Path.GetFullPath(Path.IsPathRooted(_options.CapturePath)
		? _options.CapturePath : Path.Combine(_options.ProjectPath, _options.CapturePath));
	private string GetProfilePath() => Path.GetFullPath(Path.IsPathRooted(_options.ProfileOutputPath!)
		? _options.ProfileOutputPath! : Path.Combine(_options.ProjectPath, _options.ProfileOutputPath!));

	private void WriteProfile(IReadOnlyList<GpuProfileFrame> frames)
	{
		var passes = frames.SelectMany(frame => frame.Passes)
			.GroupBy(pass => pass.Name, StringComparer.Ordinal)
			.OrderBy(group => group.Key, StringComparer.Ordinal)
			.Select(group => new GpuPassProfileResult(
				group.Key,
				Summarize(group.Select(pass => pass.DurationMs)),
				group.SelectMany(pass => pass.Scopes)
					.GroupBy(scope => scope.Name, StringComparer.Ordinal)
					.OrderBy(scope => scope.Key, StringComparer.Ordinal)
					.Select(scope => new GpuScopeProfileResult(scope.Key, Summarize(scope.Select(value => value.DurationMs))))
					.ToArray()))
			.ToArray();
		var result = new GpuFrameProfileResult(
			_options.ProfileFrames,
			frames.Select(frame => frame.FrameIndex).ToArray(),
			passes,
			0,
			0);
		var path = GetProfilePath();
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
		Console.WriteLine($"gpu profile success frames={frames.Count} path={path}");
	}

	private static GpuTimingStatistics Summarize(IEnumerable<double> samples)
	{
		var sorted = samples.OrderBy(value => value).ToArray();
		if (sorted.Length == 0) return new GpuTimingStatistics(0, 0.0, 0.0, 0.0);
		var middle = sorted.Length / 2;
		var median = (sorted.Length & 1) == 0 ? (sorted[middle - 1] + sorted[middle]) * 0.5 : sorted[middle];
		return new GpuTimingStatistics(sorted.Length, median, sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1], sorted[^1]);
	}
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

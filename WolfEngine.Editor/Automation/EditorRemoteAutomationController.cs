using System.Collections.Concurrent;
using System.Numerics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;
using WolfEngine.Editor.UI;
using WolfEngine.Profiling;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.Automation;

/// <summary>Direct in-process automation API. Mutations are dispatched onto the editor thread.</summary>
public sealed class EditorRemoteAutomationController
{
	private readonly string _projectPath;
	private readonly IEditorProjectService _projectService;
	private readonly IGameplayAssemblyHost _gameplayAssemblyHost;
	private readonly IEditorSceneWorkspace _sceneWorkspace;
	private readonly IEditorSceneSnapshotService _sceneSnapshotService;
	private readonly IEditorInteractionState _interactionState;
	private readonly IEditorCommandService _commandService;
	private readonly IRenderer _renderer;
	private readonly EditorFrameCoordinator _editorFrameCoordinator;
	private readonly RenderFrameCoordinator _renderFrameCoordinator;
	private readonly RenderGraph _renderGraph;
	private readonly GpuProfiler _gpuProfiler;
	private readonly ConcurrentQueue<Action> _pendingCommands = new();
	private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private bool _initialized;

	public EditorRemoteAutomationController(
		string projectPath,
		IEditorProjectService projectService,
		IGameplayAssemblyHost gameplayAssemblyHost,
		IEditorSceneWorkspace sceneWorkspace,
		IEditorSceneSnapshotService sceneSnapshotService,
		IEditorInteractionState interactionState,
		IEditorCommandService commandService,
		IRenderer renderer,
		EditorFrameCoordinator editorFrameCoordinator,
		RenderFrameCoordinator renderFrameCoordinator,
		RenderGraph renderGraph,
		GpuProfiler gpuProfiler)
	{
		_projectPath = projectPath;
		_projectService = projectService;
		_gameplayAssemblyHost = gameplayAssemblyHost;
		_sceneWorkspace = sceneWorkspace;
		_sceneSnapshotService = sceneSnapshotService;
		_interactionState = interactionState;
		_commandService = commandService;
		_renderer = renderer;
		_editorFrameCoordinator = editorFrameCoordinator;
		_renderFrameCoordinator = renderFrameCoordinator;
		_renderGraph = renderGraph;
		_gpuProfiler = gpuProfiler;
	}

	public Task Ready => _ready.Task;
	public Task Stopped => _stopped.Task;
	public bool ShutdownRequested { get; private set; }
	public bool ShouldStop => ShutdownRequested;

	public void Initialize()
	{
		if (_initialized) return;
		_initialized = true;
		try
		{
			if (_projectService.OpenProject(_projectPath, out var error) == false) throw new InvalidOperationException(error);
			_gameplayAssemblyHost.EnsureLoaded();
			_ready.TrySetResult();
		}
		catch (Exception exception)
		{
			_ready.TrySetException(exception);
			ShutdownRequested = true;
			_renderer.RequestShutdown();
		}
	}

	public void ProcessPendingCommands()
	{
		while (_pendingCommands.TryDequeue(out var command)) command();
		if (ShutdownRequested) _renderer.RequestShutdown();
	}

	public Task<(Guid EntityId, string Name)> CreateEntityAsync(string? name, CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			var resolvedName = string.IsNullOrWhiteSpace(name) ? "Entity" : name.Trim();
			var scene = _sceneWorkspace.CurrentScene;
			var entity = scene.World.CreateEntity(resolvedName, Matrix4x4.Identity);
			var entityId = _sceneSnapshotService.EnsurePersistentEntityId(scene, entity);
			scene.EntityCellKeys[entity] = SceneCellKey.Global;
			_interactionState.MarkSceneDirty();
			return (entityId, resolvedName);
		}, cancellationToken);

	public Task DeleteEntityAsync(Guid entityId, CancellationToken cancellationToken) => Enqueue(() =>
	{
		var scene = _sceneWorkspace.CurrentScene;
		if (scene.EntityIds.Values.Contains(entityId) == false) throw new InvalidOperationException($"Entity '{entityId:D}' was not found.");
		_sceneSnapshotService.DeleteEntitiesByPersistentIds(scene, [entityId]);
		_interactionState.MarkSceneDirty();
	}, cancellationToken);

	public Task<SceneLoadResult> LoadSceneAsync(string scenePath, CancellationToken cancellationToken) =>
		EnqueueAsync(async () =>
		{
			var scene = ResolveSceneAsset(scenePath);
			await _commandService.LoadSceneForAutomationAsync(scene.Id, cancellationToken).ConfigureAwait(false);
			return new SceneLoadResult(
				scene.RelativeAssetPath,
				scene.Id,
				_editorFrameCoordinator.CompletedSequence,
				_renderFrameCoordinator.CompletedSequence);
		}, cancellationToken);

	public Task<RenderFrameWaitResult> WaitForRenderFramesAsync(int frameCount, CancellationToken cancellationToken) =>
		EnqueueAsync(async () =>
		{
			var renderSequence = await _renderFrameCoordinator
				.WaitForCompletedFramesAsync(frameCount, cancellationToken)
				.ConfigureAwait(false);
			return new RenderFrameWaitResult(frameCount, _editorFrameCoordinator.CompletedSequence, renderSequence);
		}, cancellationToken);

	public Task<RayTracingSceneStateResult> GetRayTracingSceneStateAsync(CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			var state = _renderGraph.GetRayTracingSceneState();
			return new RayTracingSceneStateResult(
				state.TopLevelAccelerationStructureIdentity ?? "None",
				state.TopLevelAccelerationStructureGeneration,
				state.TopLevelInstanceCount,
				state.MeshBottomLevelAccelerationStructureCount,
				state.TerrainBottomLevelAccelerationStructureCount,
				state.PendingBottomLevelBuildCount,
				state.LastTopLevelUpdateReason.ToString(),
				state.TerrainInstanceCount,
				state.PendingResourceRetirementCount,
				state.LastSubmittedId,
				state.CompletedId,
				_renderFrameCoordinator.CompletedSequence);
		}, cancellationToken);

	public Task<GpuFrameProfileResult> ProfileGpuFramesAsync(int frameCount, CancellationToken cancellationToken) =>
		EnqueueAsync(async () =>
		{
			var wasEnabled = _gpuProfiler.Enabled;
			var marker = _gpuProfiler.BeginCollection();
			try
			{
				var frames = await _gpuProfiler
					.CollectCompletedFramesAsync(marker, frameCount, cancellationToken)
					.ConfigureAwait(false);
				return new GpuFrameProfileResult(
					frameCount,
					frames.Select(frame => frame.FrameIndex).ToArray(),
					SummarizeGpuFrames(frames),
					_editorFrameCoordinator.CompletedSequence,
					_renderFrameCoordinator.CompletedSequence);
			}
			finally
			{
				_gpuProfiler.Enabled = wasEnabled;
			}
		}, cancellationToken);

	public Task<FrameCaptureResult> CaptureFrameAsync(string outputPath, CancellationToken cancellationToken) =>
		EnqueueAsync(async () =>
		{
			var fullOutputPath = ResolveOutputPath(outputPath);
			var capture = await _renderer.CaptureNextFrameAsync(cancellationToken).ConfigureAwait(false);
			Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
			using var image = Image.LoadPixelData<Rgba32>(capture.Rgba8, capture.Width, capture.Height);
			image.SaveAsPng(fullOutputPath);
			return new FrameCaptureResult(
				fullOutputPath,
				capture.Width,
				capture.Height,
				_editorFrameCoordinator.CompletedSequence,
				_renderFrameCoordinator.CompletedSequence);
		}, cancellationToken);

	public Task ShutdownAsync(CancellationToken cancellationToken) => Enqueue(() => { ShutdownRequested = true; }, cancellationToken);

	private Task<T> Enqueue<T>(Func<T> operation, CancellationToken cancellationToken)
	{
		var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
		_pendingCommands.Enqueue(() =>
		{
			try { completion.TrySetResult(operation()); }
			catch (Exception exception) { completion.TrySetException(exception); }
		});
		return completion.Task.WaitAsync(cancellationToken);
	}

	private Task<T> EnqueueAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
	{
		var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
		_pendingCommands.Enqueue(() =>
		{
			try
			{
				_ = CompleteAsync(operation(), completion);
			}
			catch (Exception exception)
			{
				completion.TrySetException(exception);
			}
		});
		return completion.Task.WaitAsync(cancellationToken);
	}

	private Task Enqueue(Action operation, CancellationToken cancellationToken)
	{
		var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_pendingCommands.Enqueue(() =>
		{
			try { operation(); completion.TrySetResult(); }
			catch (Exception exception) { completion.TrySetException(exception); }
		});
		return completion.Task.WaitAsync(cancellationToken);
	}

	private AssetDatabaseEntry ResolveSceneAsset(string scenePath)
	{
		if (string.IsNullOrWhiteSpace(scenePath))
		{
			throw new InvalidOperationException("scene_path is required.");
		}

		var fullProjectPath = Path.GetFullPath(_projectPath);
		var fullScenePath = Path.GetFullPath(Path.IsPathRooted(scenePath)
			? scenePath
			: Path.Combine(fullProjectPath, scenePath));
		var relativeScenePath = Normalize(Path.GetRelativePath(fullProjectPath, fullScenePath));
		if (relativeScenePath.Equals("..", StringComparison.Ordinal) ||
			relativeScenePath.StartsWith("../", StringComparison.Ordinal))
		{
			throw new InvalidOperationException("scene_path must be inside the open project.");
		}

		var scene = _projectService.CurrentAssetDatabase.Assets.SingleOrDefault(asset =>
			asset.Type == AssetType.Scene &&
			string.Equals(Normalize(asset.RelativeAssetPath), relativeScenePath, StringComparison.OrdinalIgnoreCase));
		return scene ?? throw new InvalidOperationException(
			$"Scene '{scenePath}' was not found in the open project's asset database.");
	}

	private string ResolveOutputPath(string outputPath)
	{
		if (string.IsNullOrWhiteSpace(outputPath))
		{
			throw new InvalidOperationException("output_path is required.");
		}

		return Path.GetFullPath(Path.IsPathRooted(outputPath)
			? outputPath
			: Path.Combine(_projectPath, outputPath));
	}

	private static IReadOnlyList<GpuPassProfileResult> SummarizeGpuFrames(IReadOnlyList<GpuProfileFrame> frames)
	{
		return frames
			.SelectMany(frame => frame.Passes)
			.GroupBy(pass => pass.Name, StringComparer.Ordinal)
			.OrderBy(group => group.Key, StringComparer.Ordinal)
			.Select(passGroup => new GpuPassProfileResult(
				passGroup.Key,
				SummarizeTimings(passGroup.Select(pass => pass.DurationMs)),
				passGroup
					.SelectMany(pass => pass.Scopes)
					.GroupBy(scope => scope.Name, StringComparer.Ordinal)
					.OrderBy(group => group.Key, StringComparer.Ordinal)
					.Select(scopeGroup => new GpuScopeProfileResult(
						scopeGroup.Key,
						SummarizeTimings(scopeGroup.Select(scope => scope.DurationMs))))
					.ToArray()))
			.ToArray();
	}

	private static GpuTimingStatistics SummarizeTimings(IEnumerable<double> samples)
	{
		var sorted = samples.OrderBy(sample => sample).ToArray();
		if (sorted.Length == 0)
		{
			return new GpuTimingStatistics(0, 0.0, 0.0, 0.0);
		}

		var middle = sorted.Length / 2;
		var median = (sorted.Length & 1) == 0
			? (sorted[middle - 1] + sorted[middle]) * 0.5
			: sorted[middle];
		var p95 = sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1];
		return new GpuTimingStatistics(sorted.Length, median, p95, sorted[^1]);
	}

	private static async Task CompleteAsync<T>(Task<T> operation, TaskCompletionSource<T> completion)
	{
		try
		{
			completion.TrySetResult(await operation.ConfigureAwait(false));
		}
		catch (OperationCanceledException exception)
		{
			completion.TrySetCanceled(exception.CancellationToken);
		}
		catch (Exception exception)
		{
			completion.TrySetException(exception);
		}
	}

	private static string Normalize(string path) => path.Replace('\\', '/');

	public void NotifyStopped() => _stopped.TrySetResult();
}

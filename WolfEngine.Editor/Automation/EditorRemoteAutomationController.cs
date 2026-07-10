using System.Collections.Concurrent;
using System.Numerics;
using WolfEngine.Editor.Projects;
using WolfEngine.Editor.UI;
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
	private readonly IRenderer _renderer;
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
		IRenderer renderer)
	{
		_projectPath = projectPath;
		_projectService = projectService;
		_gameplayAssemblyHost = gameplayAssemblyHost;
		_sceneWorkspace = sceneWorkspace;
		_sceneSnapshotService = sceneSnapshotService;
		_interactionState = interactionState;
		_renderer = renderer;
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

	public void NotifyStopped() => _stopped.TrySetResult();
}

using WolfEngine.Editor;
using WolfEngine.Editor.Automation;

namespace WolfEngine.Editor.Automation;

public sealed record CreatedEntity(Guid EntityId, string Name);

/// <summary>
/// Coordinates an in-process editor. Its run loop stays on the Automation process's main thread,
/// while MCP requests are served by the hosted server on background threads.
/// </summary>
public sealed class EditorProcessController : IAsyncDisposable
{
	private sealed record StartRequest(string ProjectPath, TaskCompletionSource Ready);

	private readonly object _sync = new();
	private readonly SemaphoreSlim _startSignal = new(0);
	private StartRequest? _pendingStart;
	private EditorApplication? _application;
	private EditorRemoteAutomationController? _editor;

	public async Task StartAsync(string projectPath, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(projectPath)) throw new InvalidOperationException("project_path is required.");
		var fullProjectPath = Path.GetFullPath(projectPath);
		if (Directory.Exists(fullProjectPath) == false) throw new InvalidOperationException($"Project directory '{fullProjectPath}' does not exist.");

		var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		lock (_sync)
		{
			if (_application is not null || _pendingStart is not null) throw new InvalidOperationException("WolfEngine Editor is already running.");
			_pendingStart = new StartRequest(fullProjectPath, ready);
			_startSignal.Release();
		}

		await ready.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Called by Automation.Program on the process main thread.</summary>
	public void RunOnCurrentThread(CancellationToken cancellationToken)
	{
		while (cancellationToken.IsCancellationRequested == false)
		{
			try { _startSignal.Wait(cancellationToken); }
			catch (OperationCanceledException) { break; }
			if (cancellationToken.IsCancellationRequested) break;

			StartRequest? request;
			lock (_sync)
			{
				request = _pendingStart;
				_pendingStart = null;
			}
			if (request is null) continue;

			EditorApplication? application = null;
			EditorRemoteAutomationController? editor = null;
			try
			{
				application = EditorApplication.Create();
				editor = application.CreateAutomationController(request.ProjectPath);
				lock (_sync)
				{
					_application = application;
					_editor = editor;
				}
				_ = editor.Ready.ContinueWith(task =>
				{
					if (task.IsFaulted) request.Ready.TrySetException(task.Exception!.InnerException ?? task.Exception);
					else if (task.IsCanceled) request.Ready.TrySetCanceled();
					else request.Ready.TrySetResult();
				}, TaskScheduler.Default);
				application.Run(automationController: editor);
				if (editor.Ready.IsCompleted == false) request.Ready.TrySetException(new InvalidOperationException("Editor stopped before it became ready."));
			}
			catch (Exception exception)
			{
				request.Ready.TrySetException(exception);
			}
			finally
			{
				editor?.NotifyStopped();
				application?.Dispose();
				lock (_sync)
				{
					_application = null;
					_editor = null;
				}
			}
		}
	}

	public async Task<CreatedEntity> CreateEntityAsync(string? name, CancellationToken cancellationToken)
	{
		var entity = await GetRunningEditor().CreateEntityAsync(name, cancellationToken).ConfigureAwait(false);
		return new CreatedEntity(entity.EntityId, entity.Name);
	}

	public async Task DeleteEntityAsync(string entityId, CancellationToken cancellationToken)
	{
		if (Guid.TryParse(entityId, out var parsedId) == false) throw new InvalidOperationException("entity_id must be a GUID.");
		await GetRunningEditor().DeleteEntityAsync(parsedId, cancellationToken).ConfigureAwait(false);
	}

	public async Task ShutdownAsync(CancellationToken cancellationToken)
	{
		var editor = GetRunningEditor();
		await editor.ShutdownAsync(cancellationToken).ConfigureAwait(false);
		await editor.Stopped.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
	}

	private EditorRemoteAutomationController GetRunningEditor()
	{
		lock (_sync)
		{
			return _editor ?? throw new InvalidOperationException("WolfEngine Editor is not running.");
		}
	}

	public ValueTask DisposeAsync()
	{
		_startSignal.Dispose();
		return ValueTask.CompletedTask;
	}
}

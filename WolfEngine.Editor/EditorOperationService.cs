using System.Diagnostics;

namespace WolfEngine.Editor;

/// <summary>
/// Runs an exclusive editor operation away from the editor frame thread while keeping
/// the renderer free to present a responsive loading screen.
/// </summary>
public interface IEditorOperationService
{
	EditorOperationSnapshot Current { get; }
	bool TryStart(string title, Action<IProgress<string>> operation, Action? completed = null, Action<Exception>? failed = null);
	void Update();
}

public readonly record struct EditorOperationSnapshot(bool IsActive, string Title, string Detail, TimeSpan Elapsed);

public sealed class EditorOperationService : IEditorOperationService
{
	private readonly object _sync = new();
	private EditorOperationSnapshot _current = new(false, string.Empty, string.Empty, TimeSpan.Zero);
	private Task? _task;
	private Action? _completed;
	private Action<Exception>? _failed;
	private Stopwatch? _stopwatch;

	public EditorOperationSnapshot Current
	{
		get { lock (_sync) return _current; }
	}

	public bool TryStart(string title, Action<IProgress<string>> operation, Action? completed = null, Action<Exception>? failed = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(title);
		ArgumentNullException.ThrowIfNull(operation);
		lock (_sync)
		{
			if (_task is not null) return false;
			_stopwatch = Stopwatch.StartNew();
			_current = new EditorOperationSnapshot(true, title, "Starting…", TimeSpan.Zero);
			_completed = completed;
			_failed = failed;
			var progress = new Progress<string>(detail =>
			{
				lock (_sync)
				{
					if (_current.IsActive) _current = _current with { Detail = detail };
				}
			});
			_task = Task.Run(() => operation(progress));
			return true;
		}
	}

	/// <summary>Called by the editor thread once per frame to finish operations safely.</summary>
	public void Update()
	{
		Task? task;
		lock (_sync)
		{
			task = _task;
			if (_current.IsActive && _stopwatch is not null)
				_current = _current with { Elapsed = _stopwatch.Elapsed };
		}
		if (task is null || task.IsCompleted == false) return;

		Action? completed;
		Action<Exception>? failed;
		Exception? error = null;
		try { task.GetAwaiter().GetResult(); }
		catch (Exception exception) { error = exception; }
		lock (_sync)
		{
			completed = _completed;
			failed = _failed;
			_task = null;
			_completed = null;
			_failed = null;
			_stopwatch?.Stop();
			_stopwatch = null;
			_current = new EditorOperationSnapshot(false, string.Empty, string.Empty, TimeSpan.Zero);
		}
		if (error is null) completed?.Invoke();
		else failed?.Invoke(error);
	}
}

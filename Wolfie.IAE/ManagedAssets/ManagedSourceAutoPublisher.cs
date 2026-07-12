using System.Collections.Concurrent;
using Wolfie.IAE.ExternalTools;
using Wolfie.IAE.Projects;

namespace Wolfie.IAE.ManagedAssets;

public sealed record AutoPublishNotification(string RelativeSourcePath, bool Succeeded, string? Error);

public sealed class ManagedSourceAutoPublisher(IBlenderModelPublisher publisher, WolfiePreferences preferences) : IDisposable
{
	private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(750);
	private readonly ConcurrentDictionary<string, DateTime> _pending = new(StringComparer.OrdinalIgnoreCase);
	private readonly ConcurrentQueue<AutoPublishNotification> _notifications = new();
	private readonly object _lifecycleLock = new();
	private FileSystemWatcher? _watcher;
	private CancellationTokenSource? _cancellation;
	private Task? _worker;
	private WolfieProject? _project;
	private string? _projectFile;

	public void Start(WolfieProject project, string projectFile)
	{
		lock (_lifecycleLock)
		{
			StopCore();
			_project = project;
			_projectFile = WolfiePath.NormalizeAbsolute(projectFile);
			var root = Path.Combine(Path.GetDirectoryName(_projectFile)!, "Assets");
			_cancellation = new CancellationTokenSource();
			_watcher = new FileSystemWatcher(root, "*.blend")
			{
				IncludeSubdirectories = true,
				NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
				EnableRaisingEvents = true
			};
			_watcher.Created += OnSourceChanged;
			_watcher.Changed += OnSourceChanged;
			_watcher.Renamed += OnSourceRenamed;
			_worker = RunAsync(_cancellation.Token);
		}
	}

	public void Stop()
	{
		lock (_lifecycleLock) StopCore();
	}

	public bool TryDequeue(out AutoPublishNotification notification) => _notifications.TryDequeue(out notification!);

	public void QueueSourceChange(string absoluteSourcePath)
	{
		var projectFile = _projectFile;
		if (projectFile is null || !string.Equals(Path.GetExtension(absoluteSourcePath), ".blend", StringComparison.OrdinalIgnoreCase)) return;
		var root = Path.Combine(Path.GetDirectoryName(projectFile)!, "Assets");
		var absolute = WolfiePath.NormalizeAbsolute(absoluteSourcePath);
		if (!WolfiePath.IsWithin(absolute, root)) return;
		var relative = "Assets/" + Path.GetRelativePath(root, absolute).Replace(Path.DirectorySeparatorChar, '/');
		_pending[relative] = DateTime.UtcNow + Debounce;
	}

	private void OnSourceChanged(object sender, FileSystemEventArgs args) => QueueSourceChange(args.FullPath);
	private void OnSourceRenamed(object sender, RenamedEventArgs args) => QueueSourceChange(args.FullPath);

	private async Task RunAsync(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			try { await Task.Delay(200, cancellationToken); }
			catch (OperationCanceledException) { break; }
			var now = DateTime.UtcNow;
			foreach (var pending in _pending.ToArray())
			{
				if (pending.Value > now || !_pending.TryRemove(pending.Key, out _)) continue;
				await PublishAsync(pending.Key, cancellationToken);
			}
		}
	}

	private async Task PublishAsync(string relativePath, CancellationToken cancellationToken)
	{
		var project = _project;
		var projectFile = _projectFile;
		if (project is null || projectFile is null) return;
		try
		{
			// The source move may be observed just before its metadata move. The debounce normally
			// covers that window; this check makes an incomplete creation a harmless no-op.
			var source = Path.Combine(Path.GetDirectoryName(projectFile)!, relativePath.Replace('/', Path.DirectorySeparatorChar));
			if (!File.Exists(source) || !File.Exists(source + ".meta")) return;
			await publisher.PublishAsync(project, projectFile, relativePath, preferences.BlenderPath, cancellationToken);
			_notifications.Enqueue(new AutoPublishNotification(relativePath, true, null));
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
		catch (Exception exception)
		{
			_notifications.Enqueue(new AutoPublishNotification(relativePath, false, exception.Message));
		}
	}

	private void StopCore()
	{
		if (_watcher is not null)
		{
			_watcher.EnableRaisingEvents = false;
			_watcher.Created -= OnSourceChanged;
			_watcher.Changed -= OnSourceChanged;
			_watcher.Renamed -= OnSourceRenamed;
			_watcher.Dispose();
		}
		_cancellation?.Cancel();
		_watcher = null;
		_cancellation = null;
		_worker = null;
		_project = null;
		_projectFile = null;
		_pending.Clear();
	}

	public void Dispose()
	{
		Stop();
		GC.SuppressFinalize(this);
	}
}

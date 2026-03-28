using System.Collections.Concurrent;

namespace WolfEngine.Editor.UI;

public interface IEditorNotificationService
{
	void ReportError(string message);
	bool TryDequeueError(out string message);
}

public sealed class EditorNotificationService : IEditorNotificationService
{
	private readonly ConcurrentQueue<string> _errors = new();

	public void ReportError(string message)
	{
		if (string.IsNullOrWhiteSpace(message))
		{
			return;
		}

		_errors.Enqueue(message);
	}

	public bool TryDequeueError(out string message)
	{
		return _errors.TryDequeue(out message!);
	}
}

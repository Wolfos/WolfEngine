using System.Collections.Concurrent;

namespace WolfEngine.Editor.UI;

public enum EditorNotificationKind
{
	Error,
	Info
}

public readonly record struct EditorNotification(EditorNotificationKind Kind, string Message);

public interface IEditorNotificationService
{
	void ReportError(string message);
	void ReportInfo(string message);
	bool TryDequeue(out EditorNotification notification);
}

public sealed class EditorNotificationService : IEditorNotificationService
{
	private readonly ConcurrentQueue<EditorNotification> _notifications = new();

	public void ReportError(string message)
	{
		if (string.IsNullOrWhiteSpace(message))
		{
			return;
		}

		_notifications.Enqueue(new EditorNotification(EditorNotificationKind.Error, message));
	}

	public void ReportInfo(string message)
	{
		if (string.IsNullOrWhiteSpace(message))
		{
			return;
		}

		_notifications.Enqueue(new EditorNotification(EditorNotificationKind.Info, message));
	}

	public bool TryDequeue(out EditorNotification notification)
	{
		return _notifications.TryDequeue(out notification);
	}
}

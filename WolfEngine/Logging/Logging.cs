namespace WolfEngine.Logging;

public enum LogMessageKind
{
	Info,
	Warning,
	Error
}

public readonly record struct LogEntry(
	long Sequence,
	DateTimeOffset TimestampUtc,
	LogMessageKind Kind,
	string Message,
	string? Details);

public interface ILogSink
{
	void Write(in LogEntry entry);
}

public interface ILogService
{
	void Info(string message);
	void Warning(string message);
	void Error(string message);
	void Error(string message, Exception exception);
}

public sealed class LogService : ILogService
{
	private readonly object _sync = new();
	private readonly ILogSink[] _sinks;
	private long _nextSequence;

	public LogService(IEnumerable<ILogSink> sinks)
	{
		ArgumentNullException.ThrowIfNull(sinks);
		_sinks = sinks.ToArray();
	}

	public void Info(string message) => Write(LogMessageKind.Info, message, details: null);

	public void Warning(string message) => Write(LogMessageKind.Warning, message, details: null);

	public void Error(string message) => Write(LogMessageKind.Error, message, details: null);

	public void Error(string message, Exception exception)
	{
		ArgumentNullException.ThrowIfNull(exception);
		Write(LogMessageKind.Error, message, exception.ToString());
	}

	private void Write(LogMessageKind kind, string message, string? details)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(message);

		lock (_sync)
		{
			var entry = new LogEntry(
				++_nextSequence,
				DateTimeOffset.UtcNow,
				kind,
				message,
				details);

			for (var index = 0; index < _sinks.Length; index++)
			{
				try
				{
					_sinks[index].Write(entry);
				}
				catch (Exception exception)
				{
					try
					{
						Console.Error.WriteLine($"Log sink '{_sinks[index].GetType().FullName}' failed: {exception}");
					}
					catch
					{
						// Logging must never take down the caller, including when the fallback stream is unavailable.
					}
				}
			}
		}
	}
}

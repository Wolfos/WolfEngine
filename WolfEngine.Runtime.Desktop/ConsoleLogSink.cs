using WolfEngine.Logging;

namespace WolfEngine.Runtime.Desktop;

/// <summary>
/// Writes engine log entries to the console, which <see cref="RuntimeLog"/> copies to the log file. Without a sink,
/// what the engine reports through <see cref="ILogService"/> would be dropped in a built game.
/// </summary>
public sealed class ConsoleLogSink : ILogSink
{
	public void Write(in LogEntry entry)
	{
		var text = entry.Details is null ? entry.Message : $"{entry.Message}{Environment.NewLine}{entry.Details}";
		switch (entry.Kind)
		{
			case LogMessageKind.Error:
				Console.Error.WriteLine(text);
				break;
			case LogMessageKind.Warning:
				Console.WriteLine($"warning: {text}");
				break;
			default:
				Console.WriteLine(text);
				break;
		}
	}
}

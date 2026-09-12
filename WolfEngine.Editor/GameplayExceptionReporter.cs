using WolfEngine.ECS;
using WolfEngine.Logging;

namespace WolfEngine.Editor;

internal sealed class GameplayExceptionReporter
{
	private readonly ILogService _log;

	public GameplayExceptionReporter(ILogService log)
	{
		_log = log ?? throw new ArgumentNullException(nameof(log));
	}

	public void Run(string callback, Action action)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(callback);
		ArgumentNullException.ThrowIfNull(action);

		try
		{
			action();
		}
		catch (Exception exception)
		{
			Report(callback, exception);
		}
	}

	public bool TryRun<T>(string callback, Func<T> action, out T result)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(callback);
		ArgumentNullException.ThrowIfNull(action);

		try
		{
			result = action();
			return true;
		}
		catch (Exception exception)
		{
			Report(callback, exception);
			result = default!;
			return false;
		}
	}

	public void ReportSystem(ISystem system, Exception exception)
	{
		ArgumentNullException.ThrowIfNull(system);
		Report(system.GetType().FullName ?? system.GetType().Name, exception);
	}

	private void Report(string callback, Exception exception)
	{
		_log.Error($"Gameplay exception in {callback}.", exception);
	}
}

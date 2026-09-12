using WolfEngine.ECS;

namespace WolfEngine.Editor;

internal static class GameplayExceptionReporter
{
	public static void Run(string callback, Action action)
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

	public static bool TryRun<T>(string callback, Func<T> action, out T result)
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

	public static void ReportSystem(ISystem system, Exception exception)
	{
		ArgumentNullException.ThrowIfNull(system);
		Report(system.GetType().FullName ?? system.GetType().Name, exception);
	}

	private static void Report(string callback, Exception exception)
	{
		Console.WriteLine($"Gameplay exception in {callback}:{Environment.NewLine}{exception}");
	}
}

using NSubstitute;
using WolfEngine.ECS;
using WolfEngine.Logging;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class GameplayExceptionReporterTests
{
	[Test]
	public void Run_ReportsExceptionWithoutPropagatingIt()
	{
		var log = Substitute.For<ILogService>();
		var reporter = new GameplayExceptionReporter(log);

		Assert.DoesNotThrow(() => reporter.Run("Update", ThrowFromGameplay));
		log.Received(1).Error(
			"Gameplay exception in Update.",
			Arg.Is<InvalidOperationException>(exception => exception.Message == "Expected gameplay failure."));
	}

	[Test]
	public void ReportSystem_ReportsExceptionToTheLogger()
	{
		var log = Substitute.For<ILogService>();
		var reporter = new GameplayExceptionReporter(log);
		var system = Substitute.For<ISystem>();
		var exception = new InvalidOperationException("Expected system failure.");

		reporter.ReportSystem(system, exception);

		log.Received(1).Error(
			$"Gameplay exception in {system.GetType().FullName}.",
			exception);
	}

	private static void ThrowFromGameplay() => throw new InvalidOperationException("Expected gameplay failure.");
}

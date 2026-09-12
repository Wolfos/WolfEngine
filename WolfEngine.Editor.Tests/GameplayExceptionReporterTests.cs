namespace WolfEngine.Editor.Tests;

[TestFixture]
[NonParallelizable]
public sealed class GameplayExceptionReporterTests
{
	[Test]
	public void Run_ReportsExceptionWithoutPropagatingIt()
	{
		var originalOut = Console.Out;
		using var output = new StringWriter();
		Console.SetOut(output);
		try
		{
			Assert.DoesNotThrow(() => GameplayExceptionReporter.Run("Update", ThrowFromGameplay));
		}
		finally
		{
			Console.SetOut(originalOut);
		}

		Assert.That(output.ToString(), Does.Contain("Gameplay exception in Update:"));
		Assert.That(output.ToString(), Does.Contain(nameof(ThrowFromGameplay)));
	}

	private static void ThrowFromGameplay() => throw new InvalidOperationException("Expected gameplay failure.");
}

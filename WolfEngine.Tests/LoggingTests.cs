using WolfEngine.Logging;

namespace WolfEngine.Tests;

[TestFixture]
[NonParallelizable]
public sealed class LoggingTests
{
	[Test]
	public void SeverityMethods_PublishStructuredEntriesInOrder()
	{
		var sink = new RecordingSink();
		var log = new LogService([sink]);

		log.Info("Information");
		log.Warning("Warning");
		log.Error("Error");

		Assert.That(sink.Entries.Select(entry => entry.Sequence), Is.EqualTo(new long[] { 1, 2, 3 }));
		Assert.That(sink.Entries.Select(entry => entry.Kind), Is.EqualTo(new[]
		{
			LogMessageKind.Info,
			LogMessageKind.Warning,
			LogMessageKind.Error
		}));
		Assert.That(sink.Entries.Select(entry => entry.Message), Is.EqualTo(new[]
		{
			"Information",
			"Warning",
			"Error"
		}));
		Assert.That(sink.Entries, Has.All.Matches<LogEntry>(entry => entry.TimestampUtc.Offset == TimeSpan.Zero));
	}

	[Test]
	public void ErrorWithException_SnapshotsExceptionText()
	{
		var sink = new RecordingSink();
		var log = new LogService([sink]);
		var exception = CaptureException();

		log.Error("Gameplay failed.", exception);

		var entry = sink.Entries.Single();
		Assert.That(entry.Message, Is.EqualTo("Gameplay failed."));
		Assert.That(entry.Details, Does.Contain(typeof(InvalidOperationException).FullName));
		Assert.That(entry.Details, Does.Contain("Expected failure."));
		Assert.That(entry.Details, Does.Contain(nameof(ThrowExpectedFailure)));
		Assert.That(typeof(LogEntry).GetProperties().Any(property => typeof(Exception).IsAssignableFrom(property.PropertyType)), Is.False);
	}

	[Test]
	public void FailingSink_DoesNotBlockOtherSinksOrEscapeToCaller()
	{
		var first = new RecordingSink();
		var second = new RecordingSink();
		var log = new LogService([first, new ThrowingSink(), second]);
		var originalError = Console.Error;
		using var fallbackOutput = new StringWriter();
		Console.SetError(fallbackOutput);
		try
		{
			Assert.DoesNotThrow(() => log.Warning("Still delivered."));
		}
		finally
		{
			Console.SetError(originalError);
		}

		Assert.That(first.Entries, Has.Count.EqualTo(1));
		Assert.That(second.Entries, Has.Count.EqualTo(1));
		Assert.That(fallbackOutput.ToString(), Does.Contain(nameof(ThrowingSink)));
	}

	[Test]
	public void ConcurrentWriters_ReceiveOneStrictlyOrderedSequence()
	{
		const int entryCount = 1_000;
		var sink = new RecordingSink();
		var log = new LogService([sink]);

		Parallel.For(0, entryCount, index => log.Info($"Message {index}"));

		Assert.That(sink.Entries, Has.Count.EqualTo(entryCount));
		Assert.That(sink.Entries.Select(entry => entry.Sequence), Is.EqualTo(Enumerable.Range(1, entryCount).Select(value => (long)value)));
	}

	private static Exception CaptureException()
	{
		try
		{
			ThrowExpectedFailure();
		}
		catch (Exception exception)
		{
			return exception;
		}

		throw new AssertionException("Expected an exception.");
	}

	private static void ThrowExpectedFailure() => throw new InvalidOperationException("Expected failure.");

	private sealed class RecordingSink : ILogSink
	{
		public List<LogEntry> Entries { get; } = new();

		public void Write(in LogEntry entry) => Entries.Add(entry);
	}

	private sealed class ThrowingSink : ILogSink
	{
		public void Write(in LogEntry entry) => throw new InvalidOperationException("Sink failed.");
	}
}

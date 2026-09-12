using WolfEngine.Editor.UI;
using WolfEngine.Logging;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class EditorLogTests
{
	[Test]
	public void Buffer_EvictsOldestEntriesAtCapacity()
	{
		var buffer = new EditorLogBuffer(3);
		for (var sequence = 1; sequence <= 5; sequence++)
		{
			var entry = Entry(sequence, LogMessageKind.Info, $"Message {sequence}");
			buffer.Write(entry);
		}

		Assert.That(buffer.GetSnapshot().Select(entry => entry.Sequence), Is.EqualTo(new long[] { 3, 4, 5 }));
	}

	[Test]
	public void Buffer_ClearRemovesEntriesAndAdvancesVersion()
	{
		var buffer = new EditorLogBuffer(3);
		var entry = Entry(1, LogMessageKind.Info, "Message");
		buffer.Write(entry);
		var versionBeforeClear = buffer.Version;

		buffer.Clear();

		Assert.That(buffer.GetSnapshot(), Is.Empty);
		Assert.That(buffer.Version, Is.GreaterThan(versionBeforeClear));
	}

	[Test]
	public void Buffer_AcceptsConcurrentWriters()
	{
		const int entryCount = 5_000;
		var buffer = new EditorLogBuffer(entryCount);

		Parallel.For(0, entryCount, index =>
		{
			var entry = Entry(index + 1, LogMessageKind.Info, $"Message {index}");
			buffer.Write(entry);
		});

		Assert.That(buffer.GetSnapshot(), Has.Count.EqualTo(entryCount));
	}

	[Test]
	public void Model_FiltersKindsAndCountsAllEntries()
	{
		LogEntry[] entries =
		[
			Entry(1, LogMessageKind.Info, "Info"),
			Entry(2, LogMessageKind.Warning, "Warning"),
			Entry(3, LogMessageKind.Error, "Error")
		];
		var filtered = new List<LogEntry>();

		LogWindowModel.Filter(entries, showInfo: false, showWarnings: true, showErrors: true, filtered);

		Assert.That(filtered.Select(entry => entry.Sequence), Is.EqualTo(new long[] { 2, 3 }));
		Assert.That(LogWindowModel.CountByKind(entries), Is.EqualTo((1, 1, 1)));
	}

	[Test]
	public void Model_UsesFirstLineAsSummaryAndInvalidatesMissingSelection()
	{
		var entry = Entry(4, LogMessageKind.Error, "First line\r\nSecond line");

		Assert.That(LogWindowModel.GetSummary(entry.Message), Is.EqualTo("First line"));
		Assert.That(LogWindowModel.ResolveSelection([entry], 4), Is.EqualTo(entry));
		Assert.That(LogWindowModel.ResolveSelection(Array.Empty<LogEntry>(), 4), Is.Null);
	}

	[TestCase(true, true, true, ExpectedResult = true)]
	[TestCase(false, true, true, ExpectedResult = false)]
	[TestCase(true, false, true, ExpectedResult = false)]
	[TestCase(true, true, false, ExpectedResult = false)]
	public bool Model_AutoScrollRequiresEnabledBottomAndNewEntries(bool enabled, bool atBottom, bool hasNewEntries) =>
		LogWindowModel.ShouldAutoScroll(enabled, atBottom, hasNewEntries);

	private static LogEntry Entry(long sequence, LogMessageKind kind, string message) =>
		new(sequence, DateTimeOffset.UtcNow, kind, message, null);
}

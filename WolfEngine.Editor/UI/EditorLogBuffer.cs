using WolfEngine.Logging;

namespace WolfEngine.Editor.UI;

internal interface IEditorLogStore
{
	long Version { get; }
	IReadOnlyList<LogEntry> GetSnapshot();
	void Clear();
}

internal sealed class EditorLogBuffer : ILogSink, IEditorLogStore
{
	public const int DefaultCapacity = 10_000;

	private readonly object _sync = new();
	private readonly Queue<LogEntry> _entries;
	private readonly int _capacity;
	private long _version;

	public EditorLogBuffer() : this(DefaultCapacity)
	{
	}

	internal EditorLogBuffer(int capacity)
	{
		if (capacity <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(capacity), "Log capacity must be positive.");
		}

		_capacity = capacity;
		_entries = new Queue<LogEntry>(capacity);
	}

	public long Version
	{
		get
		{
			lock (_sync)
			{
				return _version;
			}
		}
	}

	public void Write(in LogEntry entry)
	{
		lock (_sync)
		{
			if (_entries.Count == _capacity)
			{
				_entries.Dequeue();
			}

			_entries.Enqueue(entry);
			_version++;
		}
	}

	public IReadOnlyList<LogEntry> GetSnapshot()
	{
		lock (_sync)
		{
			return _entries.ToArray();
		}
	}

	public void Clear()
	{
		lock (_sync)
		{
			_entries.Clear();
			_version++;
		}
	}
}

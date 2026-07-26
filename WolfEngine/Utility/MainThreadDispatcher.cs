using System;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace WolfEngine.Utility;

public interface IMainThreadDispatcher
{
	bool IsMainThread { get; }
	void ExecutePending();
	void Invoke(Action action);
	T Invoke<T>(Func<T> action);
}

public sealed class MainThreadDispatcher : IMainThreadDispatcher
{
	private sealed class WorkItem
	{
		public Action Action { get; }
		public ManualResetEventSlim Done { get; }
		public Exception? Exception { get; set; }

		public WorkItem(Action action)
		{
			Action = action;
			Done = new ManualResetEventSlim(false);
		}
	}

	private readonly int _mainThreadId;
	private readonly ConcurrentQueue<WorkItem> _pending = new();

	public MainThreadDispatcher()
	{
		_mainThreadId = Environment.CurrentManagedThreadId;
	}

	public bool IsMainThread => Environment.CurrentManagedThreadId == _mainThreadId;

	public void ExecutePending()
	{
		while (_pending.TryDequeue(out var item))
		{
			try
			{
				item.Action();
			}
			catch (Exception ex)
			{
				item.Exception = ex;
			}
			finally
			{
				item.Done.Set();
			}
		}
	}

	public void Invoke(Action action)
	{
		if (action is null)
		{
			throw new ArgumentNullException(nameof(action));
		}

		if (IsMainThread)
		{
			action();
			return;
		}

		var item = new WorkItem(action);
		_pending.Enqueue(item);
		item.Done.Wait();

		Rethrow(item);
	}

	public T Invoke<T>(Func<T> action)
	{
		if (action is null)
		{
			throw new ArgumentNullException(nameof(action));
		}

		if (IsMainThread)
		{
			return action();
		}

		T result = default!;
		var item = new WorkItem(() => result = action());
		_pending.Enqueue(item);
		item.Done.Wait();

		Rethrow(item);
		return result;
	}

	/// <summary>
	/// Rethrows on the calling thread while keeping the stack trace from the main thread. A plain
	/// <c>throw item.Exception</c> resets it, which reports every failure as originating here and hides
	/// the frame that actually failed.
	/// </summary>
	private static void Rethrow(WorkItem item)
	{
		if (item.Exception is null)
		{
			return;
		}

		ExceptionDispatchInfo.Capture(item.Exception).Throw();
	}
}

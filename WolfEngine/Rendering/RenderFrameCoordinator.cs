namespace WolfEngine.Rendering;

/// <summary>
/// Publishes frame boundaries after the render graph has submitted and presented a frame.
/// This is intentionally separate from <see cref="EditorFrameCoordinator"/>, whose sequence
/// advances when the editor has only published work for the renderer.
/// </summary>
public sealed class RenderFrameCoordinator
{
	private sealed record Waiter(long TargetSequence, TaskCompletionSource<long> Completion);

	private readonly object _sync = new();
	private readonly List<Waiter> _waiters = [];
	private long _completedSequence;

	public long CompletedSequence
	{
		get
		{
			lock (_sync)
			{
				return _completedSequence;
			}
		}
	}

	public long PublishCompletedFrame()
	{
		List<TaskCompletionSource<long>>? ready = null;
		long sequence;
		lock (_sync)
		{
			sequence = ++_completedSequence;
			for (var index = _waiters.Count - 1; index >= 0; index--)
			{
				if (_waiters[index].TargetSequence > sequence)
				{
					continue;
				}

				ready ??= [];
				ready.Add(_waiters[index].Completion);
				_waiters.RemoveAt(index);
			}
		}

		if (ready is not null)
		{
			for (var index = 0; index < ready.Count; index++)
			{
				ready[index].TrySetResult(sequence);
			}
		}

		return sequence;
	}

	public Task<long> WaitForCompletedFramesAsync(int frameCount, CancellationToken cancellationToken = default)
	{
		if (frameCount <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(frameCount), "Frame count must be positive.");
		}

		long targetSequence;
		lock (_sync)
		{
			targetSequence = checked(_completedSequence + frameCount);
		}
		return WaitForSequenceAsync(targetSequence, cancellationToken);
	}

	public Task<long> WaitForSequenceAsync(long targetSequence, CancellationToken cancellationToken = default)
	{
		if (targetSequence < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(targetSequence));
		}

		TaskCompletionSource<long>? completion = null;
		lock (_sync)
		{
			if (_completedSequence >= targetSequence)
			{
				return Task.FromResult(_completedSequence);
			}

			completion = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
			_waiters.Add(new Waiter(targetSequence, completion));
		}

		if (cancellationToken.CanBeCanceled)
		{
			cancellationToken.Register(() => CancelWaiter(completion!, cancellationToken));
		}

		return completion.Task;
	}

	private void CancelWaiter(TaskCompletionSource<long> completion, CancellationToken cancellationToken)
	{
		lock (_sync)
		{
			for (var index = _waiters.Count - 1; index >= 0; index--)
			{
				if (ReferenceEquals(_waiters[index].Completion, completion))
				{
					_waiters.RemoveAt(index);
					break;
				}
			}
		}

		completion.TrySetCanceled(cancellationToken);
	}
}

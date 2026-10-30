namespace WolfEngine.Rendering;

/// <summary>
/// Publishes frame boundaries after the renderer has submitted and presented a frame.
/// This is intentionally separate from <see cref="EditorFrameCoordinator"/>, whose sequence
/// advances when the editor has only published work for the renderer.
/// </summary>
/// <remarks>
/// Two sequences advance here. Every presented frame advances <see cref="CompletedSequence"/>; only a frame that ran
/// the render graph also advances <see cref="CompletedSceneSequence"/>. They differ when the editor draws its UI
/// more often than the scene, as when it limits the viewport's frame rate.
/// </remarks>
public sealed class RenderFrameCoordinator
{
	private sealed record Waiter(long TargetSequence, bool SceneFrames, TaskCompletionSource<long> Completion);

	private readonly object _sync = new();
	private readonly List<Waiter> _waiters = [];
	private long _completedSequence;
	private long _completedSceneSequence;

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

	/// <summary>Frames that ran the render graph, as opposed to frames that only presented the UI.</summary>
	public long CompletedSceneSequence
	{
		get
		{
			lock (_sync)
			{
				return _completedSceneSequence;
			}
		}
	}

	/// <param name="sceneFrame">Whether the frame ran the render graph, rather than only presenting the UI.</param>
	public long PublishCompletedFrame(bool sceneFrame = true)
	{
		List<(TaskCompletionSource<long> Completion, long Sequence)>? ready = null;
		long sequence;
		lock (_sync)
		{
			sequence = ++_completedSequence;
			if (sceneFrame)
			{
				_completedSceneSequence++;
			}

			for (var index = _waiters.Count - 1; index >= 0; index--)
			{
				var waiter = _waiters[index];
				var reached = waiter.SceneFrames ? _completedSceneSequence : sequence;
				if (waiter.TargetSequence > reached)
				{
					continue;
				}

				ready ??= [];
				ready.Add((waiter.Completion, reached));
				_waiters.RemoveAt(index);
			}
		}

		if (ready is not null)
		{
			for (var index = 0; index < ready.Count; index++)
			{
				ready[index].Completion.TrySetResult(ready[index].Sequence);
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

	/// <summary>
	/// Waits for <paramref name="frameCount"/> more frames that ran the render graph, and returns the scene sequence
	/// reached. Frames that only presented the UI do not count.
	/// </summary>
	public Task<long> WaitForCompletedSceneFramesAsync(int frameCount, CancellationToken cancellationToken = default)
	{
		if (frameCount <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(frameCount), "Frame count must be positive.");
		}

		TaskCompletionSource<long> completion;
		lock (_sync)
		{
			completion = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
			_waiters.Add(new Waiter(checked(_completedSceneSequence + frameCount), SceneFrames: true, completion));
		}

		RegisterCancellation(completion, cancellationToken);
		return completion.Task;
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
			_waiters.Add(new Waiter(targetSequence, SceneFrames: false, completion));
		}

		RegisterCancellation(completion, cancellationToken);
		return completion.Task;
	}

	private void RegisterCancellation(TaskCompletionSource<long> completion, CancellationToken cancellationToken)
	{
		if (cancellationToken.CanBeCanceled)
		{
			cancellationToken.Register(() => CancelWaiter(completion, cancellationToken));
		}
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

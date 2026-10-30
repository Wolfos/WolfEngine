namespace WolfEngine.Rendering;

public sealed class EditorFrameCoordinator
{
    private readonly object _sync = new();
    private long _publishedSequence;
    // The newest published frame the render thread has picked up.
    private long _observedSequence;
    private bool _shutdownRequested;

    /// <summary>The most recently published editor frame sequence.</summary>
    public long CompletedSequence
    {
        get
        {
            lock (_sync)
            {
                return _publishedSequence;
            }
        }
    }

    public bool IsShutdownRequested
    {
        get
        {
            lock (_sync)
            {
                return _shutdownRequested;
            }
        }
    }

    public long PublishCompletedFrame()
    {
        lock (_sync)
        {
            _publishedSequence++;
            Monitor.PulseAll(_sync);
            return _publishedSequence;
        }
    }

    public void RequestShutdown()
    {
        lock (_sync)
        {
            _shutdownRequested = true;
            Monitor.PulseAll(_sync);
        }
    }

    /// <summary>
    /// Editor thread: blocks until the render thread has picked up every published frame, or shutdown is requested.
    /// </summary>
    /// <remarks>
    /// Keeps the editor at most one frame ahead of the renderer. Writing a scene snapshot already waits like this,
    /// for its buffer slot, but a frame that publishes only UI writes no snapshot and would otherwise run ahead,
    /// producing UI frames the renderer never draws.
    /// </remarks>
    public void WaitUntilObserved()
    {
        lock (_sync)
        {
            while (_observedSequence < _publishedSequence && _shutdownRequested == false)
            {
                Monitor.Wait(_sync);
            }
        }
    }

    public bool TryWaitForNextFrame(long lastObservedSequence, Action pumpMainThreadWork, out long sequence)
    {
        ArgumentNullException.ThrowIfNull(pumpMainThreadWork);

        while (true)
        {
            lock (_sync)
            {
                if (TryObserve(lastObservedSequence, out sequence, out var published))
                {
                    return published;
                }
            }

            pumpMainThreadWork();

            lock (_sync)
            {
                if (TryObserve(lastObservedSequence, out sequence, out var published))
                {
                    return published;
                }

                Monitor.Wait(_sync, millisecondsTimeout: 1);
            }
        }
    }

    private bool TryObserve(long lastObservedSequence, out long sequence, out bool published)
    {
        sequence = _publishedSequence;

        if (_publishedSequence > lastObservedSequence)
        {
            _observedSequence = _publishedSequence;
            Monitor.PulseAll(_sync);
            published = true;
            return true;
        }

        published = false;
        return _shutdownRequested;
    }
}

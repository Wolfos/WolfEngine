namespace WolfEngine.Rendering;

public sealed class EditorFrameCoordinator
{
    private readonly object _sync = new();
    private long _publishedSequence;
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
            published = true;
            return true;
        }

        published = false;
        return _shutdownRequested;
    }
}

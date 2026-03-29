using System;
using System.Threading;

namespace WolfEngine.Rendering;

public sealed class EditorFrameCoordinator
{
    private readonly object _sync = new();
    private long _publishedSequence;

    public long PublishCompletedFrame()
    {
        lock (_sync)
        {
            _publishedSequence++;
            Monitor.PulseAll(_sync);
            return _publishedSequence;
        }
    }

    public long WaitForNextFrame(long lastObservedSequence, Action pumpMainThreadWork)
    {
        ArgumentNullException.ThrowIfNull(pumpMainThreadWork);

        while (true)
        {
            lock (_sync)
            {
                if (_publishedSequence > lastObservedSequence)
                {
                    return _publishedSequence;
                }
            }

            pumpMainThreadWork();

            lock (_sync)
            {
                if (_publishedSequence > lastObservedSequence)
                {
                    return _publishedSequence;
                }

                Monitor.Wait(_sync, millisecondsTimeout: 1);
            }
        }
    }
}

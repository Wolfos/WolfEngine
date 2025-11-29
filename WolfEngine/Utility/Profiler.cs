using System.Diagnostics;

namespace WolfEngine.Utility;

public static class Profiler
{
    private sealed class BlockStats
    {
        public double TotalMs;
        public double MaxMs;
        public int Count;
    }

    private static readonly Dictionary<string, BlockStats> _blocks = new();
    private static readonly Dictionary<string, Stack<long>> _activeStarts = new();
    private static readonly double _timestampToMilliseconds = 1000.0 / Stopwatch.Frequency;

    public static void StartBlock(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (_activeStarts.TryGetValue(name, out var stack) == false)
        {
            stack = new Stack<long>();
            _activeStarts[name] = stack;
        }

        stack.Push(Stopwatch.GetTimestamp());
    }

    public static void EndBlock(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (_activeStarts.TryGetValue(name, out var stack) == false || stack.Count == 0)
        {
            return;
        }

        var startTicks = stack.Pop();
        var elapsedMs = (Stopwatch.GetTimestamp() - startTicks) * _timestampToMilliseconds;

        if (_blocks.TryGetValue(name, out var stats) == false)
        {
            stats = new BlockStats();
            _blocks[name] = stats;
        }

        stats.TotalMs += elapsedMs;
        stats.MaxMs = Math.Max(stats.MaxMs, elapsedMs);
        stats.Count++;
    }

    public static void EndOfFrame()
    {
        _activeStarts.Clear();
    }

    public static void Report()
    {
        foreach (var (name, stats) in _blocks)
        {
            if (stats.Count == 0)
            {
                continue;
            }

            var avgMs = stats.TotalMs / stats.Count;
            Console.Out.WriteLine($"{name} - avg: {avgMs:F2}ms, max: {stats.MaxMs:F2}ms");
        }

        _blocks.Clear();
    }
}
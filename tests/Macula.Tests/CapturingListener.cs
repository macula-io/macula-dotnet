using System.Collections.Concurrent;
using System.Diagnostics;

namespace Macula.Tests;

/// <summary>Keeps every trace event, with its level, written while it is listening.</summary>
internal sealed class CapturingListener : TraceListener
{
    private readonly ConcurrentQueue<(TraceEventType Level, string Message)> _events = new();

    public IReadOnlyList<(TraceEventType Level, string Message)> Events => _events.ToArray();

    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message)
    {
        if (message is not null)
        {
            _events.Enqueue((eventType, message));
        }
    }

    public override void Write(string? message)
    {
    }

    public override void WriteLine(string? message)
    {
    }
}

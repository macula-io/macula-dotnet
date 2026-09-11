namespace Macula.Connection;

/// <summary>
/// A session direct dial dialed for a request, shared by every later request
/// that reuses it and closed when the last of them releases it. A request
/// that finds its last lease already released doesn't reuse it: direct dial
/// dials again. A session the application opened has none of this, and
/// direct dial never closes it.
/// </summary>
internal sealed class DialedSession<TSession> where TSession : class
{
    private readonly object _gate = new();
    private readonly Func<TSession, ValueTask> _close;
    private int _leases = 1;

    internal DialedSession(TSession session, Func<TSession, ValueTask> close)
    {
        Session = session;
        _close = close;
    }

    internal TSession Session { get; }

    /// <summary>Takes one more lease, unless the last one was already released.</summary>
    internal bool TryLease()
    {
        lock (_gate)
        {
            if (_leases == 0)
            {
                return false;
            }
            _leases++;
            return true;
        }
    }

    /// <summary>
    /// Gives one lease back, and closes the session when it was the last. A
    /// release after the last one does nothing.
    /// </summary>
    internal ValueTask ReleaseAsync()
    {
        lock (_gate)
        {
            if (_leases == 0 || --_leases > 0)
            {
                return ValueTask.CompletedTask;
            }
        }
        return _close(Session);
    }
}

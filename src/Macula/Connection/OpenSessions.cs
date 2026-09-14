using System.Collections.Concurrent;
using System.Runtime.Versioning;

namespace Macula.Connection;

/// <summary>This process's open sessions, which every <see cref="Session"/> registers with.</summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
internal static class OpenSessions
{
    internal static OpenSessions<Session> Live { get; } = new();
}

/// <summary>
/// The sessions currently open, at most one per (identity, station) pair.
///
/// A station keeps one connection per identity: when a newer connection
/// under an identity completes its handshake, the station closes the older
/// one (macula_station_listener.erl, replaced_by_newer_handshake). So code
/// that needs a station under an identity this process already holds a
/// session to must reuse that session rather than dial again, or it closes
/// its own session. Direct dial looks here before dialing.
///
/// A session registers once its HELLO is accepted and unregisters when it
/// closes. The newest registration for a pair wins, matching the station;
/// unregistering removes an entry only if it still holds that same session,
/// so closing an older session never drops a newer one.
/// </summary>
internal sealed class OpenSessions<TSession> where TSession : class
{
    private readonly ConcurrentDictionary<(string Identity, string Station), TSession> _open = new();

    internal void Register(byte[] identity, byte[] station, TSession session) =>
        _open[Key(identity, station)] = session;

    internal void Unregister(byte[] identity, byte[] station, TSession session) =>
        _open.TryRemove(new KeyValuePair<(string Identity, string Station), TSession>(Key(identity, station), session));

    internal TSession? Find(byte[] identity, byte[] station) =>
        _open.TryGetValue(Key(identity, station), out var session) ? session : null;

    private static (string Identity, string Station) Key(byte[] identity, byte[] station) =>
        (Convert.ToHexString(identity), Convert.ToHexString(station));
}

using System.Collections.Concurrent;
using System.Globalization;

namespace Macula.Connection;

/// <summary>
/// Inbound-EVENT dedup for <see cref="StationPool"/>: the pool subscribes
/// the same (Realm, Topic) against every link, so the same fact can arrive
/// redundantly once per link. Keys on the full 4-tuple (Realm, Publisher,
/// Seq, Topic) plus the subscription pattern the event arrived for, so an
/// event matching two pool subscriptions, "orders/*" and "orders/placed", is
/// new once for each and reaches both sets of handlers -- NOT the 3-tuple
/// (Realm, Publisher, Seq) the Erlang
/// reference's own `macula_client_dedup.erl` still uses. That narrower key
/// is the exact collision shape fixed station-side in f20be0b (broadened
/// station relay dedup from {Publisher,Seq} to {Publisher,Seq,Topic}) and
/// separately flagged upstream as a live client-side gap in the reference
/// itself -- not something to reproduce here just because it's "the
/// reference behavior."
///
/// `byte[]` fields (Realm, Publisher) are hex-encoded before use as a
/// dictionary key: a raw `byte[]` uses REFERENCE equality in .NET (arrays
/// don't override Equals/GetHashCode), so a `ConcurrentDictionary` keyed
/// directly on `byte[]` would treat every freshly-parsed EVENT's key as
/// new even when two fields are byte-for-byte identical -- silently
/// turning dedup into a no-op that no single-link test would ever catch.
/// </summary>
internal sealed class EventDedup
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new();
    private readonly TimeSpan _window;

    public EventDedup(TimeSpan window)
    {
        _window = window;
    }

    /// <summary>Insert-if-absent. Returns true on first sighting of this (Realm,Publisher,Seq,Topic) for pattern, false on every subsequent one.</summary>
    public bool CheckNew(byte[] realm, byte[] publisher, ulong seq, string topic, string pattern) =>
        _seen.TryAdd(Key(realm, publisher, seq, topic, pattern), DateTimeOffset.UtcNow);

    /// <summary>Drop entries older than the configured window. Returns the number removed.</summary>
    public int Sweep()
    {
        var cutoff = DateTimeOffset.UtcNow - _window;
        var removed = 0;
        foreach (var (key, seenAt) in _seen)
        {
            if (seenAt < cutoff && _seen.TryRemove(key, out _))
            {
                removed++;
            }
        }
        return removed;
    }

    // The topic's length keeps a topic containing ':' from running into the
    // pattern after it.
    private static string Key(byte[] realm, byte[] publisher, ulong seq, string topic, string pattern) =>
        string.Create(CultureInfo.InvariantCulture, $"{Convert.ToHexStringLower(realm)}:{Convert.ToHexStringLower(publisher)}:{seq}:{topic.Length}:{topic}:{pattern}");
}

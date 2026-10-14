using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Macula.Native;

namespace Macula;

/// <summary>A publication a subscription heard, its signature verified.</summary>
/// <param name="Publisher">The node that published it.</param>
/// <param name="Realm">The realm it was published in.</param>
/// <param name="Topic">Its topic.</param>
/// <param name="Seq">The publisher's sequence number for it.</param>
/// <param name="PublishedAt">When it was published, in Unix milliseconds.</param>
/// <param name="Payload">What it carries.</param>
/// <param name="DeliveredVia">How it arrived: <c>direct</c> or <c>plumtree</c>.</param>
/// <param name="Sealed">
/// Whether it arrived sealed, which a group subscription opens: it then also carries <paramref name="SealKeyId"/>.
/// False for a clear event, and a clear event under a group's prefix is delivered as it is unless the group's own
/// policy is <c>required</c> — which the distributor sets, not the subscriber, and which the library is the only
/// thing that knows. So a subscriber that wants sealed events alone checks this: any realm member may publish a
/// clear event under the prefix, and it arrives here with <paramref name="Sealed"/> false and a payload. Set for
/// any event whose publisher sealed it, so a plain subscription under a group prefix hears such an event with its
/// <paramref name="Payload"/> null and <paramref name="Unopened"/> <c>no_group</c>.
/// </param>
/// <param name="SealKeyId">
/// The 8-byte id, as hex, of the epoch the event arrived under — named whether or not it opened, so an event with
/// <paramref name="Unopened"/> set still carries it. Null when the event did not arrive sealed at all.
/// </param>
/// <param name="Unopened">
/// Why an event under a group could not be opened, from macula's closed set: <c>not_a_member</c>,
/// <c>membership_unknown</c>, <c>unknown_epoch</c>, <c>epoch_expired</c>, <c>no_distributor</c>, <c>no_group</c> or
/// <c>tag_invalid</c>. Null when the event carries its payload.
/// </param>
public sealed record MeshEvent(MeshId Publisher, MeshId Realm, string Topic, ulong Seq, ulong PublishedAt,
    JsonNode? Payload, string DeliveredVia, bool Sealed = false, string? SealKeyId = null, string? Unopened = null);

/// <summary>
/// A subscription to a topic. Its events wait for a reader in libmacula (256 at most; one arriving to a
/// full inbox is dropped and counted in <see cref="Dropped"/>). Dispose it to unsubscribe.
/// </summary>
public sealed class Subscription : IAsyncDisposable
{
    private readonly SubscriptionHandle _handle;
    private readonly NativePump<MeshEvent> _pump;

    internal Subscription(SubscriptionHandle handle)
    {
        _handle = handle;
        _pump = new NativePump<MeshEvent>("macula subscription", 64, token =>
        {
            nint err = 0;
            var text = NativeCall.TakeString(Libmacula.macula_subscription_next(_handle, 0, token, out var closed, ref err));
            NativeCall.Check(err);
            return closed == 1 || text is null ? new(default!, true) : new(EventOf(text), false);
        });
    }

    private static MeshEvent EventOf(string text)
    {
        using var json = JsonDocument.Parse(text);
        var e = json.RootElement;
        return new MeshEvent(MeshId.Parse(e.GetProperty("publisher").GetString()!),
            MeshId.Parse(e.GetProperty("realm").GetString()!), e.GetProperty("topic").GetString()!,
            e.GetProperty("seq").GetUInt64(), e.GetProperty("published_at").GetUInt64(),
            Payload.FromElement(e.GetProperty("payload")), e.GetProperty("delivered_via").GetString()!,
            Sealed: e.TryGetProperty("sealed", out var sealedFlag) && sealedFlag.GetInt32() == 1,
            SealKeyId: TextOf(e, "seal_key_id"), Unopened: TextOf(e, "unopened"));
    }

    // A field the event carries as a string when it has one at all.
    private static string? TextOf(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>The subscription's events as they arrive, until it is disposed or its pool closes.</summary>
    public async IAsyncEnumerable<MeshEvent> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var e in _pump.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return e;
        }
    }

    /// <summary>How many events were dropped because the reader fell behind.</summary>
    public ulong Dropped
    {
        get
        {
            nint err = 0;
            var dropped = Libmacula.macula_subscription_dropped(_handle, ref err);
            NativeCall.Check(err);
            return dropped;
        }
    }

    /// <summary>Unsubscribes.</summary>
    public async ValueTask DisposeAsync()
    {
        await _pump.DisposeAsync().ConfigureAwait(false);
        _handle.Dispose();
    }
}

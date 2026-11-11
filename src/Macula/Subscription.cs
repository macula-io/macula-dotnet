using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Macula.Native;

namespace Macula;

/// <summary>
/// One item a subscription hands over: a <see cref="MeshEvent"/> for a publication, or a
/// <see cref="MeshNotice"/> when libmacula reports that events may have been missed. Every item on the wire
/// carries a <c>kind</c> (macula-go's cabi/CONTRACT.md, "Inboxes"), and a notice has none of an event's
/// fields: read as an event it would be a failure on the first link lost, and the gap that says to resync
/// would be lost the same way.
/// </summary>
public abstract record MeshItem;

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
    JsonNode? Payload, string DeliveredVia, bool Sealed = false, string? SealKeyId = null, string? Unopened = null)
    : MeshItem;

/// <summary>
/// What a subscription reports when events may have been missed: its last link to a station ended
/// (<see cref="Interrupted"/>: <see cref="FromMs"/> is when that link last heard from its station, and
/// <see cref="Reason"/> why it ended), or a station carries the subscription again (<see cref="Gap"/>: what
/// fell between <see cref="FromMs"/> and <see cref="ToMs"/> may be gone, so resync). The times are this
/// node's clock, hints and not a filter on <see cref="MeshEvent.PublishedAt"/>.
/// <para>
/// A notice arrives only where no other link carried the subscription through, and never at the first
/// subscribe. It is handed over as itself, never as a <see cref="MeshEvent"/> (macula-go's cabi/CONTRACT.md,
/// "Inboxes"); a <see cref="Kind"/> this package does not read is refused by name instead.
/// </para>
/// </summary>
/// <param name="Kind">Which notice this is: <see cref="Interrupted"/> or <see cref="Gap"/>.</param>
/// <param name="FromMs">From when events may have been missed, in Unix milliseconds.</param>
/// <param name="ToMs">For a gap, until when; null for an interruption, which names no time it was carried again.</param>
/// <param name="Reason">The library's reason, such as <c>liveness_lost</c> or <c>goodbye</c>.</param>
public sealed record MeshNotice(string Kind, ulong FromMs, ulong? ToMs, string Reason) : MeshItem
{
    /// <summary>The kind of a notice saying the subscription's last link to a station ended.</summary>
    public const string Interrupted = "interrupted";

    /// <summary>The kind of a notice saying a station carries the subscription again, and what fell between.</summary>
    public const string Gap = "gap";
}

/// <summary>
/// A subscription to a topic. Its events wait for a reader in libmacula (256 at most; one arriving to a
/// full inbox is dropped and counted in <see cref="Dropped"/>). Dispose it to unsubscribe.
/// </summary>
public sealed class Subscription : IAsyncDisposable
{
    private readonly SubscriptionHandle _handle;
    private readonly NativePump<MeshItem> _pump;

    internal Subscription(SubscriptionHandle handle)
        : this(handle, token => NextOf(handle, token))
    {
    }

    // A subscription whose pump takes what a test says it takes, with no library behind it: what ReadAllAsync
    // hands a reader is then shown with a notice's own text, which no library available to a test reports
    // (macula-dotnet#25 r=1: a pump that reads every item as an event, and a reader that drops every notice,
    // both left every other test green).
    internal Subscription(SubscriptionHandle handle, Func<nuint, NativePump<MeshItem>.Taken> next)
    {
        _handle = handle;
        _pump = new NativePump<MeshItem>("macula subscription", 64, next);
    }

    // One turn of the pump: the item libmacula handed over, read by its kind, or the end of the subscription.
    internal static NativePump<MeshItem>.Taken ItemFrom(string? text, bool closed) =>
        closed || text is null ? new(default!, true) : new(ItemOf(text), false);

    // The native take, and the one step a reader's item is made by (ItemFrom).
    private static NativePump<MeshItem>.Taken NextOf(SubscriptionHandle handle, nuint token)
    {
        nint err = 0;
        var text = NativeCall.TakeString(Libmacula.macula_subscription_next(handle, 0, token, out var closed, ref err));
        NativeCall.Check(err);
        return ItemFrom(text, closed == 1);
    }

    // The one place an item's kind is read, and so the one place to hold to what each kind becomes.
    internal static MeshItem ItemOf(string text)
    {
        using var json = JsonDocument.Parse(text);
        var e = json.RootElement;
        var kind = e.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : "event";
        return kind switch
        {
            "event" => EventOf(e),
            MeshNotice.Interrupted or MeshNotice.Gap => NoticeOf(kind, e),
            _ => throw new MaculaException(ErrorKind.Failed,
                $"a subscription item of kind \"{kind}\", which this Macula package does not read"),
        };
    }

    private static MeshNotice NoticeOf(string kind, JsonElement e) =>
        new(kind, e.GetProperty("from_ms").GetUInt64(),
            e.TryGetProperty("to_ms", out var to) && to.ValueKind == JsonValueKind.Number ? to.GetUInt64() : null,
            e.GetProperty("reason").GetString()!);

    private static MeshEvent EventOf(JsonElement e) =>
        new(MeshId.Parse(e.GetProperty("publisher").GetString()!),
            MeshId.Parse(e.GetProperty("realm").GetString()!), e.GetProperty("topic").GetString()!,
            e.GetProperty("seq").GetUInt64(), e.GetProperty("published_at").GetUInt64(),
            Payload.FromElement(e.GetProperty("payload")), e.GetProperty("delivered_via").GetString()!,
            Sealed: e.TryGetProperty("sealed", out var sealedFlag) && sealedFlag.GetInt32() == 1,
            SealKeyId: TextOf(e, "seal_key_id"), Unopened: TextOf(e, "unopened"));

    // A field the event carries as a string when it has one at all.
    private static string? TextOf(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>
    /// The subscription's items as they arrive, until it is disposed or its pool closes: a
    /// <see cref="MeshEvent"/> for a publication, a <see cref="MeshNotice"/> when events may have been missed.
    /// </summary>
    public async IAsyncEnumerable<MeshItem> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in _pump.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return item;
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

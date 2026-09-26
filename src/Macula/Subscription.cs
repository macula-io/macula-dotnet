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
public sealed record MeshEvent(MeshId Publisher, MeshId Realm, string Topic, ulong Seq, ulong PublishedAt,
    JsonNode? Payload, string DeliveredVia);

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
            Payload.FromElement(e.GetProperty("payload")), e.GetProperty("delivered_via").GetString()!);
    }

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

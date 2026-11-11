using Macula.Native;

namespace Macula.Tests;

// Every item a subscription hands over carries a kind (macula-go's cabi/CONTRACT.md, "Inboxes"), and a
// notice is not an event: read as one it would be a failure on the first link lost, and the gap that says
// to resync would be lost the same way.
public sealed class NoticeTests
{
    private static readonly string Node = new('a', 64);

    [Fact]
    public void AnInterruptionIsItselfNamingWhenAndWhy()
    {
        var notice = Assert.IsType<MeshNotice>(Subscription.ItemOf(
            """{"kind":"interrupted","from_ms":1762992000000,"reason":"liveness_lost"}"""));
        Assert.Equal(MeshNotice.Interrupted, notice.Kind);
        Assert.Equal(1762992000000UL, notice.FromMs);
        // An interruption names no time it was carried again.
        Assert.Null(notice.ToMs);
        Assert.Equal("liveness_lost", notice.Reason);
    }

    [Fact]
    public void AGapCarriesBothTimes()
    {
        var notice = Assert.IsType<MeshNotice>(Subscription.ItemOf(
            """{"kind":"gap","from_ms":1762992000000,"to_ms":1762992060000,"reason":"goodbye"}"""));
        Assert.Equal(MeshNotice.Gap, notice.Kind);
        Assert.Equal(1762992000000UL, notice.FromMs);
        Assert.Equal(1762992060000UL, notice.ToMs);
        Assert.Equal("goodbye", notice.Reason);
    }

    [Fact]
    public void APublicationIsAnEvent()
    {
        var e = Assert.IsType<MeshEvent>(Subscription.ItemOf($$"""
            {"kind":"event","publisher":"{{Node}}","realm":"{{Node}}","topic":"dotnet.news","seq":7,
             "published_at":1762992000000,"payload":{"n":1},"delivered_via":"direct"}
            """));
        Assert.Equal(7UL, e.Seq);
        Assert.Equal("dotnet.news", e.Topic);
        Assert.Equal(1, e.Payload!["n"]!.GetValue<long>());
        Assert.False(e.Sealed);
        Assert.Null(e.SealKeyId);
        Assert.Null(e.Unopened);
    }

    // The oldest form an event arrives in names no kind at all, and it is an event: the wire's first
    // subscriptions predate the field.
    [Fact]
    public void AnItemThatNamesNoKindIsAnEvent()
    {
        var e = Assert.IsType<MeshEvent>(Subscription.ItemOf($$"""
            {"publisher":"{{Node}}","realm":"{{Node}}","topic":"dotnet.news","seq":1,
             "published_at":1762992000000,"payload":null,"delivered_via":"plumtree"}
            """));
        Assert.Equal("plumtree", e.DeliveredVia);
        Assert.Null(e.Payload);
    }

    // A notice reaches a reader as a notice: through the pump's own step and ReadAllAsync's own path, not
    // ItemOf called by hand. Three ways of losing that are otherwise invisible: a pump that reads every item as
    // an event (which fails on the first link lost), a reader that hands over events alone (which drops the gap
    // that says to resync), and a step the pump never runs at all. So the test fakes only the native take — the
    // text and the closed flag macula_subscription_next answers — and NextOf, with its ItemFrom call, runs.
    [Fact]
    public async Task ANoticeReachesAReaderThroughThePump()
    {
        var handedOver = new Queue<Subscription.NativeTake>([
            new("""{"kind":"gap","from_ms":1762992000000,"to_ms":1762992060000,"reason":"goodbye"}""", false),
            new(null, true),
        ]);
        // The handle is never reached: the take is the test's, so no library stands behind this subscription.
        await using var subscription = new Subscription(new SubscriptionHandle(), _ => handedOver.Dequeue());

        var items = new List<MeshItem>();
        await foreach (var item in subscription.ReadAllAsync())
        {
            items.Add(item);
        }

        var notice = Assert.IsType<MeshNotice>(Assert.Single(items));
        Assert.Equal(MeshNotice.Gap, notice.Kind);
        Assert.Equal(1762992060000UL, notice.ToMs);
        Assert.Equal("goodbye", notice.Reason);
    }

    [Fact]
    public void AKindThisPackageDoesNotReadIsRefusedByName()
    {
        var e = Assert.Throws<MaculaException>(() => Subscription.ItemOf(
            """{"kind":"resumed","from_ms":1762992000000,"to_ms":1762992060000,"reason":"goodbye"}"""));
        Assert.Equal(ErrorKind.Failed, e.Kind);
        Assert.Contains("resumed", e.Message);
    }
}

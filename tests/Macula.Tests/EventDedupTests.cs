using Macula.Connection;

namespace Macula.Tests;

/// <summary>
/// Pure logic, no network -- <see cref="EventDedup"/> is a plain
/// insert-if-absent table keyed on (Realm, Publisher, Seq, Topic) and the
/// subscription pattern the event arrived for.
/// </summary>
public class EventDedupTests
{
    private const string Pattern = "topic.*";

    private static byte[] Realm(byte fill = 0x01) => Enumerable.Repeat(fill, 32).ToArray();
    private static byte[] Publisher(byte fill = 0x02) => Enumerable.Repeat(fill, 32).ToArray();

    [Fact]
    public void First_sighting_of_a_key_is_new()
    {
        var dedup = new EventDedup(TimeSpan.FromMinutes(1));
        Assert.True(dedup.CheckNew(Realm(), Publisher(), 1, "topic.a", Pattern));
    }

    [Fact]
    public void Repeating_the_exact_same_key_is_a_duplicate()
    {
        var dedup = new EventDedup(TimeSpan.FromMinutes(1));
        Assert.True(dedup.CheckNew(Realm(), Publisher(), 1, "topic.a", Pattern));
        Assert.False(dedup.CheckNew(Realm(), Publisher(), 1, "topic.a", Pattern));
        Assert.False(dedup.CheckNew(Realm(), Publisher(), 1, "topic.a", Pattern));
    }

    [Fact]
    public void Duplicate_check_uses_byte_CONTENT_equality_not_reference_equality()
    {
        // Two separately-allocated byte[] instances with identical bytes --
        // this is exactly the case a naive Dictionary/tuple keyed directly
        // on byte[] gets wrong in .NET (reference equality), which would
        // make dedup silently a no-op. Every EVENT parsed off the wire is
        // necessarily a fresh array, so this is the ONLY way dedup is ever
        // actually exercised in practice, not an edge case.
        var dedup = new EventDedup(TimeSpan.FromMinutes(1));
        var realmA = new byte[32];
        var realmB = new byte[32]; // same content, different instance
        var pubA = new byte[32];
        var pubB = new byte[32];
        realmA[0] = realmB[0] = 0xAB;
        pubA[0] = pubB[0] = 0xCD;

        Assert.True(dedup.CheckNew(realmA, pubA, 42, "topic.a", Pattern));
        Assert.False(dedup.CheckNew(realmB, pubB, 42, "topic.a", Pattern));
    }

    [Fact]
    public void Same_realm_publisher_and_seq_on_two_DIFFERENT_topics_are_both_new()
    {
        // The exact regression this 4-field key exists to avoid: the
        // Erlang reference's own client-side dedup (macula_client_dedup.erl)
        // keys on {Realm,Publisher,Seq} only -- three fields -- which is
        // the identical collision shape fixed station-side in f20be0b. A
        // publisher whose Seq counter isn't scoped per-topic would have
        // its second topic's first fact silently swallowed under that
        // narrower key. This pool's dedup must not reproduce that gap.
        var dedup = new EventDedup(TimeSpan.FromMinutes(1));
        var realm = Realm();
        var publisher = Publisher();

        Assert.True(dedup.CheckNew(realm, publisher, 7, "topic.a", Pattern));
        Assert.True(dedup.CheckNew(realm, publisher, 7, "topic.b", Pattern));
    }

    [Fact]
    public void Different_realm_or_publisher_or_seq_are_independently_new()
    {
        var dedup = new EventDedup(TimeSpan.FromMinutes(1));
        var realm = Realm();
        var publisher = Publisher();

        Assert.True(dedup.CheckNew(realm, publisher, 1, "t", Pattern));
        Assert.True(dedup.CheckNew(Realm(0x99), publisher, 1, "t", Pattern));
        Assert.True(dedup.CheckNew(realm, Publisher(0x99), 1, "t", Pattern));
        Assert.True(dedup.CheckNew(realm, publisher, 2, "t", Pattern));
    }

    [Fact]
    public void The_same_event_for_two_different_patterns_is_new_for_each()
    {
        // An event matching two pool subscriptions, "topic.*" and "topic.a",
        // reaches both sets of handlers, so it is new once per pattern.
        var dedup = new EventDedup(TimeSpan.FromMinutes(1));

        Assert.True(dedup.CheckNew(Realm(), Publisher(), 1, "topic.a", "topic.*"));
        Assert.True(dedup.CheckNew(Realm(), Publisher(), 1, "topic.a", "topic.a"));
        Assert.False(dedup.CheckNew(Realm(), Publisher(), 1, "topic.a", "topic.*"));
    }

    [Fact]
    public void A_topic_containing_the_key_separator_does_not_run_into_the_pattern()
    {
        var dedup = new EventDedup(TimeSpan.FromMinutes(1));

        Assert.True(dedup.CheckNew(Realm(), Publisher(), 1, "a:b", "c"));
        Assert.True(dedup.CheckNew(Realm(), Publisher(), 1, "a", "b:c"));
    }

    [Fact]
    public void Sweep_removes_only_entries_older_than_the_window()
    {
        var dedup = new EventDedup(TimeSpan.FromMilliseconds(50));
        Assert.True(dedup.CheckNew(Realm(), Publisher(), 1, "old", Pattern));

        Thread.Sleep(120);
        Assert.True(dedup.CheckNew(Realm(), Publisher(), 2, "fresh", Pattern));

        var removed = dedup.Sweep();

        Assert.Equal(1, removed);
        // The swept entry's key is free again; the fresh one's is not.
        Assert.True(dedup.CheckNew(Realm(), Publisher(), 1, "old", Pattern));
        Assert.False(dedup.CheckNew(Realm(), Publisher(), 2, "fresh", Pattern));
    }
}

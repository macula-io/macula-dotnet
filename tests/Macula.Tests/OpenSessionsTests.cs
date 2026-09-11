using Macula.Connection;
using Macula.Identity;

namespace Macula.Tests;

/// <summary>
/// The registry of open sessions direct dial reuses instead of dialing a
/// second connection under the same identity, which the station would answer
/// by closing the first.
/// </summary>
public class OpenSessionsTests
{
    private sealed record FakeSession(string Name);

    [Fact]
    public void A_session_is_found_by_its_identity_and_station()
    {
        var (identity, station) = (KeyPair.Generate().NodeId(), KeyPair.Generate().NodeId());
        var open = new OpenSessions<FakeSession>();
        var session = new FakeSession("the caller's session");

        open.Register(identity, station, session);

        Assert.Same(session, open.Find(identity, station));
        Assert.Null(open.Find(KeyPair.Generate().NodeId(), station));
        Assert.Null(open.Find(identity, KeyPair.Generate().NodeId()));
    }

    [Fact]
    public void The_newest_session_per_identity_and_station_wins()
    {
        var (identity, station) = (KeyPair.Generate().NodeId(), KeyPair.Generate().NodeId());
        var open = new OpenSessions<FakeSession>();
        var older = new FakeSession("older");
        var newer = new FakeSession("newer");

        open.Register(identity, station, older);
        open.Register(identity, station, newer);

        Assert.Same(newer, open.Find(identity, station));
    }

    [Fact]
    public void Closing_an_older_session_leaves_the_newer_one_registered()
    {
        var (identity, station) = (KeyPair.Generate().NodeId(), KeyPair.Generate().NodeId());
        var open = new OpenSessions<FakeSession>();
        var older = new FakeSession("older");
        var newer = new FakeSession("newer");
        open.Register(identity, station, older);
        open.Register(identity, station, newer);

        open.Unregister(identity, station, older);

        Assert.Same(newer, open.Find(identity, station));
    }

    [Fact]
    public void A_closed_session_is_no_longer_found()
    {
        var (identity, station) = (KeyPair.Generate().NodeId(), KeyPair.Generate().NodeId());
        var open = new OpenSessions<FakeSession>();
        var session = new FakeSession("the caller's session");
        open.Register(identity, station, session);

        open.Unregister(identity, station, session);

        Assert.Null(open.Find(identity, station));
    }
}

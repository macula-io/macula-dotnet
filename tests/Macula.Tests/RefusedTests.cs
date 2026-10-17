namespace Macula.Tests;

// A station's verdict on a serve or subscribe (cabi/CONTRACT.md "Errors", kind refused, since macula-go
// v0.27.0-rc.4) is a RefusedException naming the station's reason, the registration and the station; any
// other refusal names none of them.
public sealed class RefusedTests
{
    private static readonly string Station = string.Concat(Enumerable.Repeat("ab", 32));

    [Fact]
    public void ARegistrationRefusalNamesItsReasonRegistrationAndStation()
    {
        var e = Assert.IsType<RefusedException>(Errors.FromJson(
            $$"""{"kind":"refused","message":"m","reason":"too_many_providers","registration":"advertise","station":"{{Station}}"}""",
            default));
        Assert.Equal(ErrorKind.Refused, e.Kind);
        Assert.Equal(("too_many_providers", "advertise", Station), (e.Reason, e.Registration, e.Station));
    }

    [Fact]
    public void NoStationAnsweringIsUnacknowledged()
    {
        var e = Assert.IsType<RefusedException>(Errors.FromJson(
            $$"""{"kind":"refused","message":"m","reason":"unacknowledged","registration":"subscribe","station":"{{Station}}"}""",
            default));
        Assert.Equal(("unacknowledged", "subscribe"), (e.Reason, e.Registration));
    }

    [Fact]
    public void ARefusalThatIsNotARegistrationsNamesNothing()
    {
        var e = Assert.IsType<RefusedException>(Errors.FromJson("""{"kind":"refused","message":"m"}""", default));
        Assert.Equal(ErrorKind.Refused, e.Kind);
        Assert.Equal(((string?)null, (string?)null, (string?)null), (e.Reason, e.Registration, e.Station));
    }
}

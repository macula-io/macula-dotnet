using Macula.Native;

namespace Macula.Tests;

// Which wire the load path is willing to speak with, without building a
// library for every case: another major is another wire, and every 1.x peer
// shares the frozen core (macula-go's cabi/CONTRACT.md "Version").
public sealed class LibraryPathTests
{
    [Theory]
    [InlineData("1.0")]
    [InlineData("1.1")]
    [InlineData("1.9")]
    public void AWireOfOurMajorIsSpokenWith(string wire) => Assert.True(Libmacula.AcceptsWire(wire));

    [Theory]
    [InlineData("2.0")]
    [InlineData("0.9")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not a wire")]
    public void AnotherWireIsRefused(string? wire) => Assert.False(Libmacula.AcceptsWire(wire));
}

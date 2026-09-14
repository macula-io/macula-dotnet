using Macula.Identity;
using Xunit;

namespace Macula.Tests;

public class PetnameTests
{
    [Fact]
    public void PetnameIsDeterministicAndShaped()
    {
        const string id = "7374b0cfab4eea68e271c3815a0f78e21e913397f67345f337ddba7a3a88ab3a";
        var first = Petname.For(id);
        Assert.Equal(first, Petname.For(id));
        var parts = first.Split('_');
        Assert.Equal(4, parts.Length);
        Assert.Equal(4, parts[3].Length);
        Assert.True(parts[3].All(char.IsDigit));
    }

    [Fact]
    public void PetnameMatchesReference()
    {
        // Shared fixtures across the SDKs: one label everywhere. The
        // second is proven against the live roster (gentle_maroon_
        // flamingo before the suffix shipped).
        Assert.Equal(
            "calm_navy_narwhal_3381",
            Petname.For("7374b0cfab4eea68e271c3815a0f78e21e913397f67345f337ddba7a3a88ab3a"));
        Assert.Equal(
            "gentle_maroon_flamingo_3490",
            Petname.For("d4b24382f4e033ad9e895070c914c8125c315f243c5a3713183352f65c322b03"));
    }
}

using System.Text.RegularExpressions;
using Macula.Native;

namespace Macula.Tests;

// abi/macula.h is the source of truth for the ABI and the wire: this package writes neither as a literal,
// but reads both from the header at build time (Macula.csproj, GenerateMaculaAbi), and the load path
// refuses a library that reports another ABI than the one it read.
public sealed class AbiHeaderTests
{
    [Fact]
    public void TheBindingsAbiAndWireAreTheHeaders()
    {
        var header = File.ReadAllText(Path.Combine(Root(), "abi", "macula.h"));
        Assert.Equal(Number(header, @"^#define MACULA_ABI_VERSION (\d+)$"), Libmacula.AbiVersion.ToString());
        Assert.Equal(Libmacula.WireVersion, Number(header, @"^#define MACULA_WIRE_VERSION ""([^""]+)""$"));
    }

    // The header is the one the release at libmacula.version ships: the ref names that tag, and the script
    // that fetched the runtimes (scripts/fetch_libmacula.sh) refuses a release whose header differs.
    [Fact]
    public void TheHeaderIsTheOneTheReleaseShips()
    {
        var reference = File.ReadAllText(Path.Combine(Root(), "abi", "MACULA_GO_REF")).Trim().Split(' ');
        Assert.Equal(2, reference.Length);
        Assert.Equal(File.ReadAllText(Path.Combine(Root(), "libmacula.version")).Trim(), reference[0]);
        Assert.Matches("^[0-9a-f]{40}$", reference[1]);
    }

    [Fact]
    public void AnotherAbiIsRefusedNamingBoth()
    {
        var other = Libmacula.AbiVersion + 1;
        var e = Assert.Throws<MaculaException>(() => NativeCall.RefuseAnotherAbi(other));
        Assert.Equal(ErrorKind.Failed, e.Kind);
        Assert.Contains($"ABI {other}", e.Message);
        Assert.Contains($"ABI {Libmacula.AbiVersion}", e.Message);
        // The ABI this package is written against is accepted, and handed back.
        Assert.Equal(Libmacula.AbiVersion, NativeCall.RefuseAnotherAbi(Libmacula.AbiVersion));
    }

    // What the load path accepted is the number the load path itself checked, not one written beside it: a
    // library of another ABI is refused there (NativeCall.Load), so an accepted load records the same number.
    [Fact]
    public void TheLoadPathRecordsTheAbiItAccepted()
    {
        NativeCall.EnsureLoaded();
        Assert.Equal(Libmacula.AbiVersion, NativeCall.CheckedAbi);
        // And the library this process loaded agrees, rather than only the package's own constant.
        Assert.Equal(Libmacula.AbiVersion, Libmacula.macula_abi_version());
    }

    // The first group of the header line that defines it.
    private static string Number(string header, string pattern) =>
        Regex.Match(header, pattern, RegexOptions.Multiline).Groups[1].Value;

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "libmacula.version")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("no libmacula.version above the test output");
    }
}

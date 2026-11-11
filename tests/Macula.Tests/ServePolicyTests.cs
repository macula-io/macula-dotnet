using System.Text.Json;
using Macula.Native;

namespace Macula.Tests;

// The C ABI refuses serve options that name no policy (ABI 2, macula-go#57, #60), so a procedure that serves
// anyone says so in the source with Open rather than leaving an argument out. Both halves are held here: the
// options this package writes always name a policy, and the library itself refuses the options this package
// no longer writes, before it looks the pool up.
public sealed class ServePolicyTests
{
    [Fact]
    public void TheOptionsAlwaysNameThePolicy()
    {
        using var json = JsonDocument.Parse(Sealing.ServeOptionsJson(new Open(), null));
        var policy = json.RootElement.GetProperty("policy");
        Assert.Equal("open", policy.GetProperty("kind").GetString());
        // An Open carries nothing else: no issuer, key id or action.
        Assert.Single(policy.EnumerateObject());
    }

    [Fact]
    public void TheConfidentialityTravelsBesideThePolicy()
    {
        using var json = JsonDocument.Parse(Sealing.ServeOptionsJson(new Open(), ServedConfidential.Required));
        Assert.Equal("open", json.RootElement.GetProperty("policy").GetProperty("kind").GetString());
        Assert.Equal("required", json.RootElement.GetProperty("confidential").GetString());
    }

    [Fact]
    public void APolicyLeftOutIsRefusedBeforeTheLibrary()
    {
        // ArgumentNullException, not the library's ArgumentException: a null policy never reaches libmacula,
        // so no handle is made and no station hears of the procedure.
        Assert.Throws<ArgumentNullException>(() => Sealing.ServeOptionsJson(null!, null));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("")]
    [InlineData(null)]
    public void TheLibraryRefusesAServeThatNamesNoPolicy(string? options)
    {
        var message = RefusalThrough(options, stream: false);
        Assert.Contains("policy is required", message);
        Assert.Contains("""{"policy":{"kind":"open"}}""", message);
        // The door that serves anyone, which is the fix the refusal teaches.
        Assert.Contains("macula_pool_serve_gated", message);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("")]
    [InlineData(null)]
    public void TheLibraryRefusesAStreamServeThatNamesNoPolicy(string? options)
    {
        var message = RefusalThrough(options, stream: true);
        Assert.Contains("policy is required", message);
        Assert.Contains("""{"policy":{"kind":"open"}}""", message);
        // A stream's own gated door, not the call's: the two refusals teach one fix each.
        Assert.Contains("macula_pool_serve_stream_gated", message);
        Assert.DoesNotContain("macula_pool_serve_gated", message);
    }

    // The options are judged before the pool is looked up: with a handle of 0 - no pool this process ever
    // opened - a refusal that came after the lookup would be invalid_handle, an ObjectDisposedException here,
    // rather than the options' own invalid_argument.
    private static unsafe string RefusalThrough(string? options, bool stream)
    {
        NativeCall.EnsureLoaded();
        byte[] realm = new byte[32];
        nint err = 0;
        fixed (byte* r = realm)
        {
            using var pool = new PoolHandle();
            if (stream)
            {
                using var served = Libmacula.macula_pool_serve_stream_opts(pool, r, "policy_test",
                    (int)StreamMode.Server, options, ref err);
                Assert.True(served.IsInvalid, "a refused stream serve returned a handle");
            }
            else
            {
                using var served = Libmacula.macula_pool_serve_opts(pool, r, "policy_test", options, ref err);
                Assert.True(served.IsInvalid, "a refused serve returned a handle");
            }
        }
        return Assert.Throws<ArgumentException>(() => NativeCall.Check(err)).Message;
    }
}

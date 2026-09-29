using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Macula.Tests;

// Sealing (macula 13's E2E seal scheme 1, macula-go v0.18.0) and the caller's seal report (macula's
// DESIGN_E2E_SEAL_REPORT, macula-go v0.19.0) through the .NET API, against two in-process stations: a
// provider that names its KEM key is called sealed, one that names none in the clear, what cannot be kept
// confidential fails as a ConfidentialityException, and a caller learns whether the exchange behind its
// result was sealed, to which provider and key.
[Collection(StationsCollection.Name)]
public sealed partial class SealingTests(TestStations stations)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    [GeneratedRegex("^[0-9a-f]{16}$")]
    private static partial Regex KeyId();

    private static CallOptions Sealed(Confidential confidential = Confidential.Preferred, MeshId? provider = null,
        UcanPresentation? ucan = null) =>
        new() { Timeout = Patience, Confidential = confidential, Provider = provider, Ucan = ucan };

    private static ValueTask<JsonNode?> SealedFlag(Request r, CancellationToken _) =>
        ValueTask.FromResult<JsonNode?>(new JsonObject { ["sealed"] = r.Sealed ? 1 : 0 });

    private static Task Nothing(MeshStream _, CancellationToken __) => Task.CompletedTask;

    [Fact]
    public async Task KemAdvertiseIsOffByDefaultAndServingRequiredNeedsIt()
    {
        Assert.False(new PoolOptions().KemAdvertise);
        await using var provider = await stations.JoinAsync("sealing no kem");
        var refused = Assert.Throws<ConfidentialityException>(() =>
            provider.Serve(stations.Realm, provider.OwnProcedure("x"), SealedFlag, confidential: ServedConfidential.Required));
        Assert.Equal("kem_advertise_disabled", refused.Reason);
        refused = Assert.Throws<ConfidentialityException>(() =>
            provider.ServeStream(stations.Realm, provider.OwnProcedure("y"), StreamMode.Server, Nothing,
                confidential: ServedConfidential.Required));
        Assert.Equal("kem_advertise_disabled", refused.Reason);
    }

    [Fact]
    public async Task AProviderThatNamesItsKeyIsCalledSealedByDefaultAndRequired()
    {
        await using var provider = await stations.JoinAsync("sealing keyed provider", kemAdvertise: true);
        await using var caller = await stations.JoinAsync("sealing keyed caller", station: 1);
        var procedure = provider.OwnProcedure("sealed_echo");
        await using var served = provider.Serve(stations.Realm, procedure, (r, _) =>
            ValueTask.FromResult<JsonNode?>(new JsonObject { ["echoed"] = r.Payload?.DeepClone(), ["sealed"] = r.Sealed ? 1 : 0 }),
            confidential: ServedConfidential.Required);
        var required = await caller.CallAsync(stations.Realm, procedure, new JsonObject { ["word"] = "hush" },
            Sealed(Confidential.Required));
        Assert.Equal("hush", required!["echoed"]!["word"]!.GetValue<string>());
        Assert.Equal(1, required["sealed"]!.GetValue<int>());
        var byDefault = await caller.CallAsync(stations.Realm, procedure, new JsonObject { ["word"] = "again" },
            new CallOptions { Timeout = Patience });
        Assert.Equal(1, byDefault!["sealed"]!.GetValue<int>());
    }

    [Fact]
    public async Task APinnedProviderIsCalledSealedAndNoOther()
    {
        await using var provider = await stations.JoinAsync("sealing pinned provider", kemAdvertise: true);
        await using var other = await stations.JoinAsync("sealing other provider", kemAdvertise: true);
        await using var caller = await stations.JoinAsync("sealing pinning caller", station: 1);
        var procedure = provider.OwnProcedure("pinned");
        await using var served = provider.Serve(stations.Realm, procedure,
            (_, _) => ValueTask.FromResult<JsonNode?>(provider.NodeId.ToString()));
        var result = await caller.CallAsync(stations.Realm, procedure, null, Sealed(Confidential.Required, provider.NodeId));
        Assert.Equal(provider.NodeId.ToString(), result!.GetValue<string>());
        var nobody = await Assert.ThrowsAsync<MaculaException>(() =>
            caller.CallAsync(stations.Realm, procedure, null, Sealed(provider: other.NodeId)));
        Assert.Equal(ErrorKind.NoProvider, nobody.Kind);
    }

    [Fact]
    public async Task AProviderThatNamesNoKeyIsCalledClearAndRequiredRefusesIt()
    {
        await using var provider = await stations.JoinAsync("sealing keyless provider");
        await using var caller = await stations.JoinAsync("sealing keyless caller", station: 1);
        var procedure = provider.OwnProcedure("clear_echo");
        await using var served = provider.Serve(stations.Realm, procedure, SealedFlag);
        var clear = await caller.CallAsync(stations.Realm, procedure, null, new CallOptions { Timeout = Patience });
        Assert.Equal(0, clear!["sealed"]!.GetValue<int>());
        var refused = await Assert.ThrowsAsync<ConfidentialityException>(() =>
            caller.CallAsync(stations.Realm, procedure, null, Sealed(Confidential.Required)));
        Assert.Equal(("no_kem_key", (string?)null, (string?)null), (refused.Reason, refused.Named, refused.Found));
        Assert.Equal(ErrorKind.Confidentiality, refused.Kind);
    }

    [Fact]
    public async Task AConfidentialityOutsideItsEnumIsRefusedBeforeSending()
    {
        await using var caller = await stations.JoinAsync("sealing odd caller");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            caller.CallAsync(stations.Realm, caller.OwnProcedure("x"), null, Sealed((Confidential)7)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            caller.OpenStreamAsync(stations.Realm, caller.OwnProcedure("x"), StreamMode.Server, confidential: (Confidential)7));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            caller.Serve(stations.Realm, caller.OwnProcedure("y"), SealedFlag, confidential: (ServedConfidential)7));
    }

    [Fact]
    public async Task OffServesClearEvenFromANodeThatNamesItsKey()
    {
        await using var provider = await stations.JoinAsync("sealing off provider", kemAdvertise: true);
        await using var caller = await stations.JoinAsync("sealing off caller", station: 1);
        var procedure = provider.OwnProcedure("off_echo");
        await using var served = provider.Serve(stations.Realm, procedure, SealedFlag, confidential: ServedConfidential.Off);
        var result = await caller.CallAsync(stations.Realm, procedure, null, new CallOptions { Timeout = Patience });
        Assert.Equal(0, result!["sealed"]!.GetValue<int>());
    }

    [Fact]
    public async Task AGatedProcedureIsCalledSealedWithItsUcan()
    {
        // The presentation and the confidentiality travel in one options set.
        await using var provider = await stations.JoinAsync("sealing gated provider", kemAdvertise: true);
        await stations.AdmitAsync(provider.NodeId);
        await using var caller = await stations.JoinAsync("sealing gated caller", station: 1);
        using var root = await NodeKey.GenerateAsync(stations.Profile);
        var procedure = $"{stations.Org}/sealed_gated";
        var token = root.CreateUcan(caller.NodeId,
            [new Capability($"mri:org:{stations.RealmName}/{stations.Org}", "invoke")], DateTimeOffset.UtcNow.AddMinutes(5));
        await using var served = provider.Serve(stations.Realm, procedure, SealedFlag, new UcanRequired(root.NodeId),
            ServedConfidential.Required);
        var reported = await UntilServed(() => caller.CallReportAsync(stations.Realm, procedure, null,
            Sealed(Confidential.Required, ucan: new UcanPresentation(token))));
        Assert.Equal(1, reported.Result!["sealed"]!.GetValue<int>());
        Assert.True(reported.Report.Sealed);
    }

    [Fact]
    public async Task AStreamOpensSealedToAProviderThatNamesItsKeyAndItsSessionSaysSo()
    {
        await using var provider = await stations.JoinAsync("sealing stream provider", kemAdvertise: true);
        await using var caller = await stations.JoinAsync("sealing stream caller", station: 1);
        var procedure = provider.OwnProcedure("sealed_count");
        await using var served = provider.ServeStream(stations.Realm, procedure, StreamMode.Client, async (s, ct) =>
        {
            var total = 0;
            await foreach (var frame in s.ReadAllAsync(ct))
            {
                if (frame is StreamData { Body: var body } && Payload.TryGetBytes(body, out var bytes))
                {
                    total += bytes.Length;
                }
                if (frame is StreamEnd)
                {
                    break;
                }
            }
            s.Reply(new JsonObject { ["total"] = total, ["sealed"] = s.Request.Sealed ? 1 : 0 });
        }, confidential: ServedConfidential.Required);
        await using var stream = await caller.OpenStreamAsync(stations.Realm, procedure, StreamMode.Client,
            timeout: Patience, confidential: Confidential.Required);
        stream.Send("ab"u8);
        stream.Send("cde"u8);
        stream.CloseSend();
        using var wait = new CancellationTokenSource(Patience);
        await foreach (var frame in stream.ReadAllAsync(wait.Token))
        {
            var reply = Assert.IsType<StreamReply>(frame);
            Assert.Equal(5, reply.Payload!["total"]!.GetValue<int>());
            Assert.Equal(1, reply.Payload["sealed"]!.GetValue<int>());
            break;
        }
    }

    [Fact]
    public async Task RequiredWillNotOpenAStreamToAProviderThatNamesNoKey()
    {
        await using var provider = await stations.JoinAsync("sealing keyless stream provider");
        await using var caller = await stations.JoinAsync("sealing keyless stream caller", station: 1);
        var procedure = provider.OwnProcedure("clear_watch");
        await using var served = provider.ServeStream(stations.Realm, procedure, StreamMode.Server, Nothing);
        var refused = await Assert.ThrowsAsync<ConfidentialityException>(() =>
            caller.OpenStreamAsync(stations.Realm, procedure, StreamMode.Server, timeout: Patience,
                confidential: Confidential.Required));
        Assert.Equal("no_kem_key", refused.Reason);
    }

    [Fact]
    public async Task ACallToAProviderThatNamesItsKeyReportsSealedToThatProviderAndKey()
    {
        await using var provider = await stations.JoinAsync("report keyed provider", kemAdvertise: true);
        await using var caller = await stations.JoinAsync("report keyed caller", station: 1);
        var procedure = provider.OwnProcedure("reported");
        await using var served = provider.Serve(stations.Realm, procedure,
            (r, _) => ValueTask.FromResult<JsonNode?>(new JsonObject { ["echoed"] = r.Payload?.DeepClone() }));
        var (result, report) = await caller.CallReportAsync(stations.Realm, procedure, new JsonObject { ["word"] = "hush" },
            new CallOptions { Timeout = Patience });
        Assert.Equal("hush", result!["echoed"]!["word"]!.GetValue<string>());
        Assert.True(report.Sealed);
        Assert.Equal(provider.NodeId, report.Provider);
        Assert.Matches(KeyId(), report.SealKeyId!);
    }

    [Fact]
    public async Task APinnedCallReportsTheProviderPinned()
    {
        await using var provider = await stations.JoinAsync("report pinned provider", kemAdvertise: true);
        await using var other = await stations.JoinAsync("report other provider", kemAdvertise: true);
        await using var caller = await stations.JoinAsync("report pinning caller", station: 1);
        var procedure = provider.OwnProcedure("reported_pinned");
        await using var served = provider.Serve(stations.Realm, procedure, (_, _) => ValueTask.FromResult<JsonNode?>(1));
        var (result, report) = await caller.CallReportAsync(stations.Realm, procedure, null,
            Sealed(Confidential.Required, provider.NodeId));
        Assert.Equal(1, result!.GetValue<int>());
        Assert.Equal((true, provider.NodeId), (report.Sealed, report.Provider));
        var nobody = await Assert.ThrowsAsync<MaculaException>(() =>
            caller.CallReportAsync(stations.Realm, procedure, null, Sealed(provider: other.NodeId)));
        Assert.Equal(ErrorKind.NoProvider, nobody.Kind);
    }

    [Fact]
    public async Task ACallToAProviderThatNamesNoKeyReportsClearWithNoKeyId()
    {
        await using var provider = await stations.JoinAsync("report keyless provider");
        await using var caller = await stations.JoinAsync("report keyless caller", station: 1);
        var procedure = provider.OwnProcedure("reported_clear");
        await using var served = provider.Serve(stations.Realm, procedure, (_, _) => ValueTask.FromResult<JsonNode?>(1));
        var reported = await caller.CallReportAsync(stations.Realm, procedure, null, new CallOptions { Timeout = Patience });
        Assert.Equal(1, reported.Result!.GetValue<int>());
        Assert.Equal(new SealReport(false, provider.NodeId, null), reported.Report);
    }

    [Fact]
    public async Task AStreamsReportSettlesOnTheFirstChunkStaysAfterTheEndAndTheProviderHasNone()
    {
        await using var provider = await stations.JoinAsync("report stream provider", kemAdvertise: true);
        await using var caller = await stations.JoinAsync("report stream caller", station: 1);
        var procedure = provider.OwnProcedure("reported_watch");
        Exception? providerSide = null;
        // The provider holds its chunk until the caller has seen the report unsettled, so the order is by
        // construction, not by a timer.
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var served = provider.ServeStream(stations.Realm, procedure, StreamMode.Server, async (s, _) =>
        {
            providerSide = Record.Exception(() => s.Report());
            await released.Task;
            s.Send("one"u8);
        }, confidential: ServedConfidential.Required);
        await using var stream = await caller.OpenStreamAsync(stations.Realm, procedure, StreamMode.Server,
            timeout: Patience, confidential: Confidential.Required);
        var early = Assert.Throws<MaculaException>(() => stream.Report());
        Assert.Equal(ErrorKind.NotSettled, early.Kind);
        released.SetResult();
        using var wait = new CancellationTokenSource(Patience);
        SealReport? settled = null;
        var ended = false;
        await foreach (var frame in stream.ReadAllAsync(wait.Token))
        {
            if (frame is StreamData)
            {
                settled = stream.Report();
            }
            if (frame is StreamEnd or StreamEof)
            {
                ended = true;
                break;
            }
        }
        Assert.True(ended);
        Assert.NotNull(settled);
        Assert.True(settled.Sealed);
        Assert.Equal(provider.NodeId, settled.Provider);
        Assert.Matches(KeyId(), settled.SealKeyId!);
        Assert.Equal(settled, stream.Report());
        var notACaller = Assert.IsType<MaculaException>(providerSide);
        Assert.Equal(ErrorKind.NotACaller, notACaller.Kind);
    }

    [Fact]
    public async Task ASealedStreamTheProviderEndsBeforeAnyChunkHasNoReport()
    {
        await using var provider = await stations.JoinAsync("report ending provider", kemAdvertise: true);
        await using var caller = await stations.JoinAsync("report ending caller", station: 1);
        var procedure = provider.OwnProcedure("ended_unsettled");
        await using var served = provider.ServeStream(stations.Realm, procedure, StreamMode.Server, Nothing,
            confidential: ServedConfidential.Required);
        await using var stream = await caller.OpenStreamAsync(stations.Realm, procedure, StreamMode.Server,
            timeout: Patience, confidential: Confidential.Required);
        using var wait = new CancellationTokenSource(Patience);
        await foreach (var frame in stream.ReadAllAsync(wait.Token))
        {
            Assert.IsType<StreamEnd>(frame);
            break;
        }
        var none = Assert.Throws<MaculaException>(() => stream.Report());
        Assert.Equal(ErrorKind.NotSettled, none.Kind);
        await stream.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => stream.Report());
    }

    // attempt until the provider, not the DHT's reach, answers.
    private static async Task<T> UntilServed<T>(Func<Task<T>> attempt)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (true)
        {
            try
            {
                return await attempt();
            }
            catch (Exception e) when (DateTime.UtcNow < deadline && e is not ProviderErrorException { Code: not "unknown_next_peer" }
                                      and not ConfidentialityException)
            {
                await Task.Delay(200);
            }
        }
    }
}

// The error JSON libmacula reports (cabi/CONTRACT.md "Errors"), as the exception its kind names.
public sealed class SealingErrorTests
{
    [Fact]
    public void ConfidentialityCarriesItsReasonAndBothKeyIds()
    {
        var e = Assert.IsType<ConfidentialityException>(Errors.FromJson(
            """{"kind":"confidentiality","message":"m","reason":"key_mismatch","named":"0102030405060708","found":"1112131415161718"}""",
            default));
        Assert.Equal(ErrorKind.Confidentiality, e.Kind);
        Assert.Equal(("key_mismatch", "0102030405060708", "1112131415161718"), (e.Reason, e.Named, e.Found));
    }

    [Fact]
    public void ConfidentialityKeyIdsMayBeNullOrAbsent()
    {
        var e = Assert.IsType<ConfidentialityException>(Errors.FromJson(
            """{"kind":"confidentiality","message":"m","reason":"no_kem_key","named":null}""", default));
        Assert.Equal(("no_kem_key", (string?)null, (string?)null), (e.Reason, e.Named, e.Found));
    }

    [Theory]
    [InlineData("not_settled", ErrorKind.NotSettled)]
    [InlineData("not_a_caller", ErrorKind.NotACaller)]
    public void TheSealReportsKindsAreTheirOwn(string kind, ErrorKind expected)
    {
        var e = Assert.IsType<MaculaException>(Errors.FromJson($$"""{"kind":"{{kind}}","message":"m"}""", default));
        Assert.Equal(expected, e.Kind);
    }
}

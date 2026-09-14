using System.Runtime.Versioning;
using System.Security.Cryptography;
using Macula.Connection;
using Macula.Frame;
using Macula.Identity;
using Macula.Ucan;

namespace Macula.Tests;

/// <summary>
/// The provider side of an inbound CALL, with no network: a gated policy
/// accepts a token only from the caller it was minted for, and a handler
/// receives the verified caller in a map payload. The cases and
/// names match macula-go's connection/serve_caller_test.go. That a CALL
/// reaches serving only when its signature verifies against the caller it
/// names is checked by the session's reader; see SessionReaderTests.
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public class ServeCallerTests
{
    private static readonly byte[] Realm = new byte[32];
    private const string Procedure = "macula_dotnet_sdk.serve_caller_test.echo";

    [Fact]
    public async Task Build_call_reply_gated_policy_refuses_a_token_presented_by_another_caller()
    {
        var issuer = KeyPair.Generate();
        var audience = KeyPair.Generate();
        var presenter = KeyPair.Generate();
        var handler = new CountingHandler();
        var token = Token(issuer, Convert.ToHexStringLower(audience.NodeId()));

        var reply = await Session.BuildCallReplyAsync(null, Info(presenter.NodeId(), token), handler.Lookup, RequiredBy(issuer), KeyPair.Generate());

        AssertUnauthorized(reply);
        Assert.Equal(0, handler.Invocations);
    }

    [Fact]
    public async Task Build_call_reply_gated_policy_accepts_a_token_from_its_audience()
    {
        var issuer = KeyPair.Generate();
        var caller = KeyPair.Generate();
        var handler = new CountingHandler();
        var token = Token(issuer, Convert.ToHexStringLower(caller.NodeId()));

        var reply = await Session.BuildCallReplyAsync(null, Info(caller.NodeId(), token), handler.Lookup, RequiredBy(issuer), KeyPair.Generate());

        Assert.IsType<CallResponse.Result>(CallFrameParsing.ParseCallResponse(reply));
        Assert.Equal(1, handler.Invocations);
    }

    [Fact]
    public async Task Build_call_reply_gated_policy_refuses_a_token_without_audience()
    {
        var issuer = KeyPair.Generate();
        var caller = KeyPair.Generate();
        var handler = new CountingHandler();

        var reply = await Session.BuildCallReplyAsync(null, Info(caller.NodeId(), Token(issuer, "")), handler.Lookup, RequiredBy(issuer), KeyPair.Generate());

        AssertUnauthorized(reply);
        Assert.Equal(0, handler.Invocations);
    }

    [Fact]
    public async Task Build_call_reply_gated_policy_refuses_a_call_without_caller()
    {
        var issuer = KeyPair.Generate();
        var someone = KeyPair.Generate();
        var handler = new CountingHandler();
        var token = Token(issuer, Convert.ToHexStringLower(someone.NodeId()));

        var reply = await Session.BuildCallReplyAsync(null, Info(Array.Empty<byte>(), token), handler.Lookup, RequiredBy(issuer), KeyPair.Generate());

        AssertUnauthorized(reply);
        Assert.Equal(0, handler.Invocations);
    }

    // A handler receives the verified caller in a map payload. The names
    // match macula-go's.

    [Fact]
    public async Task An_inbound_call_threads_its_caller_into_the_payload()
    {
        var caller = KeyPair.Generate().NodeId();
        var payload = Value.Map([new(Value.Text("n"), Value.Int(21))]);

        var seen = Assert.IsType<Value.MapValue>(await PayloadSeenByTheHandler(payload, caller));

        Assert.Equal(caller, Assert.IsType<Value.BytesValue>(seen.Get("caller")).Value);
        Assert.Equal(Value.Int(21), seen.Get("n"));
    }

    [Fact]
    public async Task A_caller_the_sender_put_in_the_payload_is_replaced_by_the_verified_caller()
    {
        var caller = KeyPair.Generate().NodeId();
        var claimed = KeyPair.Generate().NodeId();
        var payload = Value.Map([
            new(Value.Text("caller"), Value.Bytes(claimed)),
            new(Value.Bytes("caller"u8.ToArray()), Value.Bytes(claimed)),
        ]);

        var seen = Assert.IsType<Value.MapValue>(await PayloadSeenByTheHandler(payload, caller));

        var entry = Assert.Single(seen.Entries);
        Assert.Equal("caller", Assert.IsType<Value.TextValue>(entry.Key).AsText());
        Assert.Equal(caller, Assert.IsType<Value.BytesValue>(entry.Value).Value);
    }

    [Fact]
    public async Task A_non_map_payload_carries_no_caller()
    {
        var seen = await PayloadSeenByTheHandler(Value.Text("hello"), KeyPair.Generate().NodeId());

        Assert.Equal("hello", Assert.IsType<Value.TextValue>(seen).AsText());
    }

    private static async Task<Value> PayloadSeenByTheHandler(Value payload, byte[] caller)
    {
        Value? seen = null;
        CallLookup lookup = (_, _) => received =>
        {
            seen = received;
            return Task.FromResult(received);
        };
        var info = new CallInfo(RandomNumberGenerator.GetBytes(16), Procedure, Realm, payload, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 10_000, caller, Array.Empty<byte>());

        var reply = await Session.BuildCallReplyAsync(null, info, lookup, (_, _) => Policy.Open, KeyPair.Generate());

        Assert.IsType<CallResponse.Result>(CallFrameParsing.ParseCallResponse(reply));
        return Assert.IsAssignableFrom<Value>(seen);
    }

    private static CallInfo Info(byte[] caller, byte[] token) =>
        new(RandomNumberGenerator.GetBytes(16), Procedure, Realm, Value.Text("hello"), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 10_000, caller, token);

    private static byte[] Token(KeyPair issuer, string audience) =>
        UcanToken.Create("did:macula:test-issuer", audience, Array.Empty<UcanToken.Capability>(), issuer);

    private static PolicyLookup RequiredBy(KeyPair issuer) => (_, _) => Policy.Required(issuer.PublicBytes());

    private static void AssertUnauthorized(Value.MapValue reply) =>
        Assert.Equal("unauthorized", Assert.IsType<CallResponse.Error>(CallFrameParsing.ParseCallResponse(reply)).Name);

    /// <summary>An echo handler for Procedure that counts how often it ran.</summary>
    private sealed class CountingHandler
    {
        private int _invocations;

        public int Invocations => _invocations;

        public CallLookup Lookup => (_, procedure) => procedure != Procedure
            ? null
            : payload =>
            {
                Interlocked.Increment(ref _invocations);
                return Task.FromResult(payload);
            };
    }
}

using System.Runtime.Versioning;
using System.Security.Cryptography;
using Macula.Connection;
using Macula.Frame;
using Macula.Identity;
using Macula.Ucan;

namespace Macula.Tests;

/// <summary>
/// The provider side of an inbound CALL, with no network: a CALL reaches
/// policy and a handler only when its signature verifies against the caller
/// it names, and a gated policy accepts a token only from the caller it was
/// minted for. The cases and names match macula-go's
/// connection/serve_caller_test.go; the behaviour matches
/// macula_station_link.erl's on_inbound_call/3.
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public class ServeCallerTests
{
    private static readonly byte[] Realm = new byte[32];
    private const string Procedure = "macula_dotnet_sdk.serve_caller_test.echo";

    [Fact]
    public async Task Reply_to_frame_ignores_a_call_not_signed_by_its_caller()
    {
        var named = KeyPair.Generate();
        var signer = KeyPair.Generate();
        var handler = new CountingHandler();

        var reply = await Session.ReplyToFrameAsync(null, Envelope.Sign(Call(named), signer), handler.Lookup, OpenPolicy, KeyPair.Generate());

        Assert.Null(reply);
        Assert.Equal(0, handler.Invocations);
    }

    [Fact]
    public async Task Reply_to_frame_ignores_an_unsigned_call()
    {
        var handler = new CountingHandler();

        var reply = await Session.ReplyToFrameAsync(null, Call(KeyPair.Generate()), handler.Lookup, OpenPolicy, KeyPair.Generate());

        Assert.Null(reply);
        Assert.Equal(0, handler.Invocations);
    }

    [Fact]
    public async Task Reply_to_frame_answers_a_call_signed_by_its_caller()
    {
        var caller = KeyPair.Generate();
        var handler = new CountingHandler();

        var reply = await Session.ReplyToFrameAsync(null, Envelope.Sign(Call(caller), caller), handler.Lookup, OpenPolicy, KeyPair.Generate());

        Assert.NotNull(reply);
        var result = Assert.IsType<CallResponse.Result>(CallFrameParsing.ParseCallResponse(reply));
        Assert.Equal("hello", result.Payload.AsText());
        Assert.Equal(1, handler.Invocations);
    }

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

    private static Value.MapValue Call(KeyPair namedCaller) => CallFrame.Build(new CallSpec
    {
        CallId = RandomNumberGenerator.GetBytes(16),
        Procedure = Procedure,
        Realm = Realm,
        Payload = Value.Text("hello"),
        DeadlineMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 10_000,
        Caller = namedCaller.NodeId(),
    });

    private static CallInfo Info(byte[] caller, byte[] token) =>
        new(RandomNumberGenerator.GetBytes(16), Procedure, Realm, Value.Text("hello"), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 10_000, caller, token);

    private static byte[] Token(KeyPair issuer, string audience) =>
        UcanToken.Create("did:macula:test-issuer", audience, Array.Empty<UcanToken.Capability>(), issuer);

    private static Policy OpenPolicy(byte[] realm, string procedure) => Policy.Open;

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

using Macula.Frame;
using Macula.Identity;

namespace Macula.Connection;

/// <summary>
/// RPC telemetry auto-facts, matching macula_request.erl (caller side:
/// rpc.sent_v1/rpc.completed_v1) and macula_response.erl (provider side:
/// rpc.received_v1/rpc.replied_v1) exactly -- same topic names, same
/// request_id field (a fresh 16 random bytes per call, independent of the
/// wire CALL frame's own CallId), same realm as the call itself. These build
/// each fact; a session hands it to its own writer
/// (<see cref="Session.Announce"/>), so a fact never costs the call or serve
/// it describes any time and never fails it, matching macula_response.erl's
/// own `_ = macula:publish(...), ok` and macula_request.erl's identical
/// publish/5 helper. A fact is dropped when 64 frames already wait for that
/// writer.
///
/// Always on, matching the reference's own actual reachable behavior:
/// `Announce` is a config field in both Erlang modules, but every reachable
/// public entry point hardcodes it to true -- there is no way to turn it
/// off in practice on the reference side, so this doesn't expose an option
/// nothing would ever set to false either. Ported from macula-go's
/// connection/rpc_facts.go.
/// </summary>
internal static class RpcFacts
{
    private const string RpcSentTopic = "rpc.sent_v1";
    private const string RpcCompletedTopic = "rpc.completed_v1";
    private const string RpcReceivedTopic = "rpc.received_v1";
    private const string RpcRepliedTopic = "rpc.replied_v1";

    public static PublishSpec Sent(byte[] realm, KeyPair identity, byte[] requestId) =>
        Fact(realm, identity, RpcSentTopic, RequestIdFields(requestId));

    /// <summary>Matches macula_request.erl's outcome_fields/2: completed (no exception, not a bolt4 ERROR frame) or failed (either).</summary>
    public static PublishSpec Completed(byte[] realm, KeyPair identity, byte[] requestId, CallResponse? resp, Exception? err)
    {
        var fields = RequestIdFields(requestId);
        if (err is not null)
        {
            fields.AddRange(OutcomeFailed(err.Message));
        }
        else if (resp is CallResponse.Error e)
        {
            fields.AddRange(OutcomeFailed(e.Name));
        }
        else
        {
            fields.Add(new KeyValuePair<Value, Value>(Value.Text("outcome"), Value.Text("completed")));
        }
        return Fact(realm, identity, RpcCompletedTopic, fields);
    }

    public static PublishSpec Received(byte[] realm, KeyPair identity, byte[] requestId) =>
        Fact(realm, identity, RpcReceivedTopic, RequestIdFields(requestId));

    /// <summary>
    /// Matches macula_response.erl's outcome_fields/2: replied (success) or
    /// failed (a CallHandlerException). A handler CRASH is deliberately
    /// NOT announced here at all -- matching the reference exactly, where
    /// a crashing handle_request/2 crashes the whole per-request child
    /// before its own publish_replied/2 call is ever reached, so
    /// rpc.replied_v1 is never published for a crash either.
    /// </summary>
    public static PublishSpec Replied(byte[] realm, KeyPair identity, byte[] requestId, string? handlerErrorMessage)
    {
        var fields = RequestIdFields(requestId);
        if (handlerErrorMessage is not null)
        {
            fields.AddRange(OutcomeFailed(handlerErrorMessage));
        }
        else
        {
            fields.Add(new KeyValuePair<Value, Value>(Value.Text("outcome"), Value.Text("replied")));
        }
        return Fact(realm, identity, RpcRepliedTopic, fields);
    }

    public static byte[] RandomRequestId()
    {
        var b = new byte[16];
        System.Security.Cryptography.RandomNumberGenerator.Fill(b);
        return b;
    }

    private static List<KeyValuePair<Value, Value>> RequestIdFields(byte[] requestId) =>
        new() { new KeyValuePair<Value, Value>(Value.Text("request_id"), Value.Bytes(requestId)) };

    private static List<KeyValuePair<Value, Value>> OutcomeFailed(string reason) => new()
    {
        new KeyValuePair<Value, Value>(Value.Text("outcome"), Value.Text("failed")),
        new KeyValuePair<Value, Value>(Value.Text("reason"), Value.Text(reason)),
    };

    private static PublishSpec Fact(byte[] realm, KeyPair identity, string topic, List<KeyValuePair<Value, Value>> fields) => new()
    {
        Topic = topic,
        Realm = realm,
        Publisher = identity.NodeId(),
        Seq = (ulong)Random.Shared.NextInt64(),
        Payload = Value.Map(fields),
        PublishedAtMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    };
}

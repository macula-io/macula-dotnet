using System.Text;
using Macula.Bolt4;

namespace Macula.Frame;

/// <summary>
/// Fields for a CALL frame.
///
/// Overriding a base-envelope sentinel field (realm, call_id,
/// source_route -- all Null by default from Envelope.Base) MUST use
/// WithField, never a raw list append: Value.MapValue is a plain ordered
/// list of pairs, not a real map, with none of Erlang's automatic
/// key-uniqueness.
/// </summary>
public sealed class CallSpec
{
    public required byte[] CallId { get; init; }
    public required string Procedure { get; init; }
    public required byte[] Realm { get; init; }
    public required Value Payload { get; init; }
    public required long DeadlineMs { get; init; }
    public required byte[] Caller { get; init; }

    /// <summary>Opaque source-route header bytes -- empty for a direct call to one known station.</summary>
    public byte[] SourceRoute { get; init; } = Array.Empty<byte>();
    public ulong RetryBudget { get; init; }
    public byte[] UcanToken { get; init; } = Array.Empty<byte>();
}

public static class CallFrame
{
    public static Value.MapValue Build(CallSpec spec, byte[] frameId, ulong sentAtMs) =>
        Envelope.Base("call", 0, frameId, sentAtMs)
            .WithField("realm", Value.Bytes(spec.Realm))
            .WithField("call_id", Value.Bytes(spec.CallId))
            // `procedure := binary()` in the Erlang spec -- a raw byte
            // string (major 2), not text (major 3).
            .WithField("procedure", Value.Bytes(Encoding.UTF8.GetBytes(spec.Procedure)))
            .WithField("payload", spec.Payload)
            .WithField("deadline_ms", Value.Int(spec.DeadlineMs))
            .WithField("caller", Value.Bytes(spec.Caller))
            .WithField("source_route", Value.Bytes(spec.SourceRoute))
            .WithField("retry_budget", Value.UInt(spec.RetryBudget))
            .WithField("ucan_token", Value.Bytes(spec.UcanToken));

    public static Value.MapValue Build(CallSpec spec) =>
        Build(spec, Envelope.FreshFrameId(), Envelope.CurrentMillis());
}

public sealed class ResultSpec
{
    public required byte[] CallId { get; init; }
    public required Value Payload { get; init; }
    public required byte[] RespondedBy { get; init; }
    public byte[] SourceRouteReverse { get; init; } = Array.Empty<byte>();
}

public static class ResultFrame
{
    public static Value.MapValue Build(ResultSpec spec, byte[] frameId, ulong sentAtMs) =>
        // RESULT does not touch the base envelope's `realm` or
        // `source_route` fields -- they stay Null.
        Envelope.Base("result", 0, frameId, sentAtMs)
            .WithField("call_id", Value.Bytes(spec.CallId))
            .WithField("payload", spec.Payload)
            .WithField("responded_by", Value.Bytes(spec.RespondedBy))
            .WithField("source_route_reverse", Value.Bytes(spec.SourceRouteReverse));

    public static Value.MapValue Build(ResultSpec spec) =>
        Build(spec, Envelope.FreshFrameId(), Envelope.CurrentMillis());
}

/// <summary>Fields for an ERROR frame. `name` is derived from `code` automatically.</summary>
public sealed class CallErrorSpec
{
    public required byte[] CallId { get; init; }
    public required Bolt4Code Code { get; init; }
    public required byte[] ReportedBy { get; init; }
    public string? Detail { get; init; }
    public byte[]? OffendingHop { get; init; }
    public byte[] SourceRoutePartial { get; init; } = Array.Empty<byte>();
}

public static class CallErrorFrame
{
    public static Value.MapValue Build(CallErrorSpec spec, byte[] frameId, ulong sentAtMs) =>
        Envelope.Base("error", 0, frameId, sentAtMs)
            .WithField("call_id", Value.Bytes(spec.CallId))
            .WithField("code", Value.UInt(spec.Code.Value))
            .WithField("name", Value.Text(spec.Code.Name))
            .WithField("reported_by", Value.Bytes(spec.ReportedBy))
            // `detail => binary() | undefined` -- bytes, not text.
            .WithField("detail", spec.Detail is null ? Value.Null : Value.Bytes(Encoding.UTF8.GetBytes(spec.Detail)))
            .WithField("offending_hop", spec.OffendingHop is null ? Value.Null : Value.Bytes(spec.OffendingHop))
            .WithField("source_route_partial", Value.Bytes(spec.SourceRoutePartial));

    public static Value.MapValue Build(CallErrorSpec spec) =>
        Build(spec, Envelope.FreshFrameId(), Envelope.CurrentMillis());
}

/// <summary>The fields a provider needs from an inbound CALL.</summary>
public sealed record CallInfo(byte[] CallId, string Procedure, byte[] Realm, Value Payload, long DeadlineMs, byte[] Caller, byte[] UcanToken);

public enum ParseFrameError
{
    WrongFrameType,
    MissingField,
    WrongFieldType,
}

public sealed class ParseFrameException : Exception
{
    public ParseFrameError Kind { get; }
    public string? Field { get; }

    public ParseFrameException(ParseFrameError kind, string? field = null)
        : base(field is null ? kind.ToString() : $"{kind}: {field}")
    {
        Kind = kind;
        Field = field;
    }
}

/// <summary>Parsed fields of a RESULT or ERROR response to a CALL, correlated by call_id.</summary>
public abstract record CallResponse
{
    private CallResponse() { }

    public sealed record Result(Value Payload, byte[] RespondedBy) : CallResponse;

    public sealed record Error(byte Code, string Name, byte[] ReportedBy, string? Detail) : CallResponse;
}

public static class CallFrameParsing
{
    private static string FrameType(Value.MapValue frame) =>
        frame.Get("frame_type") is Value.TextValue t ? t.AsText() : "";

    private static byte[] RequireBytes(Value.MapValue frame, string field, int? exactLength = null)
    {
        return frame.Get(field) switch
        {
            Value.BytesValue b when exactLength is null || b.Value.Length == exactLength => b.Value,
            null => throw new ParseFrameException(ParseFrameError.MissingField, field),
            _ => throw new ParseFrameException(ParseFrameError.WrongFieldType, field),
        };
    }

    private static string RequireProcedureLikeBytes(Value.MapValue frame, string field)
    {
        var bytes = RequireBytes(frame, field);
        try
        {
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception)
        {
            throw new ParseFrameException(ParseFrameError.WrongFieldType, field);
        }
    }

    /// <summary>Parse a decoded frame as a CALL -- the provider-side counterpart to <see cref="ParseCallResponse"/>.</summary>
    public static CallInfo ParseCall(Value frame)
    {
        if (frame is not Value.MapValue map || FrameType(map) != "call")
        {
            throw new ParseFrameException(ParseFrameError.WrongFrameType);
        }

        var callId = RequireBytes(map, "call_id", 16);
        // `procedure := binary()` on the wire -- bytes, not text.
        var procedure = RequireProcedureLikeBytes(map, "procedure");
        var realm = RequireBytes(map, "realm", 32);
        var payload = map.Get("payload") ?? throw new ParseFrameException(ParseFrameError.MissingField, "payload");
        var deadlineMs = map.Get("deadline_ms") switch
        {
            Value.UIntValue u => (long)u.Value,
            Value.NegIntValue n => -1L - (long)n.NMinusOne,
            null => throw new ParseFrameException(ParseFrameError.MissingField, "deadline_ms"),
            _ => throw new ParseFrameException(ParseFrameError.WrongFieldType, "deadline_ms"),
        };
        var caller = RequireBytes(map, "caller", 32);
        // Absent on a wire-level CALL from a peer that predates this field,
        // or simply an ungated call from a caller who never attached one --
        // both are the empty-token case a Policy check treats as "no token
        // presented", not a parse error.
        var ucanToken = map.Get("ucan_token") switch
        {
            null or Value.NullValue => Array.Empty<byte>(),
            Value.BytesValue b => b.Value,
            Value.TextValue t => t.Utf8,
            _ => throw new ParseFrameException(ParseFrameError.WrongFieldType, "ucan_token"),
        };

        return new CallInfo(callId, procedure, realm, payload, deadlineMs, caller, ucanToken);
    }

    /// <summary>
    /// Parses frame as a CALL and returns it only when its signature
    /// verifies against its own caller field; null for anything else.
    /// Mirrors macula_station_link.erl's on_inbound_call/3: a CALL that
    /// isn't signed by the caller it names never reaches policy or a
    /// handler, and gets no reply.
    /// </summary>
    internal static CallInfo? ParseSignedCall(Value frame)
    {
        CallInfo call;
        try
        {
            call = ParseCall(frame);
        }
        catch (ParseFrameException)
        {
            return null;
        }
        return Envelope.Verify((Value.MapValue)frame, call.Caller) is null ? call : null;
    }

    /// <summary>Extract this frame's call_id, regardless of frame type -- 16 bytes, never 32.</summary>
    public static byte[]? FrameCallId(Value frame) =>
        frame is Value.MapValue map && map.Get("call_id") is Value.BytesValue { Value.Length: 16 } b ? b.Value : null;

    /// <summary>Parse a decoded frame as a RESULT or ERROR response to a CALL.</summary>
    public static CallResponse ParseCallResponse(Value frame)
    {
        if (frame is not Value.MapValue map)
        {
            throw new ParseFrameException(ParseFrameError.WrongFrameType);
        }

        switch (FrameType(map))
        {
            case "result":
            {
                var payload = map.Get("payload") ?? throw new ParseFrameException(ParseFrameError.MissingField, "payload");
                var respondedBy = RequireBytes(map, "responded_by", 32);
                return new CallResponse.Result(payload, respondedBy);
            }
            case "error":
            {
                var code = map.Get("code") switch
                {
                    Value.UIntValue { Value: <= 255 } u => (byte)u.Value,
                    null => throw new ParseFrameException(ParseFrameError.MissingField, "code"),
                    _ => throw new ParseFrameException(ParseFrameError.WrongFieldType, "code"),
                };
                var name = map.Get("name") switch
                {
                    Value.TextValue t => t.AsText(),
                    null => throw new ParseFrameException(ParseFrameError.MissingField, "name"),
                    _ => throw new ParseFrameException(ParseFrameError.WrongFieldType, "name"),
                };
                var reportedBy = RequireBytes(map, "reported_by", 32);
                // `detail` is `binary() | undefined` on the wire (bytes), not text.
                string? detail = map.Get("detail") switch
                {
                    null or Value.NullValue => null,
                    Value.BytesValue b => Encoding.UTF8.GetString(b.Value),
                    _ => throw new ParseFrameException(ParseFrameError.WrongFieldType, "detail"),
                };
                return new CallResponse.Error(code, name, reportedBy, detail);
            }
            default:
                throw new ParseFrameException(ParseFrameError.WrongFrameType);
        }
    }
}

using System.Diagnostics;
using System.Text;
using Macula.Frame;

namespace Macula.Connection;

/// <summary>What a session dropped or refused.</summary>
internal enum DropKind
{
    RefusedStreamOpen,
    DroppedCall,
    DroppedReply,
}

/// <summary>Why a session dropped or refused it.</summary>
internal enum DropReason
{
    /// <summary>
    /// A well-formed signature that doesn't verify against the key the frame
    /// names as its signer: caller on a CALL or STREAM_OPEN, responded_by on a
    /// RESULT, reported_by on an ERROR.
    /// </summary>
    InvalidSignature,

    /// <summary>A signature that's missing or isn't 64 bytes, or a signer that isn't a 32-byte key.</summary>
    Unsigned,

    /// <summary>A frame that doesn't parse.</summary>
    Malformed,

    /// <summary>A dedicated stream whose first frame is of another type.</summary>
    NotAStreamOpen,

    /// <summary>A RESULT or ERROR for no pending call.</summary>
    UnknownCallId,
}

/// <summary>
/// The warnings a session traces when it drops an inbound frame or refuses a
/// stream, bounded per kind: the first drop in an interval is traced at once,
/// and the rest in that interval are counted into one closing line when it
/// ends, so a flood of bad frames can't flood the trace. The kinds, reasons
/// and fields are the same in every Macula stack.
/// </summary>
internal sealed class DropWarnings
{
    /// <summary>The interval a session starts with.</summary>
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(60);

    private const int ProcedureLimit = 256;

    private readonly string _station;
    private readonly Limiter[] _limiters = [new(), new(), new()];
    private long _intervalTicks = DefaultInterval.Ticks;

    internal DropWarnings(byte[] stationId)
    {
        _station = Convert.ToHexStringLower(stationId);
    }

    /// <summary>How long an interval lasts, for the intervals that start after a change.</summary>
    internal TimeSpan Interval
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _intervalTicks));
        set => Interlocked.Exchange(ref _intervalTicks, value.Ticks);
    }

    /// <summary>
    /// Records one drop of <paramref name="kind"/>, with the subject field
    /// from <see cref="ProcedureField(string)"/> or <see cref="CallIdField"/>,
    /// or an empty one. The first in an interval is traced at once; later ones
    /// are counted and reported in one line when the interval ends, and not at
    /// all when none came.
    /// </summary>
    internal void Record(DropKind kind, DropReason reason, string subjectField)
    {
        var limiter = _limiters[(int)kind];
        lock (limiter)
        {
            if (limiter.Open)
            {
                limiter.Later++;
                limiter.Latest = (reason, subjectField);
                return;
            }
            limiter.Open = true;
        }
        Trace(kind, 1, reason, subjectField);
        _ = CloseAsync(limiter, kind, Interval);
    }

    /// <summary>
    /// Why <paramref name="frame"/> isn't signed by the caller it names, or
    /// null when it is: the check macula runs on an inbound CALL or
    /// STREAM_OPEN before anything else looks at it.
    /// </summary>
    internal static DropReason? CallerCheck(Value.MapValue frame) => SignerCheck(frame, "caller");

    /// <summary>
    /// Why <paramref name="frame"/>, a reply, isn't signed by the key it names
    /// as its responder, or null when it is: reported_by on an ERROR, and
    /// responded_by on a RESULT or STREAM_REPLY. The check macula runs on a
    /// reply before anything else looks at it.
    /// </summary>
    internal static DropReason? ReplySignerCheck(Value.MapValue frame) =>
        SignerCheck(frame, frame.Get("frame_type") is Value.TextValue type && type.AsText() == "error" ? "reported_by" : "responded_by");

    // Why frame isn't signed by the key in its signerField, or null when it is.
    private static DropReason? SignerCheck(Value.MapValue frame, string signerField)
    {
        if (frame.Get(signerField) is not Value.BytesValue { Value.Length: 32 } signer)
        {
            return DropReason.Unsigned;
        }
        return Envelope.Verify(frame, signer.Value) switch
        {
            null => null,
            Envelope.VerifyError.MissingSignature or Envelope.VerifyError.BadSignature => DropReason.Unsigned,
            _ => DropReason.InvalidSignature,
        };
    }

    /// <summary>
    /// The procedure <paramref name="frame"/> names, as a warning line carries
    /// it: only a byte string, the way macula sends a procedure, and nothing for
    /// a procedure of any other type or none.
    /// </summary>
    internal static string ProcedureField(Value.MapValue frame) => frame.Get("procedure") switch
    {
        Value.BytesValue bytes => ProcedureField(bytes.Value),
        _ => "",
    };

    internal static string ProcedureField(string procedure) => ProcedureField(Encoding.UTF8.GetBytes(procedure));

    /// <summary>A reply's call_id as a warning line carries it: its first 4 bytes in upper-case hex.</summary>
    internal static string CallIdField(byte[] callId) => " call_id=" + Convert.ToHexString(callId.AsSpan(0, 4));

    // The procedure cut to ProcedureLimit bytes, on a character boundary.
    private static string ProcedureField(byte[] utf8)
    {
        var end = Math.Min(utf8.Length, ProcedureLimit);
        while (end > 0 && end < utf8.Length && (utf8[end] & 0xC0) == 0x80)
        {
            end--;
        }
        return " procedure=" + Printable(Encoding.UTF8.GetString(utf8, 0, end));
    }

    // The text with its control characters escaped, \n, \r and \t by name and
    // any other as \u{..}, so a trace line never breaks.
    private static string Printable(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            builder.Append(c switch
            {
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when char.IsControl(c) => $"\\u{{{(int)c:x}}}",
                _ => c.ToString(),
            });
        }
        return builder.ToString();
    }

    private async Task CloseAsync(Limiter limiter, DropKind kind, TimeSpan interval)
    {
        await Task.Delay(interval).ConfigureAwait(false);
        long later;
        (DropReason Reason, string Field)? latest;
        lock (limiter)
        {
            (later, latest) = (limiter.Later, limiter.Latest);
            (limiter.Open, limiter.Later, limiter.Latest) = (false, 0, null);
        }
        if (later > 0 && latest is { } last)
        {
            Trace(kind, later, last.Reason, last.Field);
        }
    }

    private void Trace(DropKind kind, long count, DropReason reason, string subjectField) =>
        System.Diagnostics.Trace.TraceWarning($"macula: kind={Name(kind)} count={count} reason={Name(reason)}{subjectField} (station {_station})");

    private static string Name(DropKind kind) => kind switch
    {
        DropKind.RefusedStreamOpen => "refused_stream_open",
        DropKind.DroppedCall => "dropped_call",
        _ => "dropped_reply",
    };

    private static string Name(DropReason reason) => reason switch
    {
        DropReason.InvalidSignature => "invalid_signature",
        DropReason.Unsigned => "unsigned",
        DropReason.Malformed => "malformed",
        DropReason.NotAStreamOpen => "not_a_stream_open",
        _ => "unknown_call_id",
    };

    private sealed class Limiter
    {
        // An interval is running: its first drop was traced at once.
        internal bool Open;

        // The drops after that first one, and the latest one's reason and field.
        internal long Later;
        internal (DropReason Reason, string Field)? Latest;
    }
}

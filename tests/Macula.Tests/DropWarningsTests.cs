using System.Diagnostics;
using System.Security.Cryptography;
using Macula.Connection;

namespace Macula.Tests;

/// <summary>
/// The shared drop warning tests: at most one immediate line and one closing
/// line per interval per kind. The names match the Go, Rust and Erlang tests.
/// </summary>
public class DropWarningsTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(100);

    [Fact]
    public async Task A_drop_burst_logs_one_immediate_line_and_one_closing_line_with_the_rest()
    {
        var station = RandomNumberGenerator.GetBytes(32);
        var warnings = new DropWarnings(station) { Interval = Short };
        var listener = new CapturingListener();
        Trace.Listeners.Add(listener);
        try
        {
            for (var n = 0; n < 5; n++)
            {
                warnings.Record(DropKind.RefusedStreamOpen, DropReason.InvalidSignature, DropWarnings.ProcedureField($"app/stream_{n}"));
            }
            var immediate = About(listener, station);
            await Task.Delay(Short * 3);
            var lines = About(listener, station);

            var first = Assert.Single(immediate);
            Assert.Equal(TraceEventType.Warning, first.Level);
            Assert.Contains("kind=refused_stream_open count=1 reason=invalid_signature procedure=app/stream_0", first.Message);
            Assert.Equal(2, lines.Count);
            Assert.Contains("kind=refused_stream_open count=4 reason=invalid_signature procedure=app/stream_4", lines[1].Message);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    [Fact]
    public async Task A_single_drop_logs_only_the_immediate_line()
    {
        var station = RandomNumberGenerator.GetBytes(32);
        var warnings = new DropWarnings(station) { Interval = Short };
        var listener = new CapturingListener();
        Trace.Listeners.Add(listener);
        try
        {
            warnings.Record(DropKind.DroppedReply, DropReason.UnknownCallId, DropWarnings.CallIdField(Enumerable.Repeat((byte)0xab, 16).ToArray()));
            await Task.Delay(Short * 3);

            var line = Assert.Single(About(listener, station));
            Assert.Contains("kind=dropped_reply count=1 reason=unknown_call_id call_id=ABABABAB", line.Message);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    [Fact]
    public void A_procedure_with_a_newline_stays_on_one_trace_line()
    {
        var station = RandomNumberGenerator.GetBytes(32);
        var warnings = new DropWarnings(station) { Interval = Short };
        var listener = new CapturingListener();
        Trace.Listeners.Add(listener);
        try
        {
            warnings.Record(DropKind.DroppedCall, DropReason.InvalidSignature, DropWarnings.ProcedureField("app/a\nkind=forged\u001b"));

            var line = Assert.Single(About(listener, station));
            Assert.DoesNotContain('\n', line.Message);
            Assert.Contains("procedure=app/a\\nkind=forged\\u{1b}", line.Message);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    [Fact]
    public void A_procedure_is_cut_on_a_character_boundary()
    {
        var procedure = new string('a', 255) + "é";

        Assert.Equal(" procedure=" + new string('a', 255), DropWarnings.ProcedureField(procedure));
    }

    private static List<(TraceEventType Level, string Message)> About(CapturingListener listener, byte[] station) =>
        listener.Events.Where(e => e.Message.Contains(Convert.ToHexStringLower(station))).ToList();
}

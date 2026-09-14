using System.Diagnostics;
using System.Net.Quic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Macula;
using Macula.Cbor;
using Macula.Connection;
using Macula.Frame;
using Macula.Identity;

namespace EchoCanary;

/// <summary>
/// Echo canary for io.macula.echo, based on macula-dotnet's examples/02_Call.cs.
///
/// One run uses one fresh identity on the all-zero realm, at most two routes
/// (stations), and two calls per route ("hello" and a small map), so at most
/// four calls. No retries. Every connect and call prints one JSON line.
///
/// content_match: the reply carries the same values as the payload, treating
/// text and bytes with equal contents as equal and ignoring map key order.
/// exact_match: the reply's canonical CBOR bytes equal the payload's.
///
/// Usage:
///   EchoCanary --check                                   (no network: SDK version and QUIC availability)
///   EchoCanary &lt;run-id&gt; &lt;host:port&gt; [&lt;host:port&gt;]
/// </summary>
public static class Program
{
    private const string Procedure = "io.macula.echo";
    private const string SdkName = "macula-dotnet";
    private const int MaxRoutes = 2;
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--check")
        {
            return Check();
        }
        if (args.Length < 2 || args.Length > 1 + MaxRoutes)
        {
            Console.Error.WriteLine($"usage: EchoCanary --check | EchoCanary <run-id> <host:port> [<host:port>]  (at most {MaxRoutes} routes)");
            return 2;
        }

        var runId = args[0];
        var routes = args.Skip(1).Select(ParseRoute).ToList();
        var identity = KeyPair.GenerateWithDefaultPuzzle();
        var realm = new byte[32];

        var failures = 0;
        foreach (var (host, port) in routes)
        {
            failures += await RunRouteAsync(runId, identity, realm, host, port);
        }
        return failures == 0 ? 0 : 1;
    }

    private static int Check()
    {
        var supported = QuicConnection.IsSupported;
        Console.WriteLine($"sdk={SdkName} sdk_version={SdkVersion()} runtime={RuntimeInformation.FrameworkDescription} quic_supported={supported}");
        return supported ? 0 : 1;
    }

    private static async Task<int> RunRouteAsync(string runId, KeyPair identity, byte[] realm, string host, int port)
    {
        var route = $"{host}:{port}";
        var connectUtc = DateTimeOffset.UtcNow;
        var connectWatch = Stopwatch.StartNew();
        Session session;
        try
        {
            session = await Session.ConnectAsync(host, port, identity, Trust.UseWebPki, HandshakeTimeout);
        }
        catch (Exception ex)
        {
            connectWatch.Stop();
            Emit(new CanaryRecord(runId, Iso(connectUtc), SdkName, SdkVersion(), route, "connect", null, null,
                connectWatch.ElapsedMilliseconds, null, ex.GetType().FullName, ExceptionText(ex), null, null));
            return 1;
        }
        connectWatch.Stop();
        Emit(new CanaryRecord(runId, Iso(connectUtc), SdkName, SdkVersion(), route, "connect", null, null,
            connectWatch.ElapsedMilliseconds, null, null, null, null, Hex(session.RemoteInfo.NodeId)));

        await using (session)
        {
            var failures = 0;
            failures += await CallOnceAsync(runId, route, session, realm, "hello", Value.Text("hello"));
            failures += await CallOnceAsync(runId, route, session, realm, "map", MapPayload(runId));
            return failures;
        }
    }

    private static async Task<int> CallOnceAsync(string runId, string route, Session session, byte[] realm, string label, Value payload)
    {
        var utc = DateTimeOffset.UtcNow;
        var deadlineMs = utc.ToUnixTimeMilliseconds() + (long)CallTimeout.TotalMilliseconds;
        var watch = Stopwatch.StartNew();
        try
        {
            var response = await session.CallAsync(Procedure, realm, payload, deadlineMs, CallTimeout);
            watch.Stop();
            switch (response)
            {
                case CallResponse.Result r:
                    var exact = CborCodec.Encode(r.Payload).AsSpan().SequenceEqual(CborCodec.Encode(payload));
                    var content = Normalize(r.Payload) == Normalize(payload);
                    Emit(new CanaryRecord(runId, Iso(utc), SdkName, SdkVersion(), route, label, content, exact,
                        watch.ElapsedMilliseconds, null, null, null, Describe(r.Payload), Hex(r.RespondedBy)));
                    return exact ? 0 : 1;
                case CallResponse.Error e:
                    Emit(new CanaryRecord(runId, Iso(utc), SdkName, SdkVersion(), route, label, false, false,
                        watch.ElapsedMilliseconds, e.Code, e.Name, e.Detail, null, Hex(e.ReportedBy)));
                    return 1;
                default:
                    Emit(new CanaryRecord(runId, Iso(utc), SdkName, SdkVersion(), route, label, false, false,
                        watch.ElapsedMilliseconds, null, "unexpected_response", response.GetType().FullName, null, null));
                    return 1;
            }
        }
        catch (Exception ex)
        {
            watch.Stop();
            Emit(new CanaryRecord(runId, Iso(utc), SdkName, SdkVersion(), route, label, false, false,
                watch.ElapsedMilliseconds, null, ex.GetType().FullName, ExceptionText(ex), null, null));
            return 1;
        }
    }

    private static Value MapPayload(string runId) => Value.Map(new List<KeyValuePair<Value, Value>>
    {
        new(Value.Text("sdk"), Value.Text(SdkName)),
        new(Value.Text("sdk_version"), Value.Text(SdkVersion())),
        new(Value.Text("run"), Value.Text(runId)),
    });

    private static string SdkVersion()
    {
        var assembly = typeof(KeyPair).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
        var commit = Environment.GetEnvironmentVariable("MACULA_DOTNET_COMMIT");
        return string.IsNullOrEmpty(commit) ? version : $"{version} (git {commit})";
    }

    private static (string Host, int Port) ParseRoute(string hostPort)
    {
        var colon = hostPort.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(hostPort[(colon + 1)..], out var port))
        {
            throw new ArgumentException($"expected host:port, got '{hostPort}'");
        }
        return (hostPort[..colon], port);
    }

    private static string ExceptionText(Exception ex) => ex.InnerException is null
        ? ex.Message
        : $"{ex.Message} (inner {ex.InnerException.GetType().FullName}: {ex.InnerException.Message})";

    private static string Iso(DateTimeOffset utc) => utc.ToString("O");

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);

    // Content-level form: text and bytes with the same contents compare equal,
    // map entries compare regardless of order.
    private static string Normalize(Value value) => value switch
    {
        Value.TextValue t => "b:" + Convert.ToHexStringLower(t.Utf8),
        Value.BytesValue b => "b:" + Convert.ToHexStringLower(b.Value),
        Value.UIntValue u => "i:" + u.Value,
        Value.NegIntValue n => "i:" + (-1L - (long)n.NMinusOne),
        Value.NullValue => "null",
        Value.ListValue l => "[" + string.Join(",", l.Items.Select(Normalize)) + "]",
        Value.MapValue m => "{" + string.Join(",", m.Entries.Select(e => Normalize(e.Key) + "=" + Normalize(e.Value)).OrderBy(s => s, StringComparer.Ordinal)) + "}",
        _ => value.GetType().Name + ":" + Convert.ToHexStringLower(CborCodec.Encode(value)),
    };

    private static string Describe(Value value) => value switch
    {
        Value.TextValue t => JsonSerializer.Serialize(Encoding.UTF8.GetString(t.Utf8)),
        Value.BytesValue b => $"h'{Convert.ToHexStringLower(b.Value)}'",
        Value.UIntValue u => u.Value.ToString(),
        Value.NegIntValue n => (-1L - (long)n.NMinusOne).ToString(),
        Value.NullValue => "null",
        Value.ListValue l => "[" + string.Join(", ", l.Items.Select(Describe)) + "]",
        Value.MapValue m => "{" + string.Join(", ", m.Entries.Select(e => $"{Describe(e.Key)}: {Describe(e.Value)}")) + "}",
        _ => value.GetType().Name,
    };

    private static void Emit(CanaryRecord record) => Console.WriteLine(JsonSerializer.Serialize(record));

    private sealed record CanaryRecord(
        string run,
        string utc,
        string sdk,
        string sdk_version,
        string route,
        string call,
        bool? content_match,
        bool? exact_match,
        long duration_ms,
        int? error_code,
        string? error_name,
        string? error,
        string? reply,
        string? peer);
}

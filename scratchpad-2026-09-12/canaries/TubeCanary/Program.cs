using System.Diagnostics;
using System.Net.Quic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Macula;
using Macula.Cbor;
using Macula.Connection;
using Macula.Dht;
using Macula.Frame;
using Macula.Identity;
using Macula.Streaming;

namespace TubeCanary;

/// <summary>
/// hecate-tube canary caller for tube.watch_video_clip (server_stream), based on
/// macula-dotnet's StreamHandle and DirectDial APIs.
///
/// One run uses two fresh identities. Identity A connects to the given station and
/// opens the stream there (station path). Identity A's session is then used only to
/// resolve the direct-dial advertisement, and identity B dials the serving station
/// and opens the stream there (direct path). Each path reads until the stream ends.
///
/// A bad_request STREAM_ERROR before any data on the first STREAM_OPEN is retried
/// exactly once; if the retry succeeds it is recorded as the known hazard
/// hecate-tube#3, not as a failure.
///
/// Usage:
///   TubeCanary --check                                  (no network)
///   TubeCanary &lt;run-id&gt; &lt;clip-id&gt; &lt;station host:port&gt;
/// </summary>
public static class Program
{
    private const string Procedure = "tube.watch_video_clip";
    private const string SdkName = "macula-dotnet";
    // sha256("io.macula")
    private const string RealmHex = "abb81b5a614b63551b400b810648c0c8a78efad845442630c94b46cc95d2fcd1";
    private const string KnownHazard = "hecate-tube#3";
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DialTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StreamDeadline = TimeSpan.FromSeconds(120);

    private sealed record Attempt(
        int attempt,
        string utc,
        string outcome,
        string? error_code,
        string? error,
        long bytes,
        int chunks,
        int non_raw_chunks,
        string sha256,
        long duration_ms);

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--check")
        {
            return Check();
        }
        if (args.Length == 2 && args[0] == "--dht-diag")
        {
            return await DhtDiagAsync(args[1]);
        }
        if (args.Length != 3)
        {
            Console.Error.WriteLine("usage: TubeCanary --check | TubeCanary <run-id> <clip-id> <station host:port>");
            return 2;
        }

        var runId = args[0];
        var clipId = args[1];
        var (host, port) = ParseRoute(args[2]);
        var route = $"{host}:{port}";
        var realm = Convert.FromHexString(RealmHex);
        var streamArgs = Value.Map(new List<KeyValuePair<Value, Value>>
        {
            new(Value.Text("clip_id"), Value.Bytes(Encoding.ASCII.GetBytes(clipId))),
        });
        var identityA = KeyPair.GenerateWithDefaultPuzzle();
        var identityB = KeyPair.GenerateWithDefaultPuzzle();

        var connectUtc = DateTimeOffset.UtcNow;
        var connectWatch = Stopwatch.StartNew();
        Session sessionA;
        try
        {
            sessionA = await Session.ConnectAsync(host, port, identityA, Trust.UseWebPki, HandshakeTimeout);
        }
        catch (Exception ex)
        {
            connectWatch.Stop();
            Emit(new
            {
                run = runId, utc = Iso(connectUtc), sdk = SdkName, sdk_version = SdkVersion(), path = "station", route,
                step = "connect", outcome = "connect_failed", error_code = (string?)null, error = $"{ex.GetType().FullName}: {ExceptionText(ex)}",
                duration_ms = connectWatch.ElapsedMilliseconds, identity = Short(identityA.NodeId()),
            });
            return 1;
        }
        connectWatch.Stop();
        Emit(new
        {
            run = runId, utc = Iso(connectUtc), sdk = SdkName, sdk_version = SdkVersion(), path = "station", route,
            step = "connect", outcome = "ok", duration_ms = connectWatch.ElapsedMilliseconds,
            identity = Short(identityA.NodeId()), peer = Hex(sessionA.RemoteInfo.NodeId),
        });

        await using (sessionA)
        {
            var failures = 0;
            // TUBE_DIRECT_ONLY=1 skips the station-path stream; identity A's session is still used to resolve.
            if (Environment.GetEnvironmentVariable("TUBE_DIRECT_ONLY") != "1")
            {
                failures += await StationPathAsync(runId, route, sessionA, identityA, realm, streamArgs);
            }
            failures += await DirectPathAsync(runId, route, sessionA, identityB, realm, streamArgs);
            return failures == 0 ? 0 : 1;
        }
    }

    /// <summary>
    /// Read-only DHT diagnosis of the direct-dial path: which station the
    /// procedure_advertisement names, and the state of that station's own
    /// station_endpoint record. At most three DHT calls, no tube.* calls.
    /// </summary>
    private static async Task<int> DhtDiagAsync(string hostPort)
    {
        const int maxDhtCalls = 3;
        var (host, port) = ParseRoute(hostPort);
        var realm = Convert.FromHexString(RealmHex);
        var identity = KeyPair.GenerateWithDefaultPuzzle();
        await using var session = await Session.ConnectAsync(host, port, identity, Trust.UseWebPki, HandshakeTimeout);

        var dhtCalls = 0;
        var uri = RecordFactory.DiscoveryUri(realm, Procedure);
        var advKey = RecordFactory.ProcedureKey(uri);
        dhtCalls++;
        var advRecords = await DhtClient.FindRecordsAsync(session, advKey);
        Emit(new
        {
            step = "advertisement_lookup", utc = Iso(DateTimeOffset.UtcNow), route = hostPort, peer = Hex(session.RemoteInfo.NodeId),
            discovery_uri = uri, key = Hex(advKey), records = advRecords.Count,
        });

        var stations = new List<byte[]>();
        byte[]? sdkPick = null;
        for (var i = 0; i < advRecords.Count; i++)
        {
            var rec = advRecords[i];
            var verify = RecordFactory.Verify(rec);
            ProcedureAdvertisement? adv = null;
            string? readError = null;
            try
            {
                adv = RecordReading.ReadProcedureAdvertisement(rec);
            }
            catch (Exception ex)
            {
                readError = ex.Message;
            }
            var isSdkPick = sdkPick is null && verify is null && adv is not null;
            if (isSdkPick)
            {
                sdkPick = adv!.ServingStation;
            }
            Emit(new
            {
                step = "advertisement_record", index = i, type = rec.Type, signer = Hex(rec.Key),
                created_at = Ms(rec.CreatedAt), expires_at = Ms(rec.ExpiresAt), verify = verify?.ToString() ?? "ok",
                procedure_uri = adv?.ProcedureUri, advertiser_node = adv is null ? null : Hex(adv.AdvertiserNode),
                serving_station = adv is null ? null : Hex(adv.ServingStation), cert_chain = adv?.CertChain is { Length: > 0 },
                sdk_would_pick = isSdkPick, read_error = readError,
            });
            if (adv is not null && verify is null && !stations.Any(s => s.AsSpan().SequenceEqual(adv.ServingStation)))
            {
                stations.Add(adv.ServingStation);
            }
        }

        foreach (var station in stations)
        {
            if (dhtCalls >= maxDhtCalls)
            {
                Emit(new { step = "endpoint_skipped", serving_station = Hex(station), reason = "dht call budget reached" });
                continue;
            }
            var epKey = RecordFactory.StationEndpointKey(station);
            dhtCalls++;
            try
            {
                var rec = await DhtClient.FindRecordAsync(session, epKey);
                EmitEndpoint("find_record", station, epKey, rec);
            }
            catch (DhtClient.NotFoundException)
            {
                Emit(new { step = "endpoint_record", via = "find_record", serving_station = Hex(station), key = Hex(epKey), outcome = "not_found" });
            }
            catch (Exception ex)
            {
                Emit(new { step = "endpoint_record", via = "find_record", serving_station = Hex(station), key = Hex(epKey), outcome = "exception", error = $"{ex.GetType().FullName}: {ExceptionText(ex)}" });
            }

            if (stations.Count == 1 && dhtCalls < maxDhtCalls)
            {
                dhtCalls++;
                try
                {
                    var recs = await DhtClient.FindRecordsAsync(session, epKey);
                    Emit(new { step = "endpoint_lookup_all", via = "find_records", serving_station = Hex(station), key = Hex(epKey), records = recs.Count });
                    foreach (var r in recs)
                    {
                        EmitEndpoint("find_records", station, epKey, r);
                    }
                }
                catch (Exception ex)
                {
                    Emit(new { step = "endpoint_lookup_all", via = "find_records", serving_station = Hex(station), key = Hex(epKey), outcome = "exception", error = $"{ex.GetType().FullName}: {ExceptionText(ex)}" });
                }
            }
        }

        Emit(new { step = "done", utc = Iso(DateTimeOffset.UtcNow), dht_calls = dhtCalls, sdk_pick = sdkPick is null ? null : Hex(sdkPick) });
        return 0;
    }

    private static void EmitEndpoint(string via, byte[] station, byte[] key, Record rec)
    {
        var verify = RecordFactory.Verify(rec);
        StationEndpoint? ep = null;
        string? readError = null;
        try
        {
            ep = RecordReading.ReadStationEndpoint(rec);
        }
        catch (Exception ex)
        {
            readError = ex.Message;
        }
        Emit(new
        {
            step = "endpoint_record", via, serving_station = Hex(station), key = Hex(key), type = rec.Type,
            signer_matches_station = rec.Key.AsSpan().SequenceEqual(station),
            created_at = Ms(rec.CreatedAt), expires_at = Ms(rec.ExpiresAt), verify = verify?.ToString() ?? "ok",
            quic_port = ep?.QuicPort, host_advertised = ep?.HostAdvertised, read_error = readError,
        });
    }

    private static string Ms(long ms) => ms <= 0 ? "none" : DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("O");

    private static int Check()
    {
        var supported = QuicConnection.IsSupported;
        Console.WriteLine($"sdk={SdkName} sdk_version={SdkVersion()} runtime={RuntimeInformation.FrameworkDescription} quic_supported={supported}");
        return supported ? 0 : 1;
    }

    private static async Task<int> StationPathAsync(string runId, string route, Session session, KeyPair identity, byte[] realm, Value streamArgs)
    {
        var attempts = new List<Attempt>();
        var first = await OpenAndReadAsync(1, () => StreamHandle.OpenAsync(session, Procedure, realm, StreamMode.ServerStream, streamArgs, DeadlineMs(), identity));
        attempts.Add(first);
        EmitAttempt(runId, route, "station", identity, null, first);
        if (first.outcome == "bad_request")
        {
            var second = await OpenAndReadAsync(2, () => StreamHandle.OpenAsync(session, Procedure, realm, StreamMode.ServerStream, streamArgs, DeadlineMs(), identity));
            attempts.Add(second);
            EmitAttempt(runId, route, "station", identity, null, second);
        }
        return EmitSummary(runId, route, "station", identity, null, attempts, null);
    }

    private static async Task<int> DirectPathAsync(string runId, string route, Session resolveVia, KeyPair identity, byte[] realm, Value streamArgs)
    {
        var attempts = new List<Attempt>();
        var setupUtc = DateTimeOffset.UtcNow;
        var setupWatch = Stopwatch.StartNew();
        Session? target = null;
        try
        {
            StreamHandle handle;
            try
            {
                (target, handle) = await DirectDial.OpenStreamDirectAsync(
                    resolveVia, identity, realm, Procedure, StreamMode.ServerStream, streamArgs, DeadlineMs(), DialTimeout);
            }
            catch (DirectDial.StationEndpointNotFoundException ex)
            {
                setupWatch.Stop();
                return EmitSetupFailure(runId, route, identity, setupUtc, setupWatch.ElapsedMilliseconds, "station_endpoint_not_found", ex);
            }
            catch (DirectDial.ProcedureNotAdvertisedException ex)
            {
                setupWatch.Stop();
                return EmitSetupFailure(runId, route, identity, setupUtc, setupWatch.ElapsedMilliseconds, "procedure_not_advertised", ex);
            }
            catch (DirectDial.NoTrustedAdvertisementException ex)
            {
                setupWatch.Stop();
                return EmitSetupFailure(runId, route, identity, setupUtc, setupWatch.ElapsedMilliseconds, "no_trusted_advertisement", ex);
            }
            catch (DirectDial.TrustViolationException ex)
            {
                setupWatch.Stop();
                return EmitSetupFailure(runId, route, identity, setupUtc, setupWatch.ElapsedMilliseconds, "trust_violation", ex);
            }
            catch (Exception ex)
            {
                setupWatch.Stop();
                return EmitSetupFailure(runId, route, identity, setupUtc, setupWatch.ElapsedMilliseconds, "setup_exception", ex);
            }
            setupWatch.Stop();
            var dialed = Hex(target.RemoteInfo.NodeId);

            var first = await ReadAsync(1, handle, Stopwatch.StartNew(), DateTimeOffset.UtcNow);
            attempts.Add(first);
            EmitAttempt(runId, route, "direct", identity, dialed, first);
            if (first.outcome == "bad_request")
            {
                var dialedSession = target;
                var second = await OpenAndReadAsync(2, () => StreamHandle.OpenAsync(dialedSession, Procedure, realm, StreamMode.ServerStream, streamArgs, DeadlineMs(), identity));
                attempts.Add(second);
                EmitAttempt(runId, route, "direct", identity, dialed, second);
            }
            return EmitSummary(runId, route, "direct", identity, dialed, attempts, setupWatch.ElapsedMilliseconds);
        }
        finally
        {
            if (target is not null)
            {
                await target.CloseAsync();
            }
        }
    }

    private static async Task<Attempt> OpenAndReadAsync(int attemptNo, Func<Task<StreamHandle>> open)
    {
        var utc = DateTimeOffset.UtcNow;
        var watch = Stopwatch.StartNew();
        StreamHandle handle;
        try
        {
            handle = await open();
        }
        catch (Exception ex)
        {
            watch.Stop();
            return new Attempt(attemptNo, Iso(utc), "open_exception", null, $"{ex.GetType().FullName}: {ExceptionText(ex)}", 0, 0, 0, EmptySha(), watch.ElapsedMilliseconds);
        }
        return await ReadAsync(attemptNo, handle, watch, utc);
    }

    private static async Task<Attempt> ReadAsync(int attemptNo, StreamHandle handle, Stopwatch watch, DateTimeOffset utc)
    {
        long bytes = 0;
        var chunks = 0;
        var nonRaw = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (watch.Elapsed < StreamDeadline)
        {
            StreamItem item;
            try
            {
                item = await handle.RecvAsync(FrameTimeout);
            }
            catch (StreamHandle.RecvStreamException ex)
            {
                watch.Stop();
                var outcome = IsBadRequest(ex) && bytes == 0 ? "bad_request" : "stream_error";
                return new Attempt(attemptNo, Iso(utc), outcome, ex.Code, ex.Message, bytes, chunks, nonRaw, Convert.ToHexStringLower(hash.GetHashAndReset()), watch.ElapsedMilliseconds);
            }
            catch (TimeoutException ex)
            {
                watch.Stop();
                return new Attempt(attemptNo, Iso(utc), "frame_timeout", null, ex.Message, bytes, chunks, nonRaw, Convert.ToHexStringLower(hash.GetHashAndReset()), watch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                watch.Stop();
                return new Attempt(attemptNo, Iso(utc), "recv_exception", null, $"{ex.GetType().FullName}: {ExceptionText(ex)}", bytes, chunks, nonRaw, Convert.ToHexStringLower(hash.GetHashAndReset()), watch.ElapsedMilliseconds);
            }

            if (item is not StreamItem.Data data)
            {
                watch.Stop();
                return new Attempt(attemptNo, Iso(utc), "ok", null, null, bytes, chunks, nonRaw, Convert.ToHexStringLower(hash.GetHashAndReset()), watch.ElapsedMilliseconds);
            }

            chunks++;
            if (data.Body is Value.BytesValue raw)
            {
                hash.AppendData(raw.Value);
                bytes += raw.Value.Length;
            }
            else
            {
                nonRaw++;
                var encoded = CborCodec.Encode(data.Body);
                hash.AppendData(encoded);
                bytes += encoded.Length;
            }
        }
        watch.Stop();
        return new Attempt(attemptNo, Iso(utc), "deadline_exceeded", null, $"stream did not end within {StreamDeadline}", bytes, chunks, nonRaw, Convert.ToHexStringLower(hash.GetHashAndReset()), watch.ElapsedMilliseconds);
    }

    // StreamHandle.RecvAsync reports a STREAM_ERROR as "peer aborted the stream: {code} ({message})".
    // macula_streamer aborts a rejected open with code "cancelled" and the reason as the message.
    private static bool IsBadRequest(StreamHandle.RecvStreamException ex) =>
        ex.Code == "bad_request" || ex.Message.Contains("(bad_request)", StringComparison.Ordinal);

    private static int EmitSetupFailure(string runId, string route, KeyPair identity, DateTimeOffset utc, long durationMs, string outcome, Exception ex)
    {
        Emit(new
        {
            run = runId, utc = Iso(utc), sdk = SdkName, sdk_version = SdkVersion(), path = "direct", route,
            step = "summary", outcome, known_hazard = (string?)null, attempts = 0,
            error_code = (string?)null, error = $"{ex.GetType().FullName}: {ExceptionText(ex)}",
            bytes = 0L, chunks = 0, sha256 = (string?)null, setup_ms = durationMs, duration_ms = (long?)null,
            identity = Short(identity.NodeId()), dialed_station = (string?)null,
        });
        return 1;
    }

    private static void EmitAttempt(string runId, string route, string path, KeyPair identity, string? dialed, Attempt a) =>
        Emit(new
        {
            run = runId, utc = a.utc, sdk = SdkName, sdk_version = SdkVersion(), path, route, step = "attempt",
            a.attempt, a.outcome, a.error_code, a.error, a.bytes, a.chunks, a.non_raw_chunks, a.sha256, a.duration_ms,
            identity = Short(identity.NodeId()), dialed_station = dialed,
        });

    private static int EmitSummary(string runId, string route, string path, KeyPair identity, string? dialed, List<Attempt> attempts, long? setupMs)
    {
        var last = attempts[^1];
        var hazard = attempts.Count == 2 && attempts[0].outcome == "bad_request" && last.outcome == "ok" ? KnownHazard : null;
        Emit(new
        {
            run = runId, utc = attempts[0].utc, sdk = SdkName, sdk_version = SdkVersion(), path, route, step = "summary",
            outcome = last.outcome, known_hazard = hazard, attempts = attempts.Count,
            last.error_code, last.error, last.bytes, last.chunks, last.sha256, setup_ms = setupMs, last.duration_ms,
            identity = Short(identity.NodeId()), dialed_station = dialed,
        });
        return last.outcome == "ok" ? 0 : 1;
    }

    private static long DeadlineMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (long)StreamDeadline.TotalMilliseconds;

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

    private static string EmptySha() => Convert.ToHexStringLower(SHA256.HashData(Array.Empty<byte>()));

    private static string Iso(DateTimeOffset utc) => utc.ToString("O");

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);

    private static string Short(byte[] nodeId) => Convert.ToHexStringLower(nodeId)[..16];

    private static void Emit(object record) => Console.WriteLine(JsonSerializer.Serialize(record));
}

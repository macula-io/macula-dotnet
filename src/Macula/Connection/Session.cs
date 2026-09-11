using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;
using Macula.Bolt4;
using Macula.Frame;
using Macula.Identity;
using Macula.Ucan;

namespace Macula.Connection;

/// <summary>A provider-side handler for one advertised (realm, procedure). Throw <see cref="CallHandlerException"/> for an application-level failure with a message; any other exception is treated as a crash.</summary>
public delegate Task<Value> CallHandler(Value payload);

/// <summary>Resolves an inbound CALL's (realm, procedure) to a handler, or null if nothing is advertised for it.</summary>
public delegate CallHandler? CallLookup(byte[] realm, string procedure);

/// <summary>Resolves an inbound CALL's (realm, procedure) to the <see cref="Policy"/> gating it, consulted BEFORE lookup -- see <see cref="Session.ServeOneCallGatedAsync"/>. Defaults to <see cref="Policy.Open"/> for any (realm, procedure) an implementation doesn't explicitly gate.</summary>
public delegate Policy PolicyLookup(byte[] realm, string procedure);

/// <summary>Thrown by a <see cref="CallHandler"/> to produce an explicit `unknown_error` reply with this message as `detail`, distinct from an unexpected crash (temporary_relay_failure, no detail).</summary>
public sealed class CallHandlerException : Exception
{
    public CallHandlerException(string message) : base(message) { }
}

public sealed class ConnectRefusedException : Exception
{
    public long? RefusalCode { get; }

    public ConnectRefusedException(long? refusalCode)
        : base(refusalCode is { } code ? $"station refused the connection (refusal_code={code})" : "station refused the connection")
    {
        RefusalCode = refusalCode;
    }
}

/// <summary>
/// The HELLO frame's own signature didn't verify against the node_id it
/// claims -- proves nothing about who actually sent it.
/// </summary>
public sealed class HelloSignatureInvalidException : Exception
{
    public Envelope.VerifyError Reason { get; }

    public HelloSignatureInvalidException(Envelope.VerifyError reason)
        : base($"HELLO signature check failed: {reason}")
    {
        Reason = reason;
    }
}

/// <summary>
/// A live connection to one macula station: the QUIC transport, the
/// control stream, and the CONNECT/HELLO handshake state machine. Client
/// side of `macula_peering_conn.erl`'s `gen_statem`:
/// connecting -&gt; handshaking -&gt; connected -&gt; draining -&gt; terminated.
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public sealed class Session : IAsyncDisposable, IFrameSink
{
    private readonly QuicConnection _connection;
    private readonly FrameStream _control;
    private bool _closed;

    public KeyPair Identity { get; }
    public HelloInfo RemoteInfo { get; }

    private Session(QuicConnection connection, FrameStream control, KeyPair identity, HelloInfo remoteInfo)
    {
        _connection = connection;
        _control = control;
        Identity = identity;
        RemoteInfo = remoteInfo;
    }

    /// <summary>
    /// Dial <paramref name="host"/>:<paramref name="port"/>, open the
    /// control stream, send a signed CONNECT, and wait for HELLO. Throws
    /// <see cref="ConnectRefusedException"/> if the station's HELLO carries
    /// `accepted = false`, or <see cref="OperationCanceledException"/> if
    /// no HELLO arrives within <paramref name="handshakeTimeout"/> (30s
    /// default, matching `HANDSHAKE_TIMEOUT_MS` -- its most common
    /// real-world trigger is a protocol version mismatch, which looks like
    /// a plain timeout, not an explicit error frame).
    /// </summary>
    public static async Task<Session> ConnectAsync(
        string host,
        int port,
        KeyPair identity,
        Trust trust,
        TimeSpan? handshakeTimeout = null,
        CancellationToken ct = default)
    {
        var clientOptions = new QuicClientConnectionOptions
        {
            RemoteEndPoint = new DnsEndPoint(host, port),
            DefaultStreamErrorCode = 0,
            DefaultCloseErrorCode = 0,
            // System.Net.Quic defaults to accepting ZERO inbound streams,
            // unlike quinn (Rust's QUIC crate), which accepts by default --
            // a client must opt in explicitly or AcceptInboundStreamAsync
            // throws. This session needs inbound capacity regardless of
            // whether the caller ever advertises a procedure, since the
            // decision to advertise happens after the connection already
            // exists.
            MaxInboundBidirectionalStreams = 100,
            MaxInboundUnidirectionalStreams = 100,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                ApplicationProtocols = new List<SslApplicationProtocol> { new("macula") },
                TargetHost = host,
                RemoteCertificateValidationCallback = TrustValidation.BuildCallback(trust),
            },
        };

        var connection = await QuicConnection.ConnectAsync(clientOptions, ct).ConfigureAwait(false);
        QuicStream? controlStream = null;
        try
        {
            controlStream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, ct).ConfigureAwait(false);
            var control = new FrameStream(controlStream);

            var puzzleEvidence = Puzzle.Evidence(identity.PublicBytes());
            var connectSpec = ConnectSpec.New(identity.PublicBytes(), puzzleEvidence);
            var signed = Envelope.Sign(ConnectFrame.Build(connectSpec), identity);
            await control.SendFrameAsync(signed, ct).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(handshakeTimeout ?? TimeSpan.FromSeconds(30));

            Value helloFrame;
            try
            {
                helloFrame = await control.RecvFrameAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException(
                    "no HELLO within the handshake timeout -- the most common real-world cause is a " +
                    "protocol version mismatch, which the station's own peering layer doesn't report as " +
                    "an explicit error frame");
            }

            var helloInfo = HelloFrame.Parse(helloFrame);

            // The HELLO's own signature must verify against the node_id it
            // claims -- proves nothing about who actually sent it otherwise.
            // A station is never expected to send anything but a
            // legitimately-signed HELLO at this point, but skipping this
            // check would mean trusting the peer's self-reported identity
            // on faith alone.
            var helloMap = (Value.MapValue)helloFrame;
            if (Envelope.Verify(helloMap, helloInfo.NodeId) is { } verifyError)
            {
                throw new HelloSignatureInvalidException(verifyError);
            }

            if (!helloInfo.Accepted)
            {
                throw new ConnectRefusedException(helloInfo.RefusalCode);
            }

            return new Session(connection, control, identity, helloInfo);
        }
        catch
        {
            if (controlStream is not null)
            {
                await controlStream.DisposeAsync().ConfigureAwait(false);
            }
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Sends a frame on the control stream, auto-signing it first.</summary>
    public Task SendAsync(Value.MapValue frame, CancellationToken ct = default) =>
        _control.SendFrameAsync(Envelope.Sign(frame, Identity), ct);

    /// <summary>Receives the next frame off the control stream.</summary>
    public Task<Value> RecvAsync(CancellationToken ct = default) => _control.RecvFrameAsync(ct);

    /// <summary>
    /// Send a signed CALL on the control stream and wait for the matching
    /// RESULT or ERROR, correlated by call_id.
    ///
    /// Known v1 limitation (control stream only, matching the sibling
    /// Go/Rust SDKs): any frame that arrives before the match (e.g. an
    /// EVENT from an active SUBSCRIBE) is discarded, not queued or
    /// dispatched elsewhere -- correct for a client doing one thing at a
    /// time on the control stream, not yet correct for CALL and
    /// PUBLISH/SUBSCRIBE used concurrently on it.
    /// </summary>
    public async Task<CallResponse> CallAsync(string procedure, byte[] realm, Value payload, long deadlineMs, TimeSpan timeout, CancellationToken ct = default)
    {
        var requestId = RpcFacts.RandomRequestId();
        await RpcFacts.AnnounceSentAsync(this, realm, Identity, requestId, ct).ConfigureAwait(false);
        CallResponse? resp = null;
        Exception? err = null;
        try
        {
            resp = await _control.CallAsync(procedure, realm, payload, deadlineMs, Identity, timeout, ct).ConfigureAwait(false);
            return resp;
        }
        catch (Exception e)
        {
            err = e;
            throw;
        }
        finally
        {
            await RpcFacts.AnnounceCompletedAsync(this, realm, Identity, requestId, resp, err, ct).ConfigureAwait(false);
        }
    }

    /// <summary>As <see cref="CallAsync"/>, attaching ucanToken -- for a procedure gated by <see cref="Policy.Required"/> on the provider side.</summary>
    public async Task<CallResponse> CallWithUcanAsync(string procedure, byte[] realm, Value payload, long deadlineMs, TimeSpan timeout, byte[] ucanToken, CancellationToken ct = default)
    {
        var requestId = RpcFacts.RandomRequestId();
        await RpcFacts.AnnounceSentAsync(this, realm, Identity, requestId, ct).ConfigureAwait(false);
        CallResponse? resp = null;
        Exception? err = null;
        try
        {
            resp = await _control.CallAsync(procedure, realm, payload, deadlineMs, Identity, timeout, ucanToken, ct).ConfigureAwait(false);
            return resp;
        }
        catch (Exception e)
        {
            err = e;
            throw;
        }
        finally
        {
            await RpcFacts.AnnounceCompletedAsync(this, realm, Identity, requestId, resp, err, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Send a signed PUBLISH, carrying the end-to-end `publisher_sig`
    /// (over topic/realm/publisher/seq/payload, independent of frame
    /// type) so the resulting EVENT survives being relayed beyond one
    /// hop -- a station verifies an EVENT's per-hop `signature` against
    /// whichever station forwarded it, which only matches on hop 1;
    /// every hop after that needs `publisher_sig` instead. Matches the
    /// Erlang reference SDK's own default (`pubsub_emit_publisher_sig`,
    /// true since macula 4.6.0). Fire-and-forget -- no reply is expected
    /// on the wire; a subscriber (this session included, if subscribed
    /// to the same topic/realm) receives an EVENT asynchronously, read
    /// via <see cref="RecvAsync"/> / <see cref="RecvEventAsync"/>.
    /// </summary>
    public Task PublishAsync(PublishSpec spec, CancellationToken ct = default) =>
        SendAsync(Envelope.SignPublisher(PublishFrame.Build(spec), Identity), ct);

    public Task SubscribeAsync(SubscribeSpec spec, CancellationToken ct = default) =>
        SendAsync(SubscribeFrame.Build(spec), ct);

    public Task UnsubscribeAsync(UnsubscribeSpec spec, CancellationToken ct = default) =>
        SendAsync(UnsubscribeFrame.Build(spec), ct);

    /// <summary>
    /// Registers this connection as the handler for `spec`'s
    /// (realm, procedure). Fire-and-forget on the wire; the station then
    /// routes inbound CALLs (control stream) and STREAM_OPENs (a fresh
    /// dedicated stream -- see <see cref="AcceptDedicatedStreamAsync"/>)
    /// for that procedure back to this connection.
    /// </summary>
    public Task AdvertiseAsync(AdvertiseSpec spec, CancellationToken ct = default) =>
        SendAsync(AdvertiseFrame.Build(spec), ct);

    public Task UnadvertiseAsync(UnadvertiseSpec spec, CancellationToken ct = default) =>
        SendAsync(UnadvertiseFrame.Build(spec), ct);

    /// <summary>
    /// Read the next frame and parse it as an EVENT, bounded by
    /// <paramref name="timeout"/>. Any non-EVENT frame received first is an
    /// error, not silently skipped -- unlike <see cref="CallAsync"/>'s
    /// response wait, a caller waiting specifically for a pubsub delivery
    /// has no reason to expect anything else to legitimately arrive first.
    /// </summary>
    public async Task<EventInfo> RecvEventAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        Value value;
        try
        {
            value = await RecvAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"no event within {timeout}");
        }
        return EventFrameParsing.Parse(value);
    }

    /// <summary>
    /// The provider role's counterpart to <see cref="CallAsync"/>: block for
    /// the next inbound CALL frame on the control stream, bounded by
    /// <paramref name="timeout"/>, look it up via <paramref name="lookup"/>,
    /// invoke the matching handler, and send the resulting RESULT or ERROR
    /// back over this same connection.
    ///
    /// Any non-CALL frame that arrives first (e.g. a stray EVENT from an
    /// active <see cref="SubscribeAsync"/>, or a RESULT/ERROR for some other
    /// in-flight <see cref="CallAsync"/>) is discarded, not queued -- the
    /// same "control stream, one thing at a time" limitation
    /// <see cref="CallAsync"/>'s own doc already carries. A session that
    /// needs to serve CALLs and also act as a caller/subscriber concurrently
    /// should use a second <see cref="Session"/>.
    /// </summary>
    public Task ServeOneCallAsync(CallLookup lookup, TimeSpan timeout, CancellationToken ct = default) =>
        ServeOneCallGatedAsync(lookup, OpenPolicy, timeout, ct);

    /// <summary>
    /// As <see cref="ServeOneCallAsync"/>, additionally gating each inbound
    /// CALL through policy BEFORE lookup runs -- mirrors
    /// `macula_station_link.erl`'s `handle_inbound_call/2` exactly: an open
    /// policy (the default, <see cref="Policy.Open"/>) behaves identically
    /// to plain <see cref="ServeOneCallAsync"/>; a <see cref="Policy.Required"/>
    /// policy demands a CALL's UcanToken verify against the required
    /// issuer and name the CALL's caller as its audience, and refuses with
    /// BOLT#4 Unauthorized WITHOUT ever invoking lookup or a handler if it
    /// doesn't -- a CallHandler never sees the raw token either way,
    /// matching the reference's own handler contract (payload only).
    ///
    /// Before any policy runs, the CALL's signature must verify against the
    /// caller it names; one that doesn't is dropped with no reply, as
    /// `macula_station_link.erl`'s `on_inbound_call/3` does, and this keeps
    /// waiting for the next CALL.
    /// </summary>
    public async Task ServeOneCallGatedAsync(CallLookup lookup, PolicyLookup policy, TimeSpan timeout, CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await ServeOneCallInnerAsync(lookup, policy, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("timed out waiting for an inbound CALL");
        }
    }

    private static Policy OpenPolicy(byte[] realm, string procedure) => Policy.Open;

    private async Task ServeOneCallInnerAsync(CallLookup lookup, PolicyLookup policy, CancellationToken ct)
    {
        while (true)
        {
            var value = await RecvAsync(ct).ConfigureAwait(false);
            var reply = await ReplyToFrameAsync(this, value, lookup, policy, Identity, ct).ConfigureAwait(false);
            if (reply is null)
            {
                continue; // not a CALL signed by its caller -- see this method's doc on the limitation
            }
            await SendAsync(reply, ct).ConfigureAwait(false);
            return;
        }
    }

    /// <summary>
    /// The reply to one inbound frame, or null when there is nothing to
    /// answer: the frame isn't a CALL, or its signature doesn't verify
    /// against the caller it names (see
    /// <see cref="CallFrameParsing.ParseSignedCall"/>). Otherwise
    /// <see cref="BuildCallReplyAsync"/>.
    /// </summary>
    internal static async Task<Value.MapValue?> ReplyToFrameAsync(IFrameSink? session, Value frame, CallLookup lookup, PolicyLookup policy, KeyPair identity, CancellationToken ct = default) =>
        CallFrameParsing.ParseSignedCall(frame) is { } callInfo
            ? await BuildCallReplyAsync(session, callInfo, lookup, policy, identity, ct).ConfigureAwait(false)
            : null;

    /// <summary>
    /// Mirrors `macula_station_link.erl`'s `handle_inbound_call/2` +
    /// `safe_invoke_handler/4`: a policy rejection is Unauthorized, before
    /// lookup ever runs; a lookup miss is unknown_next_peer; the handler
    /// running to completion produces a RESULT, or a thrown
    /// <see cref="CallHandlerException"/> produces unknown_error with its
    /// message as `detail`; any OTHER thrown exception (an unexpected
    /// crash) produces temporary_relay_failure with no detail, matching
    /// the reference not sending one on a crash either. Fires
    /// rpc.received_v1/rpc.replied_v1 around dispatch, matching
    /// macula_response.erl exactly: RECEIVED only after policy and lookup
    /// both pass, REPLIED for the success/handler-error outcomes but NOT
    /// for a handler crash -- the reference's own crash-before-publish
    /// omission, matched not "improved."
    ///
    /// `internal`, not `private`, and takes <see cref="IFrameSink"/> rather
    /// than a concrete <see cref="Session"/>: <see cref="StationPool"/>
    /// reuses this exact dispatch logic for an inbound CALL arriving on a
    /// pooled link, rather than forking a second copy of the policy/lookup/
    /// crash-handling semantics that could drift from this one. The method
    /// is static and closure-free (only reads its parameters), so widening
    /// its visibility changes nothing about how it behaves when called
    /// from here.
    /// </summary>
    internal static async Task<Value.MapValue> BuildCallReplyAsync(IFrameSink? session, CallInfo callInfo, CallLookup lookup, PolicyLookup policy, KeyPair identity, CancellationToken ct = default)
    {
        var selfPub = identity.NodeId();
        try
        {
            policy(callInfo.Realm, callInfo.Procedure).Check(callInfo.UcanToken, callInfo.Caller);
        }
        catch (Exception)
        {
            return CallErrorFrame.Build(new CallErrorSpec { CallId = callInfo.CallId, Code = Bolt4Code.Unauthorized, ReportedBy = selfPub });
        }

        var handler = lookup(callInfo.Realm, callInfo.Procedure);
        if (handler is null)
        {
            return CallErrorFrame.Build(new CallErrorSpec { CallId = callInfo.CallId, Code = Bolt4Code.UnknownNextPeer, ReportedBy = selfPub });
        }

        var requestId = RpcFacts.RandomRequestId();
        await RpcFacts.AnnounceReceivedAsync(session, callInfo.Realm, identity, requestId, ct).ConfigureAwait(false);

        try
        {
            var value = await handler(callInfo.Payload).ConfigureAwait(false);
            await RpcFacts.AnnounceRepliedAsync(session, callInfo.Realm, identity, requestId, null, ct).ConfigureAwait(false);
            return ResultFrame.Build(new ResultSpec { CallId = callInfo.CallId, Payload = value, RespondedBy = selfPub });
        }
        catch (CallHandlerException e)
        {
            await RpcFacts.AnnounceRepliedAsync(session, callInfo.Realm, identity, requestId, e.Message, ct).ConfigureAwait(false);
            return CallErrorFrame.Build(new CallErrorSpec { CallId = callInfo.CallId, Code = Bolt4Code.UnknownError, ReportedBy = selfPub, Detail = e.Message });
        }
        catch (Exception)
        {
            // A crash: NOT announced, matching the reference exactly (see
            // this method's doc).
            return CallErrorFrame.Build(new CallErrorSpec { CallId = callInfo.CallId, Code = Bolt4Code.TemporaryRelayFailure, ReportedBy = selfPub });
        }
    }

    /// <summary>Opens a fresh dedicated QUIC stream (streaming RPC session, content transfer).</summary>
    public async Task<FrameStream> OpenDedicatedStreamAsync(CancellationToken ct = default)
    {
        var stream = await _connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, ct).ConfigureAwait(false);
        return new FrameStream(stream);
    }

    /// <summary>
    /// Accepts the next inbound dedicated stream the peer opens toward us
    /// (an advertised procedure's inbound STREAM_OPEN). Blocks until one
    /// arrives.
    /// </summary>
    public async Task<FrameStream> AcceptDedicatedStreamAsync(CancellationToken ct = default)
    {
        var stream = await _connection.AcceptInboundStreamAsync(ct).ConfigureAwait(false);
        return new FrameStream(stream);
    }

    /// <summary>Sends GOODBYE and closes the connection. Idempotent.</summary>
    /// <remarks>
    /// RESOLVED 2026-08-30 (previously flagged as an unverified risk since
    /// 2026-08-29): the Go and Rust ports of this exact method (connect,
    /// write, immediately close the whole connection) both had a real,
    /// confirmed data-loss bug -- their underlying QUIC libraries' Write/
    /// Close only queue data for a background sender and return before
    /// it's on the wire, so a write sent immediately before a hard
    /// connection-close could be silently dropped. The blocker preventing
    /// this from being checked here (live tests believed impossible on
    /// this machine, `System.Net.Quic` unable to load `Unofficial.MsQuic`'s
    /// Linux build) is resolved -- see
    /// `reference_dotnet_quic_gotchas` / `project_macula_dotnet_sdk` memory
    /// for the working `libmsquic` substitution.
    ///
    /// Checked live via `CloseDataLossTests.Publish_immediately_followed_by_close_survives_reliably`:
    /// 40 publish-immediately-followed-by-close attempts across 4 separate
    /// runs (10 per run), ZERO losses. `System.Net.Quic.QuicStream.WriteAsync`
    /// DOES give a stronger completion guarantee than quic-go/quinn's did --
    /// this method needs no equivalent fix. Do not re-add a drain-wait here
    /// without a new reproduction; this finding is based on real live
    /// evidence, not merely "no counter-evidence found."
    /// </remarks>
    public async ValueTask CloseAsync(string reason = "normal", string? detail = null)
    {
        if (_closed)
        {
            return;
        }
        _closed = true;

        try
        {
            await SendAsync(GoodbyeFrame.Build(reason, detail)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best-effort -- the connection may already be unusable if
            // we're closing because of a transport-level failure.
        }

        await _connection.CloseAsync(0).ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);
}

using System.Security.Cryptography;
using System.Text;
using Macula.Connection;
using Macula.Frame;
using Macula.Identity;

namespace Macula.Tests;

/// <summary>
/// The station end of a session's control stream, in memory: reads what the
/// session's channel sends and sends it frames. <see cref="Connect"/> starts a
/// channel talking to a fresh one.
/// </summary>
internal sealed class FakeStation
{
    internal static readonly TimeSpan Wait = TimeSpan.FromSeconds(2);
    internal static readonly byte[] Realm = Enumerable.Repeat((byte)7, 32).ToArray();
    internal static readonly byte[] NodeId = KeyPair.Generate().NodeId();

    private readonly InMemoryPipe _pipe;
    private readonly InMemoryPipe _session;
    private readonly FrameStream _frames;

    private FakeStation(InMemoryPipe pipe, InMemoryPipe session)
    {
        _pipe = pipe;
        _session = session;
        _frames = new FrameStream(pipe);
    }

    /// <summary>A started channel talking to a fresh station, and a source completed with the reason the channel ended.</summary>
    internal static (ControlChannel Channel, FakeStation Station, TaskCompletionSource<Exception> Ended) Connect(KeyPair? identity = null, Action<Exception>? onEnded = null, TimeSpan? sendTimeout = null)
    {
        var (client, station) = InMemoryPipe.CreatePair();
        var ended = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = new ControlChannel(new FrameStream(client), identity ?? KeyPair.Generate(), NodeId, reason =>
        {
            onEnded?.Invoke(reason);
            ended.TrySetResult(reason);
        }, sendTimeout);
        channel.Start();
        return (channel, new FakeStation(station, client), ended);
    }

    /// <summary>A CALL the session makes.</summary>
    internal static CallSpec Call(string procedure) => new()
    {
        CallId = RandomBytes(16),
        Procedure = procedure,
        Realm = Realm,
        Payload = Value.Null,
        DeadlineMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 5_000,
        Caller = KeyPair.Generate().NodeId(),
    };

    /// <summary>A CALL a station relays to the session from caller.</summary>
    internal static CallSpec InboundCall(string procedure, KeyPair caller, long? deadlineMs = null) => new()
    {
        CallId = RandomBytes(16),
        Procedure = procedure,
        Realm = Realm,
        Payload = Value.Null,
        DeadlineMs = deadlineMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 5_000,
        Caller = caller.NodeId(),
    };

    internal static SubscribeSpec Subscribe(string topic) => new() { Topic = topic, Realm = Realm, Subscriber = KeyPair.Generate().NodeId() };

    internal static string ReplyText(CallResponse response) => Assert.IsType<CallResponse.Result>(response).Payload.AsText();

    internal static string TypeOf(Value.MapValue frame) => ((Value.TextValue)frame.Get("frame_type")!).AsText();

    internal static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    internal Task SendAsync(Value.MapValue frame) => _frames.SendFrameAsync(frame);

    /// <summary>Writes bytes as they are, framed or not.</summary>
    internal async Task SendRawAsync(byte[] bytes) => await _pipe.WriteAsync(bytes);

    internal async Task<Value.MapValue> NextFrameAsync() => (Value.MapValue)await _frames.RecvFrameAsync().WaitAsync(Wait);

    internal async Task<Value.MapValue> NextAsync(string frameType)
    {
        while (true)
        {
            var frame = await NextFrameAsync();
            if (frame.Get("frame_type") is Value.TextValue t && t.AsText() == frameType)
            {
                return frame;
            }
        }
    }

    /// <summary>Whether the session sends nothing within window.</summary>
    internal async Task<bool> NothingSentWithinAsync(TimeSpan window)
    {
        using var within = new CancellationTokenSource(window);
        try
        {
            await _frames.RecvFrameAsync(within.Token);
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    internal Task ReplyAsync(Value.MapValue call, string text) =>
        SendAsync(ResultFrame.Build(new ResultSpec { CallId = ((Value.BytesValue)call.Get("call_id")!).Value, Payload = Value.Text(text), RespondedBy = NodeId }));

    internal Task SendEventAsync(string topic, string payload) =>
        SendAsync(Envelope.Base("event", 0, Envelope.FreshFrameId(), Envelope.CurrentMillis())
            .WithField("realm", Value.Bytes(Realm))
            .WithField("topic", Value.Bytes(Encoding.UTF8.GetBytes(topic)))
            .WithField("publisher", Value.Bytes(NodeId))
            .WithField("seq", Value.UInt(1))
            .WithField("payload", Value.Text(payload))
            .WithField("delivered_via", Value.Text("direct")));

    /// <summary>
    /// An inbound CALL as a station relays one, signed by its caller unless
    /// another signer is given. Returns its call_id.
    /// </summary>
    internal async Task<byte[]> SendInboundCallAsync(string procedure, KeyPair? signer = null, long? deadlineMs = null)
    {
        var caller = KeyPair.Generate();
        var call = InboundCall(procedure, caller, deadlineMs);
        await SendAsync(Envelope.Sign(CallFrame.Build(call), signer ?? caller));
        return call.CallId;
    }

    /// <summary>Holds what the session writes, as a station withholding flow-control credit does.</summary>
    internal void StallSessionWrites() => _session.StallWrites();

    internal void ResumeSessionWrites() => _session.ResumeWrites();

    internal void Hangup() => _pipe.Hangup();
}

using System.Text.Json;
using Macula.Native;

namespace Macula;

public sealed partial class Pool
{
    /// <summary>The verified record under <paramref name="key"/>, or null when there is none.</summary>
    public unsafe Task<DhtRecord?> FindRecordAsync(MeshId key, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var timeoutMs = Milliseconds(timeout);
        return NativeCall.RunAsync(token =>
        {
            nint err = 0;
            nint text;
            fixed (byte* k = key.Bytes)
            {
                text = Libmacula.macula_pool_find_record(Handle, k, timeoutMs, token, ref err);
            }
            try
            {
                NativeCall.Check(err, cancellationToken);
            }
            catch (MaculaException e) when (e.Kind == ErrorKind.NotFound)
            {
                return null;
            }
            using var json = JsonDocument.Parse(NativeCall.TakeString(text)!);
            return RecordOf(json.RootElement);
        }, cancellationToken);
    }

    /// <summary>Every verified record under <paramref name="key"/>, and how many did not verify.</summary>
    public unsafe Task<DhtRecords> FindRecordsAsync(MeshId key, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var timeoutMs = Milliseconds(timeout);
        return NativeCall.RunAsync(token =>
        {
            nint err = 0;
            nint text;
            fixed (byte* k = key.Bytes)
            {
                text = Libmacula.macula_pool_find_records(Handle, k, timeoutMs, token, ref err);
            }
            NativeCall.Check(err, cancellationToken);
            return RecordsOf(NativeCall.TakeString(text)!);
        }, cancellationToken);
    }

    /// <summary>Every verified record of <paramref name="type"/> the station holds.</summary>
    public Task<DhtRecords> FindRecordsByTypeAsync(RecordType type, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var timeoutMs = Milliseconds(timeout);
        return NativeCall.RunAsync(token =>
        {
            nint err = 0;
            var text = Libmacula.macula_pool_find_records_by_type(Handle, (int)type, timeoutMs, token, ref err);
            NativeCall.Check(err, cancellationToken);
            return RecordsOf(NativeCall.TakeString(text)!);
        }, cancellationToken);
    }

    /// <summary>Puts a signed record's wire bytes in the DHT.</summary>
    public unsafe Task PutRecordAsync(ReadOnlyMemory<byte> wire, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var timeoutMs = Milliseconds(timeout);
        return NativeCall.RunAsync(token =>
        {
            nint err = 0;
            fixed (byte* w = wire.Span)
            {
                Libmacula.macula_pool_put_record(Handle, w, (nuint)wire.Length, timeoutMs, token, ref err);
            }
            NativeCall.Check(err, cancellationToken);
        }, cancellationToken);
    }

    private static DhtRecords RecordsOf(string text)
    {
        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;
        return new DhtRecords([.. root.GetProperty("records").EnumerateArray().Select(RecordOf)],
            root.GetProperty("dropped").GetInt32());
    }

    private static DhtRecord RecordOf(JsonElement r)
    {
        Payload.TryGetBytes(Payload.FromElement(r.GetProperty("wire")), out var wire);
        return new DhtRecord(r.GetProperty("type").GetInt32(), r.GetProperty("key_id").GetString()!,
            r.GetProperty("created_at").GetInt64(), r.GetProperty("expires_at").GetInt64(),
            Payload.FromElement(r.GetProperty("payload")), wire);
    }
}

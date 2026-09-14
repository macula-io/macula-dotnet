// Measures what macula-dotnet's CBOR decoder allocates and keeps per input
// byte for the shapes that cost most per element, against the v0.4.1 tag.
// Local only, nothing on the network. Usage: dotnet run -c Release

using System.Buffers.Binary;
using Macula.Cbor;

const int elements = 1 << 20;

Measure("list of 1-byte uints", ListOf(elements, _ => [0x00]));
Measure("list of empty lists", ListOf(elements, _ => [0x80]));
Measure("list of empty maps", ListOf(elements, _ => [0xA0]));
Measure("list of 1-byte byte strings", ListOf(elements, _ => [0x41, 0x61]));
Measure("map of 5-byte uint keys to 1-byte uints", MapOf(elements, i => UInt32Key(i)));
Measure("map of 4-byte text keys to 1-byte uints", MapOf(elements, i => TextKey(i)));

static byte[] ListOf(int count, Func<int, byte[]> item)
{
    var head = new byte[] { 0x9A, 0, 0, 0, 0 };
    BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(1), (uint)count);
    return [.. head, .. Enumerable.Range(0, count).SelectMany(item)];
}

static byte[] MapOf(int count, Func<int, byte[]> key)
{
    var head = new byte[] { 0xBA, 0, 0, 0, 0 };
    BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(1), (uint)count);
    return [.. head, .. Enumerable.Range(0, count).SelectMany(i => (byte[])[.. key(i), 0x00])];
}

static byte[] UInt32Key(int i)
{
    var key = new byte[] { 0x1A, 0, 0, 0, 0 };
    BinaryPrimitives.WriteUInt32BigEndian(key.AsSpan(1), (uint)i + 0x10000);
    return key;
}

static byte[] TextKey(int i) => [0x63, (byte)(i >> 16), (byte)(i >> 8), (byte)i];

static void Measure(string shape, byte[] input)
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    var heldBefore = GC.GetTotalMemory(true);
    var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
    var value = CborCodec.Decode(input);
    var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
    var held = GC.GetTotalMemory(true) - heldBefore;
    GC.KeepAlive(value);
    Console.WriteLine($"{shape,-42} input {input.Length,9} B  allocated {allocated / (double)input.Length,6:F1} B/B ({allocated / (double)elements,6:F1} B/element)  held {held / (double)input.Length,6:F1} B/B ({held / (double)elements,6:F1} B/element)");
}

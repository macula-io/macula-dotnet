using System.Text.Json;
using System.Text.Json.Nodes;

namespace Macula;

/// <summary>
/// A payload (a call's arguments and result, a publication, a stream value) is a
/// <see cref="JsonNode"/>, mapped to and from the mesh's CBOR:
/// <list type="bullet">
/// <item>No booleans: the mesh's CBOR has none, so send 0 and 1. A boolean is refused with an
/// <see cref="ArgumentException"/>.</item>
/// <item>Integers are exact over the int64 range, the wire's; one outside it is refused. Read them
/// with <c>GetValue&lt;long&gt;()</c>, never through a double.</item>
/// <item>Bytes are the object <c>{"$bytes": "&lt;base64&gt;"}</c>, both ways: make one with
/// <see cref="Bytes"/>, read one with <see cref="TryGetBytes"/>.</item>
/// <item>Map keys come back in the wire's deterministic order, not the order they were sent in.</item>
/// </list>
/// </summary>
public static class Payload
{
    private const string BytesKey = "$bytes";

    /// <summary>Bytes as a payload value.</summary>
    public static JsonObject Bytes(ReadOnlySpan<byte> bytes) =>
        new() { [BytesKey] = Convert.ToBase64String(bytes) };

    /// <summary>The bytes a payload value carries, when it is a bytes value.</summary>
    public static bool TryGetBytes(JsonNode? value, out byte[] bytes)
    {
        bytes = [];
        if (value is not JsonObject obj || obj.Count != 1 || obj[BytesKey] is not JsonValue text ||
            !text.TryGetValue<string>(out var base64))
        {
            return false;
        }
        bytes = Convert.FromBase64String(base64);
        return true;
    }

    internal static string ToJson(JsonNode? payload) => payload?.ToJsonString() ?? "null";

    internal static JsonNode? FromJson(string json) => JsonNode.Parse(json);

    internal static JsonNode? FromElement(JsonElement element) =>
        element.ValueKind == JsonValueKind.Null ? null : JsonNode.Parse(element.GetRawText());
}

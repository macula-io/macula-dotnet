using System.Text.Json;
using System.Text.Json.Nodes;

namespace Macula;

/// <summary>
/// Whether a call or a stream is sealed (macula 13's E2E seal scheme 1): sealed to the provider's KEM key, so
/// stations route what they cannot read. A sealed call never falls back to the clear.
/// </summary>
public enum Confidential
{
    /// <summary>Sealed whenever the provider's advertisement names a KEM key; one that names none is called in the clear.</summary>
    Preferred,
    /// <summary>Never calls a provider whose advertisement names no key (<see cref="ConfidentialityException"/>, <c>no_kem_key</c>).</summary>
    Required,
}

/// <summary>Whether a served procedure is sealed.</summary>
public enum ServedConfidential
{
    /// <summary>
    /// Names this node's KEM key when the pool was connected with <see cref="PoolOptions.KemAdvertise"/>, and still takes
    /// a clear call while the procedure's last keyless advertisement could be served.
    /// </summary>
    Preferred,
    /// <summary>
    /// Refuses every clear call (<c>sealed_required</c>). Needs <see cref="PoolOptions.KemAdvertise"/>, or serving fails
    /// with a <see cref="ConfidentialityException"/> of reason <c>kem_advertise_disabled</c>.
    /// </summary>
    Required,
    /// <summary>Served in the clear.</summary>
    Off,
}

/// <summary>
/// What a caller's seal report says about the exchange behind a result (macula's DESIGN_E2E_SEAL_REPORT): whether
/// the request was sealed to the provider's advertised KEM key and its answer opened under that key, the provider it
/// was addressed to, and that key's 8-byte id as hex (only when sealed). It states that sealing ran on that exchange,
/// nothing more.
/// </summary>
public sealed record SealReport(bool Sealed, MeshId Provider, string? SealKeyId)
{
    internal static SealReport FromElement(JsonElement r) => new(r.GetProperty("sealed").GetInt32() == 1,
        MeshId.Parse(r.GetProperty("provider").GetString()!),
        r.TryGetProperty("seal_key_id", out var key) ? key.GetString() : null);
}

/// <summary>A call's result and its seal report (<see cref="Pool.CallReportAsync"/>).</summary>
public sealed record Reported(JsonNode? Result, SealReport Report);

internal static class Sealing
{
    internal static string Name(Confidential c) => c switch
    {
        Confidential.Preferred => "preferred",
        Confidential.Required => "required",
        _ => throw new ArgumentOutOfRangeException(nameof(c), c, "a call's confidentiality is Preferred or Required"),
    };

    internal static string Name(ServedConfidential c) => c switch
    {
        ServedConfidential.Preferred => "preferred",
        ServedConfidential.Required => "required",
        ServedConfidential.Off => "off",
        _ => throw new ArgumentOutOfRangeException(nameof(c), c, "a served procedure's confidentiality is Preferred, Required or Off"),
    };

    // A call's or an open's options as macula_pool_call_opts and macula_pool_open_stream_opts take them.
    internal static string CallOptionsJson(MeshId? provider, UcanPresentation? ucan, Confidential? confidential, bool report)
    {
        var json = new JsonObject();
        if (provider is { } p)
        {
            json["provider"] = p.ToString();
        }
        if (ucan is not null)
        {
            ArgumentException.ThrowIfNullOrEmpty(ucan.Token, nameof(ucan));
            json["ucan"] = ucan.Token;
            if (ucan.Proofs is { Count: > 0 } proofs)
            {
                json["proofs"] = new JsonArray([.. proofs.Select(t => (JsonNode?)t)]);
            }
        }
        if (confidential is { } c)
        {
            json["confidential"] = Name(c);
        }
        if (report)
        {
            json["report"] = 1;
        }
        return json.ToJsonString();
    }

    // A served procedure's options as macula_pool_serve_opts and macula_pool_serve_stream_opts take them.
    internal static string ServeOptionsJson(AuthPolicy? policy, ServedConfidential? confidential)
    {
        var json = new JsonObject();
        if (policy is not null)
        {
            json["policy"] = policy.ToJson();
        }
        if (confidential is { } c)
        {
            json["confidential"] = Name(c);
        }
        return json.ToJsonString();
    }
}

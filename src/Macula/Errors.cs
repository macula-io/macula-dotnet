using System.Text.Json;

namespace Macula;

/// <summary>The fixed kind of a libmacula error (macula-go's cabi/CONTRACT.md "Errors").</summary>
public enum ErrorKind
{
    /// <summary>The call's cancellation token was cancelled.</summary>
    Cancelled,
    /// <summary>The call's timeout ran out.</summary>
    Timeout,
    /// <summary>An object that was already disposed.</summary>
    InvalidHandle,
    /// <summary>A malformed id, JSON, profile, mode, size or payload.</summary>
    InvalidArgument,
    /// <summary>The provider answered a call with an error.</summary>
    ProviderError,
    /// <summary>A station could not deliver a call.</summary>
    RelayError,
    /// <summary>No DHT record under that key.</summary>
    NotFound,
    /// <summary>No provider the realm trusts advertises the procedure.</summary>
    NoProvider,
    /// <summary>No node shares that content in that realm.</summary>
    NotShared,
    /// <summary>Every sharer failed to give the content.</summary>
    Unavailable,
    /// <summary>A served call was answered already.</summary>
    Answered,
    /// <summary>The pool, subscription, served procedure or stream has ended.</summary>
    Closed,
    /// <summary>The network refused it.</summary>
    Refused,
    /// <summary>A call, stream or served procedure that could not be kept confidential (<see cref="ConfidentialityException"/>).</summary>
    Confidentiality,
    /// <summary>A stream's seal report asked for before it settled, or of a stream that ended before it did.</summary>
    NotSettled,
    /// <summary>A seal report asked of a served stream: only the caller has one.</summary>
    NotACaller,
    /// <summary>A sealed group's refusal or failure (<see cref="GroupException"/>).</summary>
    Group,
    /// <summary>Anything else.</summary>
    Failed,
}

/// <summary>
/// A failure libmacula reported. Cancellation, a timeout, a malformed argument and a disposed object
/// surface as .NET's own <see cref="OperationCanceledException"/>, <see cref="TimeoutException"/>,
/// <see cref="ArgumentException"/> and <see cref="ObjectDisposedException"/> instead.
/// </summary>
public class MaculaException : Exception
{
    /// <summary>Makes an error of <paramref name="kind"/>.</summary>
    public MaculaException(ErrorKind kind, string message) : base(message) => Kind = kind;

    /// <summary>The error's kind.</summary>
    public ErrorKind Kind { get; }
}

/// <summary>A provider answered a call with an error.</summary>
public sealed class ProviderErrorException(string message, string code, string? detail)
    : MaculaException(ErrorKind.ProviderError, message)
{
    /// <summary>The provider's code, such as <c>handler_error</c>.</summary>
    public string Code { get; } = code;

    /// <summary>What the provider said, when it said anything.</summary>
    public string? Detail { get; } = detail;
}

/// <summary>A station could not deliver a call.</summary>
public sealed class RelayErrorException(string message, string code) : MaculaException(ErrorKind.RelayError, message)
{
    /// <summary>The station's code, such as <c>unknown_next_peer</c>.</summary>
    public string Code { get; } = code;
}

/// <summary>No node shares that content in that realm right now.</summary>
public sealed class NotSharedException(string message) : MaculaException(ErrorKind.NotShared, message);

/// <summary>Every node sharing the content failed to give it.</summary>
public sealed class ContentUnavailableException(string message, IReadOnlyList<string> failures)
    : MaculaException(ErrorKind.Unavailable, message)
{
    /// <summary>Each sharer's failure.</summary>
    public IReadOnlyList<string> Failures { get; } = failures;
}

/// <summary>
/// A call, stream or served procedure that could not be kept confidential (macula 13's E2E seal scheme 1;
/// cabi/CONTRACT.md "Confidentiality"). <see cref="Reason"/>: <c>no_kem_key</c> (the provider names no key
/// where one is required, or one this node cannot seal to), <c>key_mismatch</c> (the provider's advertisement
/// names another key than its refusal did: <see cref="Named"/> and <see cref="Found"/>), <c>reply_not_opened</c>
/// (a sealed answer that does not open), <c>clear_answer_to_sealed</c> (a clear answer that nothing clear may
/// give) or <c>kem_advertise_disabled</c> (serving <see cref="ServedConfidential.Required"/> on a pool without
/// <see cref="PoolOptions.KemAdvertise"/>).
/// </summary>
public sealed class ConfidentialityException(string message, string reason, string? named, string? found)
    : MaculaException(ErrorKind.Confidentiality, message)
{
    /// <summary>Why it could not be kept confidential.</summary>
    public string Reason { get; } = reason;

    /// <summary>The key id the provider's refusal named, as hex, when one did.</summary>
    public string? Named { get; } = named;

    /// <summary>The key id the provider's advertisement names, as hex, when one does.</summary>
    public string? Found { get; } = found;
}

/// <summary>
/// A sealed group's refusal or failure (macula 13.2's sealed groups; cabi/CONTRACT.md "Sealed groups").
/// <see cref="Reason"/> is one from macula's closed set: <c>not_a_member</c>, <c>membership_unknown</c>,
/// <c>unknown_epoch</c>, <c>epoch_expired</c>, <c>no_distributor</c>, <c>no_group</c> or <c>tag_invalid</c>,
/// and <c>group_held</c> for a clear publish under a prefix this node holds a key for, which also names
/// the group's <see cref="Prefix"/> (else null).
/// </summary>
public sealed class GroupException(string message, string reason, string? prefix)
    : MaculaException(ErrorKind.Group, message)
{
    /// <summary>Why the group refused it, or why it failed.</summary>
    public string Reason { get; } = reason;

    /// <summary>The group's prefix, when the reason names one (<c>group_held</c>).</summary>
    public string? Prefix { get; } = prefix;
}

internal static class Errors
{
    // The exception an err_out's JSON stands for.
    internal static Exception FromJson(string text, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        var kind = root.GetProperty("kind").GetString()!;
        var message = root.GetProperty("message").GetString()!;
        return kind switch
        {
            "cancelled" => new OperationCanceledException(message, cancellationToken),
            "timeout" => new TimeoutException(message),
            "invalid_handle" => new ObjectDisposedException(null, message),
            "invalid_argument" => new ArgumentException(message),
            "provider_error" => new ProviderErrorException(message, root.GetProperty("code").GetString()!,
                root.GetProperty("detail").ValueKind == JsonValueKind.Null ? null : root.GetProperty("detail").GetString()),
            "relay_error" => new RelayErrorException(message, root.GetProperty("code").GetString()!),
            "not_shared" => new NotSharedException(message),
            "unavailable" => new ContentUnavailableException(message,
                [.. root.GetProperty("failures").EnumerateArray().Select(f => f.GetString()!)]),
            "not_found" => new MaculaException(ErrorKind.NotFound, message),
            "no_provider" => new MaculaException(ErrorKind.NoProvider, message),
            "answered" => new MaculaException(ErrorKind.Answered, message),
            "closed" => new MaculaException(ErrorKind.Closed, message),
            "refused" => new MaculaException(ErrorKind.Refused, message),
            "confidentiality" => new ConfidentialityException(message, root.GetProperty("reason").GetString()!,
                TextOf(root, "named"), TextOf(root, "found")),
            "not_settled" => new MaculaException(ErrorKind.NotSettled, message),
            "not_a_caller" => new MaculaException(ErrorKind.NotACaller, message),
            "group" => new GroupException(message, root.GetProperty("reason").GetString()!, TextOf(root, "prefix")),
            _ => new MaculaException(ErrorKind.Failed, message),
        };
    }

    // A field that carries a string when the native layer has one: a key id, a
    // named or found key, a group's prefix. Null when it is null or absent.
    private static string? TextOf(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

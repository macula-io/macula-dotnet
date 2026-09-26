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
            "answered" => new MaculaException(ErrorKind.Answered, message),
            "closed" => new MaculaException(ErrorKind.Closed, message),
            "refused" => new MaculaException(ErrorKind.Refused, message),
            _ => new MaculaException(ErrorKind.Failed, message),
        };
    }
}

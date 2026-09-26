using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Macula.Native;

// How every call into libmacula is made: its error turned into an exception,
// the strings and buffers it returns copied and freed, and a blocking call run
// on a thread of its own with the CancellationToken wired to a libmacula
// cancel token (cabi/CONTRACT.md "Threads and blocking", "Cancellation").
internal static class NativeCall
{
    // Every way into libmacula passes here first (a key or a pool is made
    // through NativeCall, and every handle comes from one), so the resolver is
    // in place, and the ABI checked, before any call into it.
    static NativeCall()
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeCall).Assembly, Resolve);
        var version = Libmacula.macula_abi_version();
        if (version != Libmacula.AbiVersion)
        {
            throw new MaculaException(ErrorKind.Failed,
                $"libmacula is ABI {version}, and this Macula package is written against ABI {Libmacula.AbiVersion}");
        }
    }

    // Loads the ABI check on first use.
    internal static void EnsureLoaded() { }

    // libmacula's file for this runtime, as the package lays it out. NuGet
    // resolves runtimes/<rid>/native for a package reference; a project that
    // builds against this one directly gets the files under its own output's
    // runtimes/ directory, which the runtime does not search on its own.
    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != Libmacula.Library)
        {
            return 0;
        }
        if (NativeLibrary.TryLoad(name, assembly, searchPath, out var loaded))
        {
            return loaded;
        }
        var file = OperatingSystem.IsWindows() ? "macula.dll"
            : OperatingSystem.IsMacOS() ? "libmacula.dylib"
            : "libmacula.so";
        var path = Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", file);
        return NativeLibrary.TryLoad(path, out loaded) ? loaded : 0;
    }

    // Throws the error an err_out holds, and frees it; nothing when it is 0.
    internal static void Check(nint err, CancellationToken cancellationToken = default)
    {
        if (err == 0)
        {
            return;
        }
        var text = TakeString(err)!;
        throw Errors.FromJson(text, cancellationToken);
    }

    // The string a function returned, freed; null for NULL.
    internal static string? TakeString(nint text)
    {
        if (text == 0)
        {
            return null;
        }
        try
        {
            return Marshal.PtrToStringUTF8(text);
        }
        finally
        {
            Libmacula.macula_free_string(text);
        }
    }

    // The bytes a function returned, freed; empty for NULL.
    internal static byte[] TakeBytes(nint bytes, nuint length)
    {
        if (bytes == 0)
        {
            return [];
        }
        try
        {
            var copy = new byte[checked((int)length)];
            Marshal.Copy(bytes, copy, 0, copy.Length);
            return copy;
        }
        finally
        {
            Libmacula.macula_free_bytes(bytes);
        }
    }

    // Runs a blocking native call off the caller's thread, handing it the raw
    // cancel token that cancellationToken cancels (0 when it cannot be
    // cancelled). A one-shot call is bounded by its timeout, so it runs on the
    // thread pool; what waits indefinitely (a subscription, a served
    // procedure, a stream) has a pump thread of its own (NativePump).
    internal static Task<T> RunAsync<T>(Func<nuint, T> call, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureLoaded();
        return Task.Run(() => WithCancel(call, cancellationToken), cancellationToken);
    }

    // Calls call with a raw cancel token that cancellationToken cancels.
    internal static T WithCancel<T>(Func<nuint, T> call, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            return call(0);
        }
        // The registration is disposed first, which waits out a running
        // callback, so the token is never cancelled after it is freed.
        using var token = Libmacula.macula_cancel_new();
        using var registration = cancellationToken.Register(static state =>
            Libmacula.macula_cancel((CancelHandle)state!), token);
        return call(token.Value);
    }

    internal static Task RunAsync(Action<nuint> call, CancellationToken cancellationToken) =>
        RunAsync<bool>(token =>
        {
            call(token);
            return true;
        }, cancellationToken);

    // The JSON text libmacula returned, parsed.
    internal static JsonDocument ParseJson(string text) => JsonDocument.Parse(text);
}

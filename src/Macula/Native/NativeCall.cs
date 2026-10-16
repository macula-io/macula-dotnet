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
    // The resolver is registered here, before any member of this type can be
    // reached, so no call into libmacula finds the runtime without one.
    // Nothing that can fail belongs in a static constructor: every way into
    // libmacula passes through EnsureLoaded first (a key or a pool is made
    // through NativeCall, and every handle comes from one), and what that
    // throws reaches the caller as itself (macula-dotnet#12).
    static NativeCall()
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeCall).Assembly, Resolve);
    }

    // The library, loaded and checked once, on the first call into it. Held as
    // a Lazy rather than run in the static constructor above so that what the
    // checks throw is what the caller sees: a static constructor that threw
    // arrives as TypeInitializationException, with the MaculaException one
    // level down in InnerException, and the message that says what to do
    // (wrong ABI, a wire this package does not speak, libmacula too old to say
    // which wire it speaks) is not the one on screen (macula-dotnet#12). The
    // second caller gets that same MaculaException rather than a second
    // attempt at a load that has already failed.
    private static readonly Lazy<bool> Loaded = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    // Loads and checks libmacula on first use.
    internal static void EnsureLoaded() => _ = Loaded.Value;

    // The load and the checks, once; true when the library may be used. What
    // this throws, the caller gets (EnsureLoaded).
    private static bool Load()
    {
        // libmacula's Go runtime sets its signal handlers while the library
        // loads (its constructor, inside dlopen); the load must leave the
        // runtime's suspension signal as it found it (SuspensionSignal). The
        // library is loaded here, the way every call finds it (the resolver, not
        // NativeLibrary.Load, which does not consult it), so the registration is
        // put back before anything else can fail or run.
        var suspension = SuspensionSignal.Save();
        var library = Resolve(Libmacula.Library, typeof(NativeCall).Assembly, null);
        suspension?.Restore();
        var version = Libmacula.macula_abi_version();
        if (version != Libmacula.AbiVersion)
        {
            throw new MaculaException(ErrorKind.Failed,
                $"libmacula is ABI {version}, and this Macula package is written against ABI {Libmacula.AbiVersion}");
        }
        // Which wire the library links, said by the library itself rather than
        // inferred from the functions it happens to export (cabi/CONTRACT.md
        // "Version", macula-go#18, #30): another major reaches no station this
        // package can speak with, so it is refused here, naming both.
        if (library == 0 || !NativeLibrary.TryGetExport(library, "macula_wire_version", out _))
        {
            throw new MaculaException(ErrorKind.Failed,
                $"libmacula does not say which wire it speaks: this Macula package needs macula-go {Libmacula.LibraryFloor}'s library or later");
        }
        var wire = Marshal.PtrToStringUTF8(Libmacula.macula_wire_version());
        if (!Libmacula.AcceptsWire(wire))
        {
            var release = Marshal.PtrToStringUTF8(Libmacula.macula_library_version());
            throw new MaculaException(ErrorKind.Failed,
                $"libmacula is {release} and speaks wire {wire}, and this Macula package speaks wire {Libmacula.WireVersion}");
        }
        // What LibraryFloor stands for, asked of the library rather than
        // assumed from its number: v0.27.0-rc.1 and rc.2 are ABI 1 and speak
        // this wire, so both checks above pass them, and neither has a sealed
        // group call at all. Without this they load and die on the first
        // publish or subscribe under a group with an
        // EntryPointNotFoundException instead of a MaculaException naming the
        // release. Asked here because the load path is where a library that
        // cannot do the job is refused, and one symbol answers for the
        // capability, where a release number could not be compared without
        // inventing an ordering that "devel" does not have.
        if (library == 0 || !NativeLibrary.TryGetExport(library, Libmacula.FloorExport, out _))
        {
            var release = Marshal.PtrToStringUTF8(Libmacula.macula_library_version());
            throw new MaculaException(ErrorKind.Failed,
                $"libmacula is {release} and has no {Libmacula.FloorExport}: this Macula package needs macula-go {Libmacula.LibraryFloor}'s library or later");
        }
        return true;
    }

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
        var file = OperatingSystem.IsWindows() ? "libmacula.dll"
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

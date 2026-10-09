using System.Runtime.InteropServices;

namespace Macula.Native;

// libmacula is Go. Go's runtime, started inside a process that is not Go (a
// c-shared or c-archive library), adds SA_ONSTACK to every signal handler it
// does not own (runtime.initsig, setsigstack), so that a foreign handler that
// lands on a Go thread runs on the alternate signal stack instead of a small
// goroutine stack. On Linux one of those handlers is the runtime's own
// thread-suspension signal, SIGRTMIN, which the garbage collector sends to a
// managed thread to stop it. With SA_ONSTACK that handler runs on the thread's
// small alternate signal stack, and under collection load a thread resumes
// with corrupted registers: the process dies with a general-protection fault
// in whatever managed code was running (GHSA-49xj-cmc6-whqc, macula-dotnet#7).
//
// The runtime sends SIGRTMIN only to a managed thread it is suspending, which
// runs on its own thread stack, never on a goroutine stack, so the alternate
// stack buys nothing there. Around libmacula's first load, this keeps the
// runtime's registration of that one signal as the runtime made it: the whole
// sigaction is saved before, and put back after, unless another handler has
// replaced it meanwhile. Every other signal keeps Go's SA_ONSTACK.
internal sealed class SuspensionSignal
{
    // More than any libc's struct sigaction (152 bytes on x86-64 and arm64 glibc).
    private const int ActionSize = 512;

    private readonly int _signal;
    private readonly byte[] _action;

    private SuspensionSignal(int signal, byte[] action)
    {
        _signal = signal;
        _action = action;
    }

    // The runtime's registration of its suspension signal now, on Linux; null
    // elsewhere, or when libc does not say which signal it is.
    internal static SuspensionSignal? Save()
    {
        if (!OperatingSystem.IsLinux() || !TryCurrentSigrtmin(out var signal))
        {
            return null;
        }
        var action = Read(signal);
        return action is null ? null : new SuspensionSignal(signal, action);
    }

    // Puts the saved registration back when the handler is still the one saved,
    // so only the flags another runtime added since are undone.
    internal void Restore()
    {
        var now = Read(_signal);
        if (now is null || Handler(now) != Handler(_action))
        {
            return;
        }
        var saved = Marshal.AllocHGlobal(ActionSize);
        try
        {
            Marshal.Copy(_action, 0, saved, ActionSize);
            _ = Libc.sigaction(_signal, saved, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(saved);
        }
    }

    private static byte[]? Read(int signal)
    {
        var buffer = Marshal.AllocHGlobal(ActionSize);
        try
        {
            if (Libc.sigaction(signal, IntPtr.Zero, buffer) != 0)
            {
                return null;
            }
            var action = new byte[ActionSize];
            Marshal.Copy(buffer, action, 0, ActionSize);
            return action;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // A struct sigaction opens with its handler, in every libc.
    private static nint Handler(byte[] action) =>
        IntPtr.Size == 8 ? (nint)BitConverter.ToInt64(action, 0) : BitConverter.ToInt32(action, 0);

    private static bool TryCurrentSigrtmin(out int signal)
    {
        try
        {
            signal = Libc.__libc_current_sigrtmin();
            return true;
        }
        catch (EntryPointNotFoundException)
        {
            signal = 0;
            return false;
        }
    }

    private static class Libc
    {
        [DllImport("libc", SetLastError = true)]
        internal static extern int sigaction(int signal, IntPtr action, IntPtr oldAction);

        [DllImport("libc")]
        internal static extern int __libc_current_sigrtmin();
    }
}

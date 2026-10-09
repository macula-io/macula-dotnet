using System.Runtime.InteropServices;

namespace Macula.Tests;

/// <summary>
/// libmacula is Go, and Go's runtime, started inside this process, adds SA_ONSTACK to the signal handlers it does
/// not own. One of them is .NET's thread-suspension signal (SIGRTMIN on Linux): its handler then runs on a thread's
/// small alternate signal stack, and under garbage-collection load a thread resumes with corrupted registers and the
/// process dies (GHSA-49xj-cmc6-whqc, macula-dotnet#7). Loading libmacula leaves .NET's registration as it was.
/// </summary>
public sealed class SignalStackTests
{
    private const int SaOnStack = 0x08000000;

    [DllImport("libc")]
    private static extern int sigaction(int sig, IntPtr act, IntPtr oldact);

    [DllImport("libc")]
    private static extern int __libc_current_sigrtmin();

    [Fact]
    public async Task LoadingLibmaculaLeavesTheSuspensionSignalOffTheAlternateStack()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var key = await NodeKey.GenerateAsync(Profile.PqPure);
        // struct sigaction: the handler, the 128-byte sa_mask, then sa_flags.
        var action = Marshal.AllocHGlobal(256);
        try
        {
            Assert.Equal(0, sigaction(__libc_current_sigrtmin(), IntPtr.Zero, action));
            var flags = Marshal.ReadInt32(action, IntPtr.Size + 128);
            Assert.True((flags & SaOnStack) == 0, $"SIGRTMIN's flags are 0x{flags:x}: SA_ONSTACK is set");
        }
        finally
        {
            Marshal.FreeHGlobal(action);
        }
    }
}

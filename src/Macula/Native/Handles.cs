using System.Runtime.InteropServices;

namespace Macula.Native;

// Every libmacula handle a caller frees is a SafeHandle: the runtime holds a
// reference while a native call uses it, so disposing one on another thread
// cannot free it mid-call, and one never disposed is still freed by the
// finalizer. 0 is never a handle (cabi/CONTRACT.md "Handles").
internal abstract class MaculaHandle : SafeHandle
{
    protected MaculaHandle() : base(0, ownsHandle: true) { }

    public override bool IsInvalid => handle == 0;

    internal nuint Value => (nuint)handle;
}

internal sealed class KeyHandle : MaculaHandle
{
    protected override bool ReleaseHandle()
    {
        Libmacula.macula_key_free(Value);
        return true;
    }
}

internal sealed class PoolHandle : MaculaHandle
{
    protected override bool ReleaseHandle()
    {
        Libmacula.macula_pool_close(Value);
        return true;
    }
}

internal sealed class SubscriptionHandle : MaculaHandle
{
    protected override bool ReleaseHandle()
    {
        Libmacula.macula_subscription_stop(Value);
        return true;
    }
}

internal sealed class ServedHandle : MaculaHandle
{
    protected override bool ReleaseHandle()
    {
        nint err = 0;
        Libmacula.macula_served_stop(Value, ref err);
        if (err != 0)
        {
            Libmacula.macula_free_string(err);
        }
        return true;
    }
}

internal sealed class StreamHandle : MaculaHandle
{
    public StreamHandle() { }

    // A served stream session arrives as a bare handle from served_next.
    internal StreamHandle(nuint raw) => SetHandle((nint)raw);

    protected override bool ReleaseHandle()
    {
        Libmacula.macula_stream_free(Value);
        return true;
    }
}

internal sealed class CancelHandle : MaculaHandle
{
    protected override bool ReleaseHandle()
    {
        Libmacula.macula_cancel_free(Value);
        return true;
    }
}

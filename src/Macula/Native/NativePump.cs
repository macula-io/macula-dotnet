using System.Threading.Channels;

namespace Macula.Native;

// A thread of its own that drains one libmacula inbox (a subscription's
// events, a served procedure's calls, a stream's frames) into a bounded
// channel. When the channel is full the pump waits, so it stops taking from
// libmacula and the inbox's own policy applies (cabi/CONTRACT.md "Inboxes").
// Stopping cancels the native wait it is in, and waits for the thread to end.
internal sealed class NativePump<T> : IAsyncDisposable
{
    // What one turn of the pump found.
    internal readonly record struct Taken(T Item, bool Ended);

    private readonly Func<nuint, Taken> _next;
    private readonly Func<T, bool>? _stopAfter;
    private readonly Channel<T> _channel;
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;
    private Exception? _failure;

    // next takes one item, blocking with the raw cancel token it is given;
    // Ended means the source ended. stopAfter, when given, names an item that
    // is the last: it is handed over, and the pump ends. An exception ends the
    // pump and is rethrown to the reader.
    internal NativePump(string name, int capacity, Func<nuint, Taken> next, Func<T, bool>? stopAfter = null)
    {
        _next = next;
        _stopAfter = stopAfter;
        _channel = Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
        {
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.Start();
    }

    internal ChannelReader<T> Reader => _channel.Reader;

    private void Run()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var taken = NativeCall.WithCancel(_next, _stop.Token);
                if (taken.Ended)
                {
                    break;
                }
                if (!_channel.Writer.WaitToWriteAsync(_stop.Token).AsTask().GetAwaiter().GetResult())
                {
                    break;
                }
                _channel.Writer.TryWrite(taken.Item);
                if (_stopAfter?.Invoke(taken.Item) == true)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            _failure = e;
        }
        finally
        {
            _channel.Writer.TryComplete(_failure);
        }
    }

    private int _disposed;

    // Idempotent, as a dispose must be: the owner disposing twice (an explicit DisposeAsync inside an await using)
    // stops the pump once.
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }
        await _stop.CancelAsync().ConfigureAwait(false);
        await Task.Run(_thread.Join).ConfigureAwait(false);
        _stop.Dispose();
    }
}

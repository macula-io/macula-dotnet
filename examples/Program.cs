namespace Macula.Examples;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var name = args.Length > 0 ? args[0] : "";
        Func<Task>? example = name switch
        {
            "00" or "quickstart" => Quickstart.RunAsync,
            "01" or "handshake" => Handshake.RunAsync,
            "02" or "call" => Call.RunAsync,
            "03" or "pubsub" => PublishSubscribe.RunAsync,
            "04" or "content" => Content.RunAsync,
            "05" or "stream" => StreamOpenCaller.RunAsync,
            "06" or "rpc-provider" => RpcProvider.RunAsync,
            "07" or "stream-provider" => StreamProvider.RunAsync,
            "08" or "errors" => ErrorHandling.RunAsync,
            "09" or "long-running-provider" => LongRunningProvider.RunAsync,
            "10" or "direct-dial" => DirectDialExample.RunAsync,
            "11" or "ucan" => UcanExample.RunAsync,
            _ => null,
        };

        if (example is null)
        {
            Console.Error.WriteLine("Usage: dotnet run --project examples -- <example>");
            Console.Error.WriteLine("  00 | quickstart             advertise + call its own trivial echo procedure");
            Console.Error.WriteLine("  01 | handshake              identity + connect + close");
            Console.Error.WriteLine("  02 | call                   unary RPC caller");
            Console.Error.WriteLine("  03 | pubsub                 publish/subscribe/event");
            Console.Error.WriteLine("  04 | content                content transfer (put/get)");
            Console.Error.WriteLine("  05 | stream                 streaming RPC, caller role");
            Console.Error.WriteLine("  06 | rpc-provider           unary RPC, provider role (two sessions)");
            Console.Error.WriteLine("  07 | stream-provider        streaming RPC, provider role (two sessions)");
            Console.Error.WriteLine("  08 | errors                 every error shape this SDK produces");
            Console.Error.WriteLine("  09 | long-running-provider  a provider serving many calls, not just one");
            Console.Error.WriteLine("  10 | direct-dial            resolve+dial a provider via its DHT advertisement");
            Console.Error.WriteLine("  11 | ucan                   mint a token, gate a served procedure by policy");
            return 1;
        }

        await example();
        return 0;
    }
}

using Macula.Examples;

// dotnet run --project examples -- <example>
var examples = new Dictionary<string, Func<CancellationToken, Task>>
{
    ["quickstart"] = Quickstart.RunAsync,
    ["call"] = CallAService.RunAsync,
    ["serve"] = ServeAndCall.RunAsync,
    ["pubsub"] = PublishSubscribe.RunAsync,
    ["stream"] = Streams.RunAsync,
    ["content"] = ShareContent.RunAsync,
    ["dht"] = ReadTheDht.RunAsync,
    ["errors"] = ErrorHandling.RunAsync,
};

if (args.Length != 1 || !examples.TryGetValue(args[0], out var run))
{
    Console.Error.WriteLine($"usage: dotnet run --project examples -- <{string.Join('|', examples.Keys)}>");
    return 2;
}

using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(2));
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};
await run(cancel.Token);
return 0;

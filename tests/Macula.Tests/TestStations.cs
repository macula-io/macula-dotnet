using System.Diagnostics;
using System.Text.Json;

namespace Macula.Tests;

/// <summary>
/// Two in-process macula 12 stations sharing one DHT, and a test realm with one org, run by macula-go's
/// teststation harness (cabi/CONTRACT.md "Test harness"). With MACULA_GO_DIR set, the harness runs from
/// that checkout; otherwise from the macula-go version in libmacula.version.
/// </summary>
public sealed class TestStations : IAsyncLifetime
{
    private Process? _harness;
    private readonly SemaphoreSlim _commands = new(1, 1);

    public IReadOnlyList<Seed> Seeds { get; private set; } = [];
    public MeshId Realm { get; private set; }
    public byte[] RealmKey { get; private set; } = [];
    public string Org { get; private set; } = "";
    public string KeyDirectory { get; } = Directory.CreateTempSubdirectory("macula-dotnet-keys-").FullName;

    public async Task InitializeAsync()
    {
        var goDir = Environment.GetEnvironmentVariable("MACULA_GO_DIR");
        var start = new ProcessStartInfo("go")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (goDir is not null)
        {
            start.WorkingDirectory = goDir;
            start.ArgumentList.Add("run");
            start.ArgumentList.Add("./teststation/cmd/teststation");
        }
        else
        {
            var version = File.ReadAllText(Path.Combine(RepositoryRoot(), "libmacula.version")).Trim();
            start.ArgumentList.Add("run");
            start.ArgumentList.Add($"github.com/macula-io/macula-go/teststation/cmd/teststation@{version}");
        }
        start.ArgumentList.Add("pq_pure");
        _harness = Process.Start(start) ?? throw new InvalidOperationException("the teststation harness did not start");
        // Its stderr (go's own output, a harness failure) is kept, to say why
        // it printed nothing when it does not start.
        var stderr = _harness.StandardError.ReadToEndAsync();
        var line = await _harness.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromMinutes(5));
        if (line is null)
        {
            throw new InvalidOperationException($"the teststation harness printed nothing: {await stderr}");
        }
        using var json = JsonDocument.Parse(line);
        var root = json.RootElement;
        Seeds = [.. root.GetProperty("stations").EnumerateArray().Select(s => new Seed(
            s.GetProperty("host").GetString()!, s.GetProperty("port").GetUInt16(),
            MeshId.Parse(s.GetProperty("node_id").GetString()!)))];
        Realm = MeshId.Parse(root.GetProperty("realm_id").GetString()!);
        RealmKey = Convert.FromHexString(root.GetProperty("realm_key").GetString()!);
        Org = root.GetProperty("org").GetString()!;
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "libmacula.version")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("no libmacula.version above the test output");
    }

    /// <summary>The org delegates its procedures to <paramref name="node"/>.</summary>
    public async Task AdmitAsync(MeshId node)
    {
        await _commands.WaitAsync();
        try
        {
            await _harness!.StandardInput.WriteLineAsync($"admit {node}");
            await _harness.StandardInput.FlushAsync();
            var answer = await _harness.StandardOutput.ReadLineAsync();
            Assert.Equal($"admitted {node}", answer);
        }
        finally
        {
            _commands.Release();
        }
    }

    /// <summary>A pool of a node named <paramref name="name"/>, trusting the test realm, linked to one station.</summary>
    public async Task<Pool> JoinAsync(string name, int station = 0)
    {
        using var key = await NodeKey.LoadOrCreateAsync(Path.Combine(KeyDirectory, name + ".key"), Profile.PqPure);
        return await Pool.ConnectAsync(key, [Seeds[station]], new PoolOptions
        {
            RealmTrust = new Dictionary<MeshId, byte[]> { [Realm] = RealmKey },
            RespawnDelay = TimeSpan.FromMilliseconds(100),
            ConnectTimeout = TimeSpan.FromSeconds(20),
        });
    }

    public async Task DisposeAsync()
    {
        if (_harness is not null)
        {
            _harness.StandardInput.Close();
            if (!_harness.WaitForExit(10_000))
            {
                _harness.Kill(entireProcessTree: true);
            }
            _harness.Dispose();
        }
        Directory.Delete(KeyDirectory, recursive: true);
        await Task.CompletedTask;
    }
}

[CollectionDefinition(Name)]
public sealed class StationsCollection : ICollectionFixture<TestStations>
{
    public const string Name = "stations";
}

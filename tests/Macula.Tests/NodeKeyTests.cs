using System.Text;

namespace Macula.Tests;

public sealed class NodeKeyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("macula-dotnet-key-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task AKeyIsCreatedOnceThenLoaded()
    {
        var path = Path.Combine(_dir, "node.key");
        using var created = await NodeKey.LoadOrCreateAsync(path, Profile.PqPure);
        using var loaded = await NodeKey.LoadOrCreateAsync(path, Profile.PqPure);
        using var again = NodeKey.Load(path, Profile.PqPure);
        Assert.Equal(created.NodeId, loaded.NodeId);
        Assert.Equal(created.NodeId, again.NodeId);
        Assert.Equal(Profile.PqPure, loaded.Profile);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
    }

    [Fact]
    public async Task AKeySignsAndItsSignatureVerifies()
    {
        using var key = await NodeKey.GenerateAsync(Profile.PqHybrid);
        var message = Encoding.UTF8.GetBytes("macula");
        var signature = key.Sign(message);
        Assert.True(NodeKey.Verify(message, signature, key.PublicKey, Profile.PqHybrid));
        Assert.False(NodeKey.Verify(Encoding.UTF8.GetBytes("maculb"), signature, key.PublicKey, Profile.PqHybrid));
        Assert.False(NodeKey.Verify(message, signature, key.PublicKey, Profile.PqPure));
    }

    [Fact]
    public async Task AKeyFileOthersCanReadIsRefusedNotReplaced()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var path = Path.Combine(_dir, "exposed.key");
        using (await NodeKey.LoadOrCreateAsync(path, Profile.PqPure))
        {
        }
        var before = File.ReadAllBytes(path);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        await Assert.ThrowsAsync<MaculaException>(() => NodeKey.LoadOrCreateAsync(path, Profile.PqPure));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task GeneratingAKeyCanBeCancelled()
    {
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NodeKey.GenerateAsync(cancellationToken: cancel.Token));
    }
}

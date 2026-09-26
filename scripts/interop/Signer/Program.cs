// Signs what scripts/interop hands to the verifiers themselves:
//
//   Signer ownership <teststation binary> <capture binary> <out file>
//     an ownership proof (v2, mcl-om#7) over a payload of every CBOR type, sent the real way to capture
//     (this binding's JSON, libmacula's CBOR, a signed CALL, a station, a provider's verification), which
//     writes the payload as its provider received it, for macula-go's erlang_ownership_proof.escript verify.
//   Signer device-request <out file>
//     a join session's body and its realm proof v2 (macula-realm#29), for realm_device_request.exs.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Macula;

var realm = new MeshId(SHA256.HashData("io.macula"u8));
const string Procedure = "mcl-graph/learn_link";

return args switch
{
    ["ownership", var teststation, var capture, var output] => await Ownership(teststation, capture, output),
    ["device-request", var output] => await DeviceRequest(output),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("usage: Signer ownership <teststation> <capture> <out> | device-request <out>");
    return 2;
}

async Task<int> Ownership(string teststation, string capture, string output)
{
    using var stations = Process.Start(new ProcessStartInfo(teststation, "pq_hybrid")
    {
        RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false,
    })!;
    using var info = JsonDocument.Parse((await stations.StandardOutput.ReadLineAsync())!);
    var seeds = info.RootElement.GetProperty("stations").EnumerateArray().Select(s => new Seed(
        s.GetProperty("host").GetString()!, s.GetProperty("port").GetUInt16(),
        MeshId.Parse(s.GetProperty("node_id").GetString()!))).ToList();
    try
    {
        var s0 = seeds[0];
        using var provider = Process.Start(new ProcessStartInfo(capture)
        {
            ArgumentList = { "-station", $"{s0.Host}:{s0.Port}@{s0.NodeId}", "-profile", "pq_hybrid", "-out", output,
                "-realm", realm.ToString(), "-procedure", Procedure },
            RedirectStandardOutput = true, UseShellExecute = false,
        })!;
        var captureId = (await provider.StandardOutput.ReadLineAsync())!.Trim();
        using var key = await NodeKey.GenerateAsync(Profile.PqHybrid);
        await using var caller = await Pool.ConnectAsync(key, [seeds[1]]);
        // Every type a payload carries; the escript changes "weight" to check a changed field is refused.
        var fields = new JsonObject
        {
            ["subject"] = "entity:alpha", ["predicate"] = "knows", ["object"] = "entity:beta", ["confidence"] = 0.75,
            ["weight"] = 3, ["offset"] = -7, ["digest"] = Payload.Bytes([1, 2, 3]), ["note"] = null,
            ["tags"] = new JsonArray("a", "b"), ["metadata"] = new JsonObject { ["source"] = "field-notes", ["page"] = 12 },
        };
        var signed = key.OwnershipProof(realm, Procedure, fields);
        // A sender may write a caller; the station link replaces it with the one it authenticated.
        signed["caller"] = "written by the sender";
        var deadline = DateTime.UtcNow.AddSeconds(20);
        JsonNode? reply;
        while (true)
        {
            try
            {
                reply = await caller.CallAsync(new MeshId(new byte[32]), $"~{captureId}/capture", signed);
                break;
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(200); // the advertisement reaches the other station in its own time
            }
        }
        if (reply?.GetValue<string>() != "captured")
        {
            throw new InvalidOperationException($"capture answered {reply}");
        }
        await provider.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        if (provider.ExitCode != 0)
        {
            throw new InvalidOperationException("capture failed");
        }
        Console.WriteLine($".NET-signed payload captured by {captureId[..16]}: {output}");
        return 0;
    }
    finally
    {
        stations.StandardInput.Close();
        await stations.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }
}

async Task<int> DeviceRequest(string output)
{
    using var key = await NodeKey.GenerateAsync(Profile.PqHybrid);
    var body = new JsonObject
    {
        ["public_key"] = Convert.ToHexStringLower(key.PublicKey),
        ["device_info"] = new JsonObject { ["hostname"] = "dotnet.local", ["note"] = null },
        ["n"] = 2,
    }.ToJsonString();
    var proof = key.DeviceRequestProof(realm, DeviceRequestProofs.JoinSession, body);
    await File.WriteAllLinesAsync(output, [Convert.ToHexStringLower(key.PublicKey), realm.ToString(),
        DeviceRequestProofs.JoinSession, proof.ToJsonString(), body]);
    Console.WriteLine($".NET-signed join session for {key.NodeId.ToString()[..16]}: {output}");
    return 0;
}

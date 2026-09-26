using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Macula.Tests;

/// <summary>
/// The device request proof (realm proof v2, macula-realm#29) and the ownership proof (v2, mcl-om#7): the
/// bytes each signs are the verifier's own, byte for byte (fixtures/), a proof made here verifies over them
/// and over nothing else, and what a verifier would refuse is refused at the source. No station is needed.
/// </summary>
public sealed class ProofTests : IAsyncLifetime
{
    private static readonly Dictionary<string, string> Pinned = new()
    {
        ["device_request/message.hex"] = "e35e0bf847bd0a8c41ae5e96c4e128f67e318c28a752a13325df10fc4e68fc93",
        ["ownership_proof/message.hex"] = "d03c2ac3fac87b1361e26261457bfd14a349d7e2131c25feaf3c8fc34a336f7b",
        ["ownership_proof/identity.hex"] = "b265848773695fc72f4f589517c17bcabcfaac1d6fba03577a225371d1df6fdb",
    };

    private static readonly MeshId IoMacula = new(SHA256.HashData("io.macula"u8));
    private const string LearnLink = "mcl-graph/learn_link";

    private NodeKey _key = null!;

    public async Task InitializeAsync() => _key = await NodeKey.GenerateAsync(Profile.PqPure);

    public Task DisposeAsync()
    {
        _key.Dispose();
        return Task.CompletedTask;
    }

    internal static byte[] FixtureHex(string name)
    {
        var data = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", name));
        Assert.True(Convert.ToHexStringLower(SHA256.HashData(data)) == Pinned[name], $"{name} drifted from the pinned bytes");
        return Convert.FromHexString(Encoding.ASCII.GetString(data).Trim());
    }

    // The realm's vector: a carried key of 2592 bytes of 0x07, io.macula, the join session, at
    // 1790000000000 with a zero nonce, over this body.
    private const string VectorBody =
        """{"device_info": {"hostname": "laptop.local", "note": null}, "n": 1e2, "z": -0.0, "f": 1.5}""";

    private static byte[] Sevens() => Enumerable.Repeat((byte)7, 2592).ToArray();

    [Fact]
    public void TheDeviceRequestMessageIsTheRealmsVector()
    {
        var message = DeviceRequestProofs.Message(Sevens(), IoMacula, DeviceRequestProofs.JoinSession, 1790000000000,
            new byte[16], VectorBody);
        Assert.Equal(FixtureHex("device_request/message.hex"), message);
    }

    [Fact]
    public void AJsonObjectIsSignedAsItsJsonUnderTheRealmsRule()
    {
        var body = JsonNode.Parse(VectorBody)!.AsObject();
        var message = DeviceRequestProofs.Message(Sevens(), IoMacula, DeviceRequestProofs.JoinSession, 1790000000000,
            new byte[16], body, DeviceRequestRule.Http);
        Assert.Equal(FixtureHex("device_request/message.hex"), message);
    }

    [Theory]
    [InlineData(DeviceRequestRule.Http)]
    [InlineData(DeviceRequestRule.Mesh)]
    public void ADeviceRequestProofVerifiesOverItsRequestAndNoOther(DeviceRequestRule rule)
    {
        var request = new JsonObject { ["public_key"] = "a2V5", ["ttl_seconds"] = 3600 };
        var proof = _key.DeviceRequestProof(IoMacula, DeviceRequestProofs.MembershipUcan, request, rule);
        Assert.Equal(2, proof["v"]!.GetValue<int>());
        var timestamp = proof["timestamp"]!.GetValue<long>();
        var nonce = Convert.FromHexString(proof["nonce"]!.GetValue<string>());
        var signature = Convert.FromHexString(proof["signature"]!.GetValue<string>());
        byte[] Message(JsonObject r) => DeviceRequestProofs.Message(_key.PublicKey, IoMacula,
            DeviceRequestProofs.MembershipUcan, timestamp, nonce, r, rule);
        Assert.True(NodeKey.Verify(Message(request), signature, _key.PublicKey, Profile.PqPure));
        var changed = new JsonObject { ["public_key"] = "a2V5", ["ttl_seconds"] = 7200 };
        Assert.False(NodeKey.Verify(Message(changed), signature, _key.PublicKey, Profile.PqPure));
        var again = _key.DeviceRequestProof(IoMacula, DeviceRequestProofs.MembershipUcan, request, rule);
        Assert.NotEqual(proof["nonce"]!.GetValue<string>(), again["nonce"]!.GetValue<string>());
    }

    [Fact]
    public void AnHttpBodyIsSignedAsSent()
    {
        const string body = """{"public_key": "a2V5", "n": 2}""";
        var proof = _key.DeviceRequestProof(IoMacula, DeviceRequestProofs.JoinSession, body);
        var message = DeviceRequestProofs.Message(_key.PublicKey, IoMacula, DeviceRequestProofs.JoinSession,
            proof["timestamp"]!.GetValue<long>(), Convert.FromHexString(proof["nonce"]!.GetValue<string>()), body);
        Assert.True(NodeKey.Verify(message, Convert.FromHexString(proof["signature"]!.GetValue<string>()), _key.PublicKey,
            Profile.PqPure));
    }

    [Theory]
    [InlineData(DeviceRequestRule.Http)]
    [InlineData(DeviceRequestRule.Mesh)]
    public void ADeviceRequestCarryingACallerIsRefused(DeviceRequestRule rule)
    {
        // The realm drops a top-level "caller" when it rebuilds the request: the caller is the signer.
        var request = new JsonObject { ["public_key"] = "a2V5", ["caller"] = "me" };
        Assert.Throws<ArgumentException>(() =>
            _key.DeviceRequestProof(IoMacula, DeviceRequestProofs.MembershipUcan, request, rule));
    }

    [Theory]
    [InlineData(DeviceRequestRule.Http)]
    [InlineData(DeviceRequestRule.Mesh)]
    public void ABooleanInADeviceRequestIsRefused(DeviceRequestRule rule)
    {
        var request = new JsonObject { ["ok"] = true };
        Assert.Throws<ArgumentException>(() =>
            _key.DeviceRequestProof(IoMacula, DeviceRequestProofs.JoinSession, request, rule));
    }

    // mcl_om's vector: io.macula, learn_link, at 1790000000000 with nonce 00..0f, over fields of every
    // CBOR type. The caller in them is left out, as a verifier reads a delivered payload.
    private static JsonObject VectorFields() => new()
    {
        ["subject"] = "entity:alpha",
        ["predicate"] = "knows",
        ["object"] = "entity:beta",
        ["confidence"] = 0.75,
        ["weight"] = 3,
        ["offset"] = -7,
        ["digest"] = Payload.Bytes([1, 2, 3]),
        ["note"] = null,
        ["tags"] = new JsonArray("a", "b"),
        ["metadata"] = new JsonObject { ["source"] = "field-notes", ["page"] = 12 },
        ["caller"] = "a caller is sent, not signed",
    };

    private static byte[] Nonce0To15() => Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();

    [Fact]
    public void TheOwnershipMessageIsMclOmsVector()
    {
        var identity = new MeshId(FixtureHex("ownership_proof/identity.hex"));
        var message = OwnershipProofs.Message(identity, IoMacula, LearnLink, 1790000000000, Nonce0To15(), VectorFields());
        Assert.Equal(FixtureHex("ownership_proof/message.hex"), message);
    }

    [Fact]
    public void AnOwnershipProofVerifiesOverItsFieldsAndNoOther()
    {
        var payload = new JsonObject { ["subject"] = "entity:alpha", ["weight"] = 3, ["digest"] = Payload.Bytes([1]) };
        var signed = _key.OwnershipProof(IoMacula, LearnLink, payload);
        var block = signed["asserted_by"]!.AsObject();
        var proof = block["proof"]!.AsObject();
        Assert.Equal(_key.NodeId.ToString(), block["identity"]!.GetValue<string>());
        Assert.Equal(2, proof["v"]!.GetValue<int>());
        Assert.Equal(_key.PublicKey, Convert.FromHexString(proof["public"]!.GetValue<string>()));
        signed.Remove("asserted_by");
        Assert.True(JsonNode.DeepEquals(payload, signed));
        byte[] Message(JsonObject fields) => OwnershipProofs.Message(_key.NodeId, IoMacula, LearnLink,
            proof["timestamp"]!.GetValue<long>(), Convert.FromHexString(proof["nonce"]!.GetValue<string>()), fields);
        var signature = Convert.FromHexString(proof["signature"]!.GetValue<string>());
        Assert.True(NodeKey.Verify(Message(payload), signature, _key.PublicKey, Profile.PqPure));
        var changed = payload.DeepClone().AsObject();
        changed["weight"] = 4;
        Assert.False(NodeKey.Verify(Message(changed), signature, _key.PublicKey, Profile.PqPure));
    }

    [Fact]
    public void AnOwnershipProvenPayloadCarryingACallerIsRefused()
    {
        Assert.Throws<ArgumentException>(() =>
            _key.OwnershipProof(IoMacula, LearnLink, new JsonObject { ["a"] = 1, ["caller"] = "me" }));
        Assert.Throws<ArgumentException>(() =>
            _key.OwnershipProof(IoMacula, LearnLink, new JsonObject { ["ok"] = true }));
    }

    [Fact]
    public void ANonceIsSixteenBytes()
    {
        Assert.Throws<ArgumentException>(() => OwnershipProofs.Message(_key.NodeId, IoMacula, LearnLink, 1,
            new byte[15], new JsonObject()));
        Assert.Throws<ArgumentException>(() => DeviceRequestProofs.Message(_key.PublicKey, IoMacula,
            DeviceRequestProofs.JoinSession, 1, new byte[17], "{}"));
    }
}

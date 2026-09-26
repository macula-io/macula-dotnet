# The realm's own device request verifier (macula-realm#29) on a join-session
# proof a binding made: MaculaRealm.Identity.DeviceRequestProof.verify/5 must
# accept it once, refuse it with one field of the body changed (bad_proof),
# and refuse it sent again (replayed). The realm's three modules are compiled
# from a macula-realm checkout; nothing else of the realm runs.
#
#   elixir -pa <macula ebin> scripts/interop/realm_device_request.exs <realm module dir> <proof file>
#
# The proof file is the signer's (scripts/interop/Signer): the carried public
# key in hex, the realm id in hex, the procedure, the proof JSON and the body
# exactly as sent, one per line. Run it within 60 s of signing.
[dir, file] = System.argv()

for m <- ["identity.ex", "identity/device_request_replay.ex", "identity/device_request_proof.ex"],
    do: Code.compile_file(Path.join(dir, m))

:ok = Application.put_env(:macula, :crypto_profile, :pq_hybrid)
{:ok, _} = MaculaRealm.Identity.DeviceRequestReplay.start_link()

[public_hex, realm_hex, procedure, proof_json, body] = file |> File.read!() |> String.split("\n", trim: true)
public = Base.decode16!(public_hex, case: :lower)
realm = Base.decode16!(realm_hex, case: :lower)
proof = JSON.decode!(proof_json)
alias MaculaRealm.Identity.DeviceRequestProof

check = fn body_map ->
  {:ok, request} = DeviceRequestProof.json_request(body_map)
  DeviceRequestProof.verify(public, proof, procedure, realm, request)
end

sent = JSON.decode!(body)
genuine = check.(sent)
changed = check.(Map.update!(sent, "device_info", &Map.put(&1, "hostname", "evil.local")))
again = check.(sent)
IO.puts("binding-signed: #{inspect(genuine)}; one field changed: #{inspect(changed)}; the same proof again: #{inspect(again)}")

case {genuine, changed, again} do
  {:ok, {:error, :bad_proof}, {:error, :replayed}} -> :ok
  _ -> System.halt(1)
end

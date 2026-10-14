# Proof, UCAN and sealed-group vectors

Pinned by sha256 in `ProofTests.cs`, `UcanTests.cs` and `GroupTests.cs`.

- `device_request/message.hex`: the realm's device request proof v2 message
  (macula-realm#29) for the inputs `ProofTests` names, from macula-go
  v0.17.0's `devicerequest/testdata/device_request_proof_vector.hex`.
- `ownership_proof/message.hex`, `identity.hex`: mcl_om 0.32.0's
  `message/6` for the inputs `ProofTests` names, and the identity it was made
  for, from macula-go v0.17.0's `ownershipproof/testdata/vector`.
- `ucan/ucan_v1.json`: macula's UCAN vectors, `test/vectors/ucan_v1.json` at
  v14.5.0 (26e8dca6), with `did_key_length` (`UCAN_V1.md` beside it there is
  the contract).
- `groups/e2e_seal_v1_group_keys.json`: macula's sealed-group vectors,
  `test/vectors/e2e_seal_v1_group_keys.json` at v15.0.0-rc.2 (42b60269;
  `E2E_SEAL_V1.md`, its "Sealed groups (package 5)" section, is the
  contract). The epoch's 8-byte id and 32-byte key, the reply's shape, the
  refusals, and the six org-grant UCAN cases. macula-go holds the vectors'
  CBOR byte for byte (`group/vectors_test.go`); this binding never touches
  CBOR, so it reaches the epoch's bytes, every field of a pull's payloads at
  the distributor, and which grants the gate admits — `GroupTests.cs` says
  which, and why.

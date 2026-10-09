# Proof and UCAN vectors

Pinned by sha256 in `ProofTests.cs` and `UcanTests.cs`.

- `device_request/message.hex`: the realm's device request proof v2 message
  (macula-realm#29) for the inputs `ProofTests` names, from macula-go
  v0.17.0's `devicerequest/testdata/device_request_proof_vector.hex`.
- `ownership_proof/message.hex`, `identity.hex`: mcl_om 0.32.0's
  `message/6` for the inputs `ProofTests` names, and the identity it was made
  for, from macula-go v0.17.0's `ownershipproof/testdata/vector`.
- `ucan/ucan_v1.json`: macula's UCAN vectors, `test/vectors/ucan_v1.json` at
  v14.5.0 (26e8dca6), with `did_key_length` (`UCAN_V1.md` beside it there is
  the contract).

# Evidence for the macula-dotnet 0.4.2 candidate (WIP, local only, never push)

Copied out of a session scratchpad under /tmp, which does not survive a reboot.

- logs/: build, red, green and mutation logs for the commits on uranus/0.4.2-candidate,
  named by step (depth, lengths, header, keys, manifest, budget, roothash, version).
- scripts/: the test runner (Release, GC heap cap, timeout) and the mutation runner.
- decode-cost/: the measurement of decoder allocation per element on v0.4.1.
- frame-samples/: the generators and samples recorded from each published builder.

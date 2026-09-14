# .NET lane scratchpad, 09-10 to 09-12 (WIP, local only, never push)

Copied out of a session scratchpad under /tmp, which does not survive a reboot.

- live-tests/: the live test runner (swaps in an OpenSSL-free libmsquic built
  elsewhere on this workstation) and the live run logs it wrote.
- canaries/: the .NET callers and scripts from the hecate-echo and hecate-tube
  canaries, their build and check logs, and their results. They were built against
  a `git archive` of this repo at SOURCE_COMMIT, so project references point at
  `../src/Macula`. Identities are generated at run time; the only constants are
  the public realm id and a clip id.
- frame-samples/: the generator and the 15 frame samples it wrote for cross-SDK
  decoder tests. Samples carry a throwaway identity's public key and signature only.
- logs/: red, green, offline, live and build logs, and draft commit messages, for
  the commits on local main from d1f67da to 61e1fbb and the 0.4.1 release.
- mutations/: mutation-check logs and the mutated source copies they ran.
- backups/: two source files as they stood before an edit.
- issue-1/: the draft and duplicate search for issue #1.

Left out: build output, the git-archive copy of the repo, copies of third-party
sources fetched for research, and every Rust file.

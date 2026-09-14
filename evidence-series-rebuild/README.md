# Evidence behind the .NET series rebuild (WIP, local only, never push)

Copied out of a session scratchpad under /tmp, which does not survive a reboot,
when the .NET lane parked on 2026-09-14.

- logs/: build, test and mutation logs written after the 0.4.2 evidence
  (wip/uranus-0.4.2-evidence): the series replay, the 61e1fbb split, the tree
  guard, the follow-up commits on uranus/series-rebuild up to 682ef6d, and the
  red run of the WIP commit on wip/uranus-series-rebuild-2026-09-14.
- scripts/: the replay and split scripts, the test and mutation runners, the
  mutation checks for 682ef6d, and this securing script.
- messages/: commit message parts and file lists the replay used, and the
  messages of the reply check commits.
- split-61e1fbb/: the three-part split of 61e1fbb, with its intermediate files.

Left out: notes from reviews of other repositories, transcript tools, and what
the 0.4.2 evidence already holds (frame samples, decoder cost).

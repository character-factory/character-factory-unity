# Changelog

## 0.1.1

- Rename `cf-create --timeout` to `--wait-seconds` and raise its default wait to 900 seconds.
- Preserve timed-out server jobs and report their job id and exact `cf-import` recovery command.
- Add `cf-submit` for submitting a generation job without waiting or importing.
- Update Unity Pipeline to 0.6.0-exp.1 for current Unity CLI compatibility.

## 0.1.0

- Require export-manifest 0.6, its 54-role Humanoid map, mouth-interior topology, and all 72 named
  facial morphs.
- Import controller-free canonical prefabs and build the reference animation setup only when
  requested.
- Add full-body idle, talking, walk, jog, run, and interaction reference motions with stable
  controller parameters.
- Add strict `bodyanim-1` and pinned SOMA-77 import into reusable Unity Humanoid clips.
- Preserve body-animation provenance, root-motion policy, loop measurements, and foot contacts in
  `BodyAnimationMetadata`.
- Compose name-based jaw animation from the manifest-declared joint and fit morph.
- Keep generated blink and micro-expression clips independent from body animation and runtime
  expression playback.
- Support durable server jobs, cancellation, retry, idempotency keys, artifact revisions, and
  structured errors, with an optional reproducible generation seed.
- Align create and rebuild payloads with the strict public v0 API.

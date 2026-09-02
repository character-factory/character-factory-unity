# Reference motion provenance

The six `CF_Reference*.anim` assets were generated with Kimodo-SOMA-RP-v1.1 at upstream revision
`1aece8c124d73d255ceff5086d983b844c9f4e94`. Their source artifacts use
`character-factory/bodyanim` version 1 and the pinned SOMA-77 definition hash
`aaf0aff99c267e5a110a2c8ca42dcd0696832e553f2f37e14a94ce6d7adc7c39`.

| Asset | Duration | Last/first pose mean/max | Velocity mismatch mean/max | Loop |
| --- | ---: | ---: | ---: | --- |
| `CF_ReferenceIdle.anim` | 3.000 s | 0.50° / 4.19° | 0.17° / 0.43° per frame | yes |
| `CF_ReferenceTalking.anim` | 5.900 s | 0.86° / 7.49° | 0.77° / 4.56° per frame | yes |
| `CF_ReferenceWalk.anim` | 1.333 s | 0.59° / 4.67° | 0.35° / 1.56° per frame | yes |
| `CF_ReferenceJog.anim` | 0.767 s | 1.12° / 8.82° | 0.67° / 1.97° per frame | yes |
| `CF_ReferenceRun.anim` | 0.767 s | 1.78° / 14.76° | 1.14° / 2.52° per frame | yes |
| `CF_ReferenceInteract.anim` | 4.000 s | 0.43° / 4.30° | n/a | no; one-shot |

Idle, talking, walk, jog, and run use selected cyclic segments and include their first sample at
the exact next-cycle boundary. Interact is a non-looping one-shot. Every `.anim` includes a
`BodyAnimationMetadata` subasset with provider, model, prompt, seed, source hashes, root-motion
policy, and loop measurements.

The package contains the baked Unity animation assets and their metadata. It does not contain
Kimodo code, model weights, a Llama model, or a motion-generation runtime. Kimodo is published at
[nv-tlabs/kimodo](https://github.com/nv-tlabs/kimodo) under the
[NVIDIA Open Model License](https://www.nvidia.com/en-us/agreements/enterprise-software/nvidia-open-model-license/).
That license states that NVIDIA claims no ownership in outputs produced by an NVIDIA Model. The
animation assets are distributed under this package's Apache-2.0 license.

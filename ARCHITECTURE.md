# Architecture

Character Factory Unity has one runtime assembly and one editor assembly.

`CharacterFactory.Core` contains manifest models, the HTTP client, server-address resolution,
runtime expression control, optional reference-motion control, and non-editor verification logic.
It has no `UnityEditor` references.

`CharacterFactory.Editor` contains Unity Pipeline commands, the editor window, GLB import,
Humanoid Avatar construction, generated asset creation, reference-controller construction, and
bodyanim conversion.

## Character flow

```text
prompt or character id
        ↓
Character Factory v0 job and immutable GLB artifact
        ↓
embedded export-manifest 0.6 validation
        ↓
glTFast model + manifest-driven Humanoid Avatar
        ↓
controller-free canonical prefab
        └── optional reference prefab/controller
```

The GLB and its embedded manifest are the character deliverable. The package does not call a
second service to complete an import. Geometry representation, materials, skeleton, expressions,
jaw behavior, and grounding data come from that deliverable.

Server-backed operations are editor-only. They resolve one base address from the explicit command
argument, `CHARACTER_FACTORY_URL`, project settings, or `http://localhost:8400`.

## Animation flow

```text
bodyanim-1 + embedded SOMA-77 definition
        ↓
strict coordinate, hierarchy, hash, frame, and root-policy validation
        ↓
temporary SOMA Human Avatar
        ↓
reusable Unity Humanoid AnimationClip + BodyAnimationMetadata
```

Body-animation conversion does not require a target character. Unity's Humanoid system performs
the target retargeting when the resulting clip is played on a compatible Avatar.

Body clips omit facial muscles, blend shapes, eyes, and jaw. `ExpressionController`, Timeline, or
another facial system can therefore own the face without competing curves. Foot contacts remain
metadata for a consumer's locomotion or IK system.

## Import invariants

- Export manifest schema 0.6 is required.
- Mouth-interior topology and 72 ordered `facs_00`–`facs_71` morphs are required.
- The 54-role Unity Humanoid map is taken from the manifest; no empirical fallback is used.
- The canonical prefab has a valid Avatar and no Animator Controller.
- Reference motion is opt-in and uses a separate prefab.
- Generated facial clips are opt-in assets and are not installed as an always-running Animator
  layer.
- The bodyanim importer accepts only its pinned SOMA-77 joint-order and definition hashes.

## Direct dependencies

- `com.unity.cloud.gltfast` 6.10.1
- `com.unity.nuget.newtonsoft-json` 3.2.2
- `com.unity.pipeline` 0.6.0-exp.1

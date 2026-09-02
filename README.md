# Character Factory Unity

Generate a Character Factory character from a prompt, or import an existing Character Factory
export, as a configured Unity Humanoid prefab.

This is an editor-time package for Unity 6000.0 and newer. It connects to a Character Factory
server running on the same computer or another computer on the local network. Runtime generation
is not supported.

Imported characters, the runtime assembly, and the reference controllers ship in standalone
player builds; WebGL is not supported.

## Install

Add the package to `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.character-factory.unity": "https://github.com/character-factory/character-factory-unity.git"
  }
}
```

You can also use **Window → Package Manager → + → Install package from git URL…** with the same
URL. Unity resolves glTFast, Newtonsoft JSON, and Unity Pipeline from its package registry.
Install the package with the Editor closed, or reopen the project afterwards.

If the Unity CLI reports that the Pipeline package is too old, run `unity pipeline upgrade`.

Install and start the server separately:

```text
pip install "character-factory[generation,server]"
character-factory serve
```

See the [Character Factory server repository](https://github.com/character-factory/character-factory)
for generation setup and hardware requirements.

## Quickstart

With the project open and the [Unity CLI](https://docs.unity.com/en-us/unity-cli) connected to the
Editor:

```text
unity cmd cf-create --prompt "<description>" --walking true --json
unity cmd cf-verify --target "<name>" --json
unity cmd editor_play --json
```

A bake takes several minutes; the Editor does not answer other commands while cf-create waits. For long jobs use cf-submit, then cf-import --id when GET /v0/jobs/{id} reports completion.

Use `spawned.sceneObject` from the first command as `<name>` in the second command.

For a headless setup, run:

```text
unity projects create <name> --template com.unity.template.urp-blank --editor-version <version> --no-cloud
unity open
```

If commands time out, the Editor may be showing a modal dialog; check the Editor window.

`cf-create` submits an asynchronous server job, waits for its artifact, downloads the GLB, reads
its embedded manifest, builds a Humanoid Avatar and prefab, and spawns it in the current scene.
`--walking true` also builds and selects the optional reference-motion prefab at walk speed.

For a transport retry, repeat the create with the same returned `idempotencyKey`:

```text
unity cmd cf-create --prompt "<description>" --idempotency-key <key> --json
```

Use a new key for a new character. Reusing a key with another operation or payload returns an
idempotency conflict.

## What the import creates

Server imports are stored under `Assets/CharacterFactory/<name>_<id>/`. An import produces:

- the downloaded `.glb` and `character.json`;
- a Unity Humanoid Avatar built from the manifest's 54-role `humanoid_map`;
- a canonical prefab with the Avatar and `ExpressionController`, with no Animator Controller;
- optional Blink and MicroExpression clips, which are not played automatically;
- when requested, a separate reference prefab and Animator Controller.

The canonical prefab never starts body animation on its own. Add your own controller for a game,
or request the reference setup for evaluation and prototypes.

Garment and shoe representation belongs to the server export. The package imports separate-shell
geometry as declared; it does not request a representation, convert one into another, or apply a
shell-boundary acceptance gate.

## Reference motion

Pass `--reference-controller true` to `cf-import`, `cf-spawn`, or `cf-create`. The resulting
reference prefab contains six full-body Humanoid motions:

- `CF_ReferenceIdle`
- `CF_ReferenceTalking`
- `CF_ReferenceWalk`
- `CF_ReferenceJog`
- `CF_ReferenceRun`
- `CF_ReferenceInteract`

The controller exposes `MoveSpeed` (`0` idle, `1` walk, `2` jog, `3` run), `Talking` (Boolean),
and `Interact` (trigger). `ReferenceMotionDriver` exposes the same controls from a component.

```csharp
var motion = character.GetComponent<CharacterFactory.Core.ReferenceMotionDriver>();
motion.MoveSpeed = 2f;
motion.Talking = false;
motion.Apply();
motion.PlayInteract();
```

The reference clips contain body curves only. They do not write eye, jaw, or blend-shape
properties, so facial animation can run independently. Their source and loop measurements are in
[the motion provenance note](Editor/ReferenceAnimations/MOTION_PROVENANCE.md).

## Facial control

Every accepted export has mouth-interior topology, exactly 72 morph targets named `facs_00`
through `facs_71`, and a `c_jaw` joint. `ExpressionController` uses the semantic and jaw mappings
from that character's manifest.

```csharp
var face = character.GetComponent<CharacterFactory.Core.ExpressionController>();
face.SetExpression("smile", 0.7f);
face.SetExpression("blink_left", 1f);
face.SetExpression("facs_40", 0.5f);
face.SetExpression("jaw_open", 0.4f);
face.ResetExpressions();
```

Weights use the `0..1` range. `jaw_open` and the manifest-declared jaw expression unit drive the
jaw joint and exterior fit morph together. Use `SetMorph(index, weight)` only when morph-only
control is intentional.

Semantic aliases such as `smile` are provisional and come from the manifest. The numbered
`facs_00` through `facs_71` channel names are the stable fallback.

Manifest 0.6 currently declares these provisional semantic aliases:

| Alias | Morph channel |
| --- | --- |
| `jaw_open` | `facs_24` |
| `blink_left` | `facs_14` |
| `blink_right` | `facs_15` |
| `smile_left` | `facs_32` |
| `smile_right` | `facs_33` |
| `pucker_left` | `facs_40` |
| `pucker_right` | `facs_41` |
| `grin_left` | `facs_56` |
| `grin_right` | `facs_57` |
| `brow_left` | `facs_48` |
| `brow_right` | `facs_49` |

The aliases are convenience names; `facs_00` through `facs_71` are the stable channel names.

## Import generated body animation

`cf-import-bodyanim` converts a `character-factory/bodyanim` version 1 artifact into one reusable
Unity Humanoid clip:

```text
unity cmd cf-import-bodyanim --path "C:/motions/idle.bodyanim.json" --output "Assets/Motions/Idle.anim" --loop true --json
```

The artifact must carry the pinned SOMA-77 hierarchy, rest basis, semantic mapping, coordinate
system, and definition hashes. Unknown or incomplete definitions are rejected. The conversion is
character-independent; the resulting Human clip can be used with compatible Character Factory
Avatars without baking one copy per character.

The `.anim` includes a `BodyAnimationMetadata` subasset containing source provenance, root-motion
policy, loop-boundary measurements, and foot-contact channels. The importer does not contact a
motion server. It does not add facial curves, foot locking, target-specific grounding, or a
gameplay controller.

`--loop true` adds an exact next-cycle boundary key and marks the Unity clip as looping. It does
not repair a discontinuous source motion. For artifacts already under `Assets/`, use
**Character Factory → Import Body Animation** from the context menu.

## Commands

| Command | Result |
| --- | --- |
| `cf-list [--server]` | Lists completed server characters and artifact metadata. |
| `cf-fetch --id <id> [--server]` | Downloads the GLB and character document without building a prefab. |
| `cf-import --id <id> [--reference-controller] [--server]` | Fetches and builds the canonical prefab and optional reference setup. |
| `cf-spawn --id <id> [--position "x,y,z"] [--reference-controller] [--speed 0..3] [--server]` | Imports and instantiates a character. |
| `cf-submit --prompt <text> [--interpreter] [--turbo] [--seed] [--idempotency-key] [--server]` | Submits a generation job and returns its job id immediately, without waiting or importing. |
| `cf-create --prompt <text> [--interpreter] [--turbo] [--seed] [--idempotency-key] [--spawn] [--position] [--reference-controller] [--speed] [--wait-seconds] [--server]` | Generates, imports, and optionally spawns one character; the server job continues if the wait expires. |
| `cf-import-glb --path <Assets/...glb> [--reference-controller]` | Builds from a GLB already inside the project. |
| `cf-import-bodyanim --path <file> [--output] [--name] [--loop] [--overwrite]` | Converts a supported SOMA-77 bodyanim artifact into a Human clip. |
| `cf-verify --target <scene-name>` or `--asset <path>` | Reports orientation, grounding, skeleton, Humanoid, and morph checks. |

`--walking true` is shorthand for `--reference-controller true --speed 1` on `cf-spawn` and
`cf-create`.

The same character workflow is available from **Window → Character Factory**. For a GLB already
under `Assets/`, use **Character Factory → Build Avatar and Prefab** from its context menu.

## Server address

Server-backed commands resolve their address in this order:

1. `--server`
2. `CHARACTER_FACTORY_URL`
3. **Project Settings → Character Factory**
4. `http://localhost:8400`

The package has no other server address or network dependency.

## Export contract

The GLB must contain `character-factory/export-manifest` schema 0.6 in asset-level `extras`. The
manifest supplies units, axes, topology, the Humanoid map, expression inventory and semantics, jaw
composition, animation limitations, and grounding measurements.

Import fails when the manifest is missing or unsupported, the required Humanoid map is incomplete,
mouth-interior topology is absent, or the mesh does not contain the exact 72 named morph targets.
The package does not guess a rig map for older exports.

The GLB's native `idle` is a Generic animation and is not treated as a Unity Humanoid clip.

## Limitations

- Generation and server downloads run in the Unity Editor, not in a player build or WebGL.
- Reference locomotion is in-place prototype motion, with no navigation, root-motion movement,
  or foot IK; foot sliding and ground deviation remain possible.
- The generated Blink and MicroExpression clips are assets only and require explicit playback.
- Expression aliases are provisional; numbered FACS channel names are stable.
- The body-animation importer accepts one pinned SOMA-77 definition and rejects other skeletons.
- Source contact channels are metadata; the package does not turn them into runtime foot locks.
- The manifest's `animation_limitations` entries identify expression combinations that may clip.

## Status and license

Version 0.1.1. File issues at the
[Character Factory Unity issue tracker](https://github.com/character-factory/character-factory-unity/issues).

Licensed under [Apache-2.0](LICENSE.md). Generated reference-motion provenance and third-party
dependencies are documented in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

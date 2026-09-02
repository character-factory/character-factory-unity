using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CharacterFactory.Core;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEngine;

namespace CharacterFactory.Editor
{
    /// <summary>
    /// The agent-facing door: character-factory commands exposed through the Unity Pipeline CLI
    /// (`unity cmd cf-... --arg value`). Every command returns a parseable result object and
    /// throws ArgumentException / InvalidOperationException with actionable messages on failure.
    /// None of them read editor-UI state.
    /// </summary>
    public static class CharacterFactoryCliCommands
    {
        const string ServerArgDescription =
            "Server address override. Default resolution: CHARACTER_FACTORY_URL env var, then the " +
            "Project Settings > Character Factory value, then http://localhost:8400.";

        [Serializable]
        public class SpawnInfo
        {
            public string SceneObject;
            public string Scene;
            public float[] Position;
            public bool Walking;
            public bool ReferenceController;
            public float MoveSpeed;
        }

        [Serializable]
        public class CfImportResponse
        {
            public string Server;
            public string JobId;
            public string IdempotencyKey;
            public string Id;
            public string Name;
            public string GlbPath;
            public string AvatarPath;
            public string PrefabPath;
            public string ReferencePrefabPath;
            public string ControllerPath;
            public string ManifestSource;
            public string ManifestSchemaVersion;
            public bool ManifestHumanoidMapUsed;
            public string Topology;
            public float GroundPlaneM;
            public float ManifestRootHeightM;
            public float HumanoidRootHeightM;
            public float GroundingAdjustmentM;
            public int ExpressionMorphCount;
            public List<string> ExpressionNames;
            public string BlinkClipPath;
            public string MicroExpressionClipPath;
            public string IdleClipPath;
            public string WalkClipPath;
            public string JogClipPath;
            public string RunClipPath;
            public string TalkingClipPath;
            public string InteractClipPath;
            public string ReferenceMotionSource;
            public bool ReferenceControllerIncluded;
            public List<string> ReferenceValidation;
            public List<string> Warnings;
            public List<ApiWarning> ServerWarnings;
            public SpawnInfo Spawned;
        }

        // ------------------------------------------------------------------ cf-list
        [CliCommand("cf-list", "List characters on the character-factory server.", Tags = new[] { "character-factory" })]
        public static async Task<object> List(
            [CliArg("server", ServerArgDescription)] string server = null)
        {
            var client = Client(server);
            var rows = await client.ListCharactersAsync();
            return new
            {
                server = client.BaseUrl,
                count = rows.Length,
                characters = rows.Select(r => new
                {
                    r.Id,
                    r.Name,
                    revision = r.Artifact?.Revision ?? 0,
                    bytes = r.Artifact?.Bytes,
                    sha256 = r.Artifact?.Sha256,
                    r.CreatedAt,
                }).ToArray(),
            };
        }

        // ------------------------------------------------------------------ cf-fetch
        [CliCommand("cf-fetch", "Download a character's GLB (plus its character.json) into Assets/CharacterFactory/ and import it. No avatar or prefab is built — use cf-import for the full chain.", Tags = new[] { "character-factory" })]
        public static async Task<object> Fetch(
            [CliArg("id", "Character id on the server.", Required = true)] string id,
            [CliArg("server", ServerArgDescription)] string server = null)
        {
            var client = Client(server);
            var (assetPath, record) = await CharacterImportPipeline.FetchAsync(client, id);
            return new { server = client.BaseUrl, id = record.Id, name = record.Name, glbPath = assetPath };
        }

        // ------------------------------------------------------------------ cf-import
        [CliCommand("cf-import", "Fetch a character and build a controller-free Humanoid prefab. Optionally build the full-body reference animation setup.", Tags = new[] { "character-factory" })]
        public static async Task<CfImportResponse> Import(
            [CliArg("id", "Character id on the server.", Required = true)] string id,
            [CliArg("reference-controller", "Also build the optional generated full-body reference controller and prefab.")] bool referenceController = false,
            [CliArg("server", ServerArgDescription)] string server = null)
        {
            var client = Client(server);
            var import = await CharacterImportPipeline.ImportAsync(client, id, referenceController);
            return ToResponse(client.BaseUrl, import, null);
        }

        // ------------------------------------------------------------------ cf-spawn
        [CliCommand("cf-spawn", "cf-import plus instantiation. The canonical prefab has no body controller; --reference-controller opts into the generated full-body reference setup.", Tags = new[] { "character-factory" })]
        public static async Task<CfImportResponse> Spawn(
            [CliArg("id", "Character id on the server.", Required = true)] string id,
            [CliArg("position", "Spawn position as \"x,y,z\" (default 0,0,0).")] string position = null,
            [CliArg("reference-controller", "Spawn the optional full-body reference prefab.")] bool referenceController = false,
            [CliArg("speed", "Reference locomotion preview speed: 0 idle, 1 walk, 2 jog, 3 run.")] float speed = 0f,
            [CliArg("walking", "Shorthand for --reference-controller true --speed 1.")] bool walking = false,
            [CliArg("server", ServerArgDescription)] string server = null)
        {
            var client = Client(server);
            bool includeReference = referenceController || walking || speed > 0f;
            if (walking && speed <= 0f) speed = 1f;
            var import = await CharacterImportPipeline.ImportAsync(client, id, includeReference);
            var instance = CharacterImportPipeline.Spawn(import, ParsePosition(position), includeReference, speed);
            return ToResponse(client.BaseUrl, import, instance);
        }

        // ------------------------------------------------------------------ cf-create
        [CliCommand("cf-create", "POST a prompt, wait for the server job, import the result, and optionally spawn it.", Tags = new[] { "character-factory" })]
        public static async Task<CfImportResponse> Create(
            [CliArg("prompt", "Character description for the generator.", Required = true)] string prompt,
            [CliArg("interpreter", "Interpreter alias (see GET /v0/interpreters); server default when omitted.")] string interpreter = null,
            [CliArg("turbo", "Use the server's faster texture-bake path (default false).")] bool turbo = false,
            [CliArg("seed", "Optional integer seed for reproducible character generation.")] string seed = null,
            [CliArg("idempotency-key", "Retry key for this intended create. Reuse only when retrying the same request; generated when omitted.")] string idempotencyKey = null,
            [CliArg("spawn", "Instantiate into the open scene after import (default true).")] bool spawn = true,
            [CliArg("position", "Spawn position as \"x,y,z\" (default 0,0,0).")] string position = null,
            [CliArg("reference-controller", "Build/spawn the optional full-body reference setup.")] bool referenceController = false,
            [CliArg("speed", "Reference locomotion preview speed: 0 idle, 1 walk, 2 jog, 3 run.")] float speed = 0f,
            [CliArg("walking", "Shorthand for --reference-controller true --speed 1.")] bool walking = false,
            [CliArg("timeout", "Seconds to wait for the server-side bake (default 300).")] int timeout = 300,
            [CliArg("server", ServerArgDescription)] string server = null)
        {
            if (string.IsNullOrWhiteSpace(prompt))
                throw new ArgumentException("Prompt is empty.");
            long? parsedSeed = null;
            if (!string.IsNullOrWhiteSpace(seed))
            {
                if (!long.TryParse(seed, out var value))
                    throw new ArgumentException("--seed must be an integer.");
                parsedSeed = value;
            }

            var client = Client(server);
            var request = new CreateCharacterRequest
            {
                Prompt = prompt,
                Interpreter = interpreter,
                Turbo = turbo,
                Seed = parsedSeed,
                IdempotencyKey = idempotencyKey ?? CharacterFactoryClient.NewIdempotencyKey(),
            };
            Debug.Log($"[character-factory] create key {request.IdempotencyKey}; retain it only for retries of this request.");
            var job = await client.CreateCharacterAsync(request);
            Debug.Log($"[character-factory] queued job {job.Id}; waiting for the bake…");
            job = await client.WaitForJobAsync(job.Id, TimeSpan.FromSeconds(timeout),
                j => Debug.Log($"[character-factory] {j.Id}: {j.Status}/{j.Stage} {j.Progress:P0}"));

            bool includeReference = referenceController || walking || speed > 0f;
            if (walking && speed <= 0f) speed = 1f;
            var import = await CharacterImportPipeline.ImportAsync(client, job.Result.CharacterId, includeReference);
            GameObject instance = null;
            if (spawn)
                instance = CharacterImportPipeline.Spawn(import, ParsePosition(position), includeReference, speed);
            return ToResponse(client.BaseUrl, import, instance, job, request.IdempotencyKey);
        }

        // ------------------------------------------------------------------ cf-import-glb
        [CliCommand("cf-import-glb", "The existing-GLB door: run the avatar/prefab chain on a .glb already under Assets/. Requires an embedded character-factory export-manifest 0.6.", Tags = new[] { "character-factory" })]
        public static CfImportResponse ImportGlb(
            [CliArg("path", "Project-relative path to the .glb (e.g. Assets/Models/hero.glb).", Required = true)] string path,
            [CliArg("reference-controller", "Also build the optional full-body reference controller and prefab.")] bool referenceController = false)
        {
            var import = CharacterImportPipeline.BuildFromGlb(path, includeReferenceController: referenceController);
            return ToResponse(null, import, null);
        }

        // -------------------------------------------------------- cf-import-bodyanim
        [CliCommand("cf-import-bodyanim", "Validate a bodyanim-1 artifact and bake one reusable Unity Humanoid .anim clip. The conversion is character-independent and preserves source provenance and foot contacts as a metadata subasset.", Tags = new[] { "character-factory", "animation" })]
        public static object ImportBodyAnimation(
            [CliArg("path", "Filesystem or project-relative path to a bodyanim-1 JSON artifact.", Required = true)] string path,
            [CliArg("output", "Project-relative .anim path under Assets/. Defaults to Assets/CharacterFactory/Motions/<source-name>.anim.")] string output = null,
            [CliArg("name", "Optional Unity AnimationClip name override.")] string name = null,
            [CliArg("loop", "Mark the Unity clip as looping. This does not repair provider endpoint discontinuities.")] bool loop = false,
            [CliArg("overwrite", "Replace an existing output asset (default true).")] bool overwrite = true)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Body animation path is empty.");
            if (string.IsNullOrWhiteSpace(output))
            {
                string file = Path.GetFileName(path);
                string stem = file.EndsWith(".bodyanim.json", StringComparison.OrdinalIgnoreCase)
                    ? file.Substring(0, file.Length - ".bodyanim.json".Length)
                    : Path.GetFileNameWithoutExtension(file);
                output = $"Assets/CharacterFactory/Motions/{SanitizeFileName(stem)}.anim";
            }
            var result = BodyAnimationImporter.Import(path, output,
                new BodyAnimationImporter.Options
                {
                    ClipName = string.IsNullOrWhiteSpace(name)
                        ? Path.GetFileNameWithoutExtension(output)
                        : name,
                    Loop = loop,
                    Overwrite = overwrite,
                });
            return new
            {
                result.SourcePath,
                result.ClipPath,
                result.MetadataSubAsset,
                result.SkeletonId,
                result.SkeletonDefinitionId,
                result.SkeletonDefinitionSha256,
                result.JointOrderSha256,
                result.RootMotionPolicy,
                result.Provider,
                result.Model,
                result.FrameCount,
                result.FramesPerSecond,
                result.DurationSeconds,
                result.Loop,
                result.HumanoidBindings,
                result.FacialBindings,
                result.BoundaryMeanDegrees,
                result.BoundaryMaximumDegrees,
                result.BoundaryMaximumJoint,
                result.Warnings,
            };
        }

        // ------------------------------------------------------------------ cf-verify
        [CliCommand("cf-verify", "Run the handedness/facing/grounding/humanoid checks against a scene object (--target) or a prefab/model asset (--asset). Returns per-check evidence.", Tags = new[] { "character-factory" })]
        public static object Verify(
            [CliArg("target", "Scene GameObject name to verify in place.")] string target = null,
            [CliArg("asset", "Prefab or .glb asset path to verify (instantiated temporarily at the origin).")] string asset = null,
            [CliArg("glb", "Optional .glb path to read the embedded manifest from (defaults to --asset when it is a .glb).")] string glb = null)
        {
            if (string.IsNullOrEmpty(target) == string.IsNullOrEmpty(asset))
                throw new ArgumentException("Pass exactly one of --target (scene object) or --asset (asset path).");

            ExportManifest manifest = null;
            var manifestPath = glb ?? (asset != null && asset.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) ? asset : null);
            if (manifestPath != null && System.IO.File.Exists(manifestPath))
                GlbManifestReader.TryReadFile(manifestPath, out manifest, out _);

            CharacterVerification.Report report;
            string subject;
            if (target != null)
            {
                // Imported GLBs nest an inner node with the same name as the prefab root, so
                // prefer the match that owns the Animator (the configured root).
                GameObject go = null;
                foreach (var animator in UnityEngine.Object.FindObjectsByType<Animator>(FindObjectsSortMode.None))
                    if (animator.gameObject.name == target) { go = animator.gameObject; break; }
                go = go ?? GameObject.Find(target);
                if (go == null)
                    throw new ArgumentException($"No scene object named '{target}' found (it must be active).");
                subject = $"scene object '{target}'";
                report = CharacterVerification.Run(go, manifest);
            }
            else
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(asset);
                if (prefab == null)
                    throw new ArgumentException($"No GameObject asset at '{asset}'.");
                subject = $"asset '{asset}'";
                var temp = (GameObject)UnityEngine.Object.Instantiate(prefab);
                temp.hideFlags = HideFlags.HideAndDontSave;
                try
                {
                    temp.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                    report = CharacterVerification.Run(temp, manifest);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(temp);
                }
            }

            return new
            {
                subject,
                passed = report.Passed,
                manifest = manifest != null ? "embedded" : "none",
                checks = report.Checks.Select(c => new { c.Name, c.Passed, c.Evidence }).ToArray(),
                notes = report.Notes,
            };
        }

        // ------------------------------------------------------------------ helpers
        static CharacterFactoryClient Client(string serverOverride) =>
            new CharacterFactoryClient(CharacterFactorySettings.ResolveServer(serverOverride));

        static Vector3 ParsePosition(string position)
        {
            if (string.IsNullOrWhiteSpace(position)) return Vector3.zero;
            var parts = position.Split(',');
            if (parts.Length != 3
                || !float.TryParse(parts[0], out var x)
                || !float.TryParse(parts[1], out var y)
                || !float.TryParse(parts[2], out var z))
                throw new ArgumentException($"Could not parse position '{position}'. Expected \"x,y,z\", e.g. \"1.5,0,0\".");
            return new Vector3(x, y, z);
        }

        static string SanitizeFileName(string value)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars())
                value = value.Replace(invalid, '-');
            return string.IsNullOrWhiteSpace(value) ? "body-animation" : value;
        }

        static CfImportResponse ToResponse(
            string server, CharacterImportPipeline.ImportResult import, GameObject instance,
            CharacterJob job = null, string idempotencyKey = null)
        {
            return new CfImportResponse
            {
                Server = server,
                JobId = job?.Id,
                IdempotencyKey = idempotencyKey,
                Id = import.Id,
                Name = import.Name,
                GlbPath = import.GlbPath,
                AvatarPath = import.AvatarPath,
                PrefabPath = import.PrefabPath,
                ReferencePrefabPath = import.ReferencePrefabPath,
                ControllerPath = import.ControllerPath,
                ManifestSource = import.ManifestSource,
                ManifestSchemaVersion = import.ManifestSchemaVersion,
                ManifestHumanoidMapUsed = import.ManifestHumanoidMapUsed,
                Topology = import.Topology,
                GroundPlaneM = import.GroundPlaneM,
                ManifestRootHeightM = import.ManifestRootHeightM,
                HumanoidRootHeightM = import.HumanoidRootHeightM,
                GroundingAdjustmentM = import.GroundingAdjustmentM,
                ExpressionMorphCount = import.ExpressionMorphCount,
                ExpressionNames = import.ExpressionNames,
                BlinkClipPath = import.BlinkClipPath,
                MicroExpressionClipPath = import.MicroExpressionClipPath,
                IdleClipPath = import.IdleClipPath,
                WalkClipPath = import.WalkClipPath,
                JogClipPath = import.JogClipPath,
                RunClipPath = import.RunClipPath,
                TalkingClipPath = import.TalkingClipPath,
                InteractClipPath = import.InteractClipPath,
                ReferenceMotionSource = import.ReferenceMotionSource,
                ReferenceControllerIncluded = import.ReferenceControllerIncluded,
                ReferenceValidation = import.ReferenceValidation,
                Warnings = import.Warnings,
                ServerWarnings = job?.Warnings,
                Spawned = instance == null ? null : new SpawnInfo
                {
                    SceneObject = instance.name,
                    Scene = instance.scene.path,
                    Position = new[] { instance.transform.position.x, instance.transform.position.y, instance.transform.position.z },
                    ReferenceController = instance.GetComponent<ReferenceMotionDriver>() != null,
                    MoveSpeed = instance.GetComponent<ReferenceMotionDriver>()?.MoveSpeed ?? 0f,
                    Walking = (instance.GetComponent<ReferenceMotionDriver>()?.MoveSpeed ?? 0f) > 0f,
                },
            };
        }
    }
}

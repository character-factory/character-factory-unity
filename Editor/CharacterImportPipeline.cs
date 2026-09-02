using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CharacterFactory.Core;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace CharacterFactory.Editor
{
    /// <summary>
    /// The Character Factory editor import pipeline:
    ///   GLB (embedded manifest 0.6) -> glTFast import -> manifest-driven Humanoid avatar ->
    ///   controller-free configured prefab, plus an optional validated reference controller.
    /// Every door (CLI commands, existing-GLB import, editor window) funnels through here.
    /// </summary>
    public static class CharacterImportPipeline
    {
        public const string AssetRootFolder = "Assets/CharacterFactory";

        [Serializable]
        public class ImportResult
        {
            public string Id;
            public string Name;
            public string GlbPath;
            public string AvatarPath;
            public string PrefabPath;
            public string ReferencePrefabPath;
            public string ControllerPath;
            public string IdleClipPath;
            public string WalkClipPath;
            public string JogClipPath;
            public string RunClipPath;
            public string TalkingClipPath;
            public string InteractClipPath;
            public string ReferenceMotionSource;
            public bool ReferenceControllerIncluded;
            public string ManifestSource;
            public bool ManifestHumanoidMapUsed;
            public string Topology;
            public string ManifestSchemaVersion;
            public float GroundPlaneM;
            public float ManifestRootHeightM;
            public float HumanoidRootHeightM;
            public float GroundingAdjustmentM;
            public string BlinkClipPath;
            public string MicroExpressionClipPath;
            public int ExpressionMorphCount;
            public List<string> ExpressionNames = new List<string>();
            public List<string> ReferenceValidation = new List<string>();
            public List<string> Warnings = new List<string>();
        }

        /// <summary>Download a character's GLB into the project (no import).</summary>
        public static async Task<(string assetPath, CharacterRecord record)> FetchAsync(
            CharacterFactoryClient client, string id)
        {
            var record = await client.GetCharacterAsync(id);
            if (!record.IsAvailable)
                throw new InvalidOperationException(
                    $"Character {id} has no completed artifact. Poll its creation/rebuild job before importing.");

            var folder = CharacterFolder(record);
            Directory.CreateDirectory(folder);
            var assetPath = $"{folder}/{SafeName(record)}.glb".Replace('\\', '/');
            await client.DownloadSceneGlbAsync(id, assetPath);

            // Keep the character document beside the GLB for provenance.
            try
            {
                File.WriteAllText($"{folder}/{SafeName(record)}.character.json",
                    await client.GetCharacterDocumentAsync(id));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[character-factory] Could not fetch character.json for {id}: {e.Message}");
            }

            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
            return (assetPath, record);
        }

        /// <summary>
        /// The existing-GLB door: run the full avatar/prefab build for a .glb already in the
        /// project. First-class path — the manifest rides inside the file.
        /// </summary>
        public static ImportResult BuildFromGlb(string glbAssetPath, string id = null,
            string displayName = null, bool includeReferenceController = false)
        {
            if (string.IsNullOrEmpty(glbAssetPath) || !glbAssetPath.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"'{glbAssetPath}' is not a .glb asset path.");
            glbAssetPath = glbAssetPath.Replace('\\', '/');
            if (!glbAssetPath.StartsWith("Assets/"))
                throw new ArgumentException($"'{glbAssetPath}' must be inside the project's Assets folder. Use cf-fetch (or copy the file in) first.");
            if (!File.Exists(glbAssetPath))
                throw new ArgumentException($"No file exists at '{glbAssetPath}'.");

            AssetDatabase.ImportAsset(glbAssetPath, ImportAssetOptions.ForceSynchronousImport);
            var modelPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(glbAssetPath);
            if (modelPrefab == null)
                throw new InvalidOperationException(
                    $"'{glbAssetPath}' did not import as a model. Is com.unity.cloud.gltfast installed and the file a valid GLB?");

            var result = new ImportResult
            {
                Id = id,
                Name = displayName ?? Path.GetFileNameWithoutExtension(glbAssetPath),
                GlbPath = glbAssetPath,
            };

            // Manifest 0.6 is the authoritative format contract. This package does not guess at
            // missing or precontract exports.
            ExportManifest manifest = null;
            try
            {
                if (GlbManifestReader.TryReadFile(glbAssetPath, out manifest, out _))
                    result.ManifestSource = ManifestSource.EmbeddedExtras.ToString();
            }
            catch (Exception e)
            {
                result.Warnings.Add($"Could not parse GLB container for the embedded manifest: {e.Message}");
            }
            if (manifest == null)
                throw new InvalidDataException(
                    "The GLB has no recognized embedded character-factory export manifest. " +
                    "This package requires export-manifest 0.6; regenerate the character with a current server.");
            manifest.RequireSupportedBaseline();
            result.ManifestSchemaVersion = manifest.SchemaVersion;
            result.Topology = manifest.Topology;
            result.GroundPlaneM = manifest.Grounding.PlaneHeightM;
            result.ManifestRootHeightM = manifest.Grounding.RootSceneHeightM;
            ValidateMorphInventory(modelPrefab, manifest);

            var folder = Path.GetDirectoryName(glbAssetPath).Replace('\\', '/');
            var baseName = Path.GetFileNameWithoutExtension(glbAssetPath);

            // --- Humanoid avatar, built on a throwaway instance (T-pose straightening mutates it).
            var temp = (GameObject)UnityEngine.Object.Instantiate(modelPrefab);
            temp.hideFlags = HideFlags.HideAndDontSave;
            Avatar avatar;
            try
            {
                temp.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                var built = AvatarFactory.Build(temp, manifest);
                avatar = built.Avatar;
                result.ManifestHumanoidMapUsed = built.FromManifestMap;
                result.Warnings.AddRange(built.Warnings);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(temp);
            }

            avatar.name = baseName + "_Avatar";
            result.AvatarPath = $"{folder}/{avatar.name}.asset";
            avatar = SaveOrUpdate(avatar, result.AvatarPath);

            // Unity's muscle zero is a range midpoint, not the character's source rest pose.
            // In particular, this Avatar needs a strongly positive Lower Leg Stretch value for
            // straight knees. A partial clip is therefore actively unsafe. The canonical prefab
            // owns no body controller; the optional reference path below uses independently
            // authored clips that drive the complete body.
            result.HumanoidRootHeightM = result.ManifestRootHeightM;
            result.GroundingAdjustmentM = 0f;

            AnimatorController controller = null;
            ReferenceMotionLibrary.Motions referenceMotions = null;
            if (includeReferenceController)
            {
                referenceMotions = ReferenceMotionLibrary.Load();
                result.ReferenceValidation.AddRange(
                    ReferenceMotionLibrary.Validate(modelPrefab, avatar, manifest, referenceMotions));
                result.ReferenceMotionSource = ReferenceMotionLibrary.SourceLabel;
                result.ReferenceControllerIncluded = true;
                result.IdleClipPath = AssetDatabase.GetAssetPath(referenceMotions.Idle);
                result.WalkClipPath = AssetDatabase.GetAssetPath(referenceMotions.Walk);
                result.JogClipPath = AssetDatabase.GetAssetPath(referenceMotions.Jog);
                result.RunClipPath = AssetDatabase.GetAssetPath(referenceMotions.Run);
                result.TalkingClipPath = AssetDatabase.GetAssetPath(referenceMotions.Talking);
                result.InteractClipPath = AssetDatabase.GetAssetPath(referenceMotions.Interact);
                result.ControllerPath = $"{folder}/{baseName}_Reference.controller";
                controller = ReferenceMotionLibrary.BuildController(result.ControllerPath, referenceMotions);
            }

            // --- Expressions: generated clips are opt-in assets. They are deliberately not put
            // on an always-running Animator layer, which would overwrite facial weights applied
            // by ExpressionController, Timeline, or another facial-animation system.
            if (manifest.Morphs != null)
            {
                result.ExpressionMorphCount = manifest.Morphs.Count;
                result.ExpressionNames.AddRange(manifest.Morphs.Semantics.Keys);

                var probe = (GameObject)UnityEngine.Object.Instantiate(modelPrefab);
                probe.hideFlags = HideFlags.HideAndDontSave;
                string bodyPath;
                try
                {
                    var bodySmr = probe.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                        .OrderByDescending(s => s.sharedMesh != null ? s.sharedMesh.blendShapeCount : 0).First();
                    bodyPath = AnimationUtility.CalculateTransformPath(bodySmr.transform, probe.transform);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(probe);
                }

                int Sem(string name) => manifest.Morphs.Semantics.TryGetValue(name, out var i) ? i : -1;

                var blinkClip = NeutralPose.BuildBlinkClip(bodyPath, Sem("blink_left"), Sem("blink_right"));
                MakeLooping(blinkClip);
                result.BlinkClipPath = $"{folder}/{baseName}_Blink.anim";
                blinkClip = SaveOrUpdate(blinkClip, result.BlinkClipPath);

                var micro = NeutralPose.BuildMicroExpressionClip(bodyPath,
                    Sem("brow_left"), Sem("brow_right"), Sem("smile_left"), Sem("smile_right"));
                MakeLooping(micro);
                result.MicroExpressionClipPath = $"{folder}/{baseName}_MicroExpression.anim";
                SaveOrUpdate(micro, result.MicroExpressionClipPath);

                if (Sem("blink_left") < 0 || Sem("blink_right") < 0)
                    result.Warnings.Add("Manifest semantics lack blink_left/blink_right; the blink demo clip is empty for the missing side(s).");

            }

            // --- Canonical prefab: Humanoid Avatar + facial bridge, deliberately no body
            // controller. Importing a character must never make it start posing or moving.
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(modelPrefab);
            try
            {
                instance.name = baseName;
                var animator = instance.GetComponent<Animator>();
                if (animator == null) animator = instance.AddComponent<Animator>();
                animator.avatar = avatar;
                animator.runtimeAnimatorController = null;
                animator.applyRootMotion = false;

                ConfigureExpressionController(instance, manifest);

                result.PrefabPath = $"{folder}/{baseName}.prefab";
                PrefabUtility.SaveAsPrefabAsset(instance, result.PrefabPath);

                if (controller != null)
                {
                    animator.runtimeAnimatorController = controller;
                    var driver = instance.GetComponent<ReferenceMotionDriver>()
                        ?? instance.AddComponent<ReferenceMotionDriver>();
                    driver.MoveSpeed = 0f;
                    result.ReferencePrefabPath = $"{folder}/{baseName}_Reference.prefab";
                    PrefabUtility.SaveAsPrefabAsset(instance, result.ReferencePrefabPath);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }

            AssetDatabase.SaveAssets();
            return result;
        }

        /// <summary>Fetch + full build for a server character.</summary>
        public static async Task<ImportResult> ImportAsync(CharacterFactoryClient client, string id,
            bool includeReferenceController = false)
        {
            var (assetPath, record) = await FetchAsync(client, id);
            var result = BuildFromGlb(assetPath, record.Id, record.Name, includeReferenceController);
            return result;
        }

        /// <summary>Instantiate a built prefab into the active scene.</summary>
        public static GameObject Spawn(ImportResult import, Vector3 position,
            bool useReferenceController, float moveSpeed = 0f)
        {
            var path = useReferenceController ? import.ReferencePrefabPath : import.PrefabPath;
            if (useReferenceController && string.IsNullOrEmpty(path))
                throw new InvalidOperationException(
                    "The reference controller was not built. Import again with includeReferenceController=true.");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
                throw new InvalidOperationException($"Prefab missing at {path}.");
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            instance.transform.position = position;
            var driver = instance.GetComponent<ReferenceMotionDriver>();
            if (driver != null)
            {
                driver.MoveSpeed = Mathf.Clamp(moveSpeed, 0f, 3f);
                driver.Apply();
            }
            EditorSceneManager.MarkSceneDirty(instance.scene);
            return instance;
        }

        /// <summary>Convenience overload: walking opts into the reference setup at speed 1.</summary>
        public static GameObject Spawn(ImportResult import, Vector3 position, bool walking) =>
            Spawn(import, position, walking, walking ? 1f : 0f);

        static void ValidateMorphInventory(GameObject modelPrefab, ExportManifest manifest)
        {
            var body = modelPrefab.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .OrderByDescending(s => s.sharedMesh != null ? s.sharedMesh.blendShapeCount : 0)
                .FirstOrDefault();
            var mesh = body != null ? body.sharedMesh : null;
            if (mesh == null || mesh.blendShapeCount != manifest.Morphs.Count)
                throw new InvalidDataException(
                    $"Imported GLB has {mesh?.blendShapeCount ?? 0} facial morphs; manifest requires {manifest.Morphs.Count}.");
            for (int i = 0; i < manifest.Morphs.Names.Count; i++)
                if (!mesh.GetBlendShapeName(i).EndsWith(manifest.Morphs.Names[i], StringComparison.Ordinal))
                    throw new InvalidDataException(
                        $"Imported GLB morph {i} is '{mesh.GetBlendShapeName(i)}'; manifest requires '{manifest.Morphs.Names[i]}'.");
        }

        /// <summary>
        /// Attach and configure the runtime ExpressionController from the manifest: semantic
        /// name table (provisional — resolved at import, never hardcoded), jaw joint, axis and
        /// angles, and the jaw fit morph.
        /// </summary>
        static void ConfigureExpressionController(GameObject instance, ExportManifest manifest)
        {
            var controller = instance.GetComponent<ExpressionController>();
            if (controller == null) controller = instance.AddComponent<ExpressionController>();
            controller.Body = instance.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .OrderByDescending(s => s.sharedMesh != null ? s.sharedMesh.blendShapeCount : 0).First();
            controller.Semantics.Clear();
            if (manifest.Morphs != null)
                foreach (var kv in manifest.Morphs.Semantics)
                    controller.Semantics.Add(new ExpressionController.SemanticEntry { Name = kv.Key, Index = kv.Value });

            var jaw = manifest.Jaw;
            if (jaw != null)
            {
                foreach (var t in instance.GetComponentsInChildren<Transform>(true))
                    if (t.name == (jaw.Joint ?? "c_jaw")) { controller.JawJoint = t; break; }
                if (jaw.RotationAxisLocal is { Length: 3 })
                    controller.JawAxisLocal = new Vector3(jaw.RotationAxisLocal[0], jaw.RotationAxisLocal[1], jaw.RotationAxisLocal[2]);
                controller.JawFitAngleDegrees = jaw.ExpressionFitAngleDegrees > 0 ? jaw.ExpressionFitAngleDegrees : 14.84f;
                controller.JawFullOpenDegrees = jaw.FullOpenDegrees > 0 ? jaw.FullOpenDegrees : 27.06f;
                controller.JawFitMorphIndex = jaw.ExpressionUnit;
            }
        }

        /// <summary>
        /// Persist an object at a path, updating any existing asset in place (CopySerialized)
        /// so scene instances and other assets that reference it keep working across re-imports.
        /// Returns the object that now lives at the path.
        /// </summary>
        static T SaveOrUpdate<T>(T fresh, string path) where T : UnityEngine.Object
        {
            var expectedName = Path.GetFileNameWithoutExtension(path);
            fresh.name = expectedName;
            var existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (existing != null)
            {
                EditorUtility.CopySerialized(fresh, existing);
                // CopySerialized does not reliably rename an existing main object. Unity emits
                // warning-shaped stack traces when an asset's main object and filename differ.
                existing.name = expectedName;
                EditorUtility.SetDirty(existing);
                UnityEngine.Object.DestroyImmediate(fresh);
                return existing;
            }
            AssetDatabase.CreateAsset(fresh, path);
            return fresh;
        }

        static void MakeLooping(AnimationClip clip)
        {
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = true;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
        }

        static string SafeName(CharacterRecord record)
        {
            var name = string.IsNullOrEmpty(record.Name) ? record.Id : record.Name;
            var safe = new string(name.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-').ToArray());
            return safe.Length > 40 ? safe.Substring(0, 40) : safe;
        }

        static string CharacterFolder(CharacterRecord record) =>
            $"{AssetRootFolder}/{SafeName(record)}_{record.Id.Substring(0, Math.Min(8, record.Id.Length))}";
    }
}

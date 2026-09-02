using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CharacterFactory.Core;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace CharacterFactory.Editor
{
    /// <summary>
    /// Strict editor-time importer for character-factory/bodyanim version 1. It accepts the pinned
    /// SOMA-77 definition carried by the artifact and bakes one reusable Unity Humanoid clip.
    /// Target Character Factory characters are not involved in this conversion.
    /// </summary>
    public static class BodyAnimationImporter
    {
        public const string Schema = "character-factory/bodyanim";
        public const int SchemaVersion = 1;
        public const string SomaSkeletonId = "somaskel77";
        public const string SomaDefinitionId = "soma/somaskel77";
        public const string SomaJointOrderSha256 = "7f1c171817f9600cc53b475a37508b5b4062774ea74ba22e16e94c180e33bf22";
        public const string SomaDefinitionSha256 = "aaf0aff99c267e5a110a2c8ca42dcd0696832e553f2f37e14a94ce6d7adc7c39";

        public sealed class Options
        {
            public string ClipName;
            public bool Loop;
            public bool Overwrite = true;
        }

        public sealed class Result
        {
            public string SourcePath;
            public string ClipPath;
            public string MetadataSubAsset;
            public string SkeletonId;
            public string SkeletonDefinitionId;
            public string SkeletonDefinitionSha256;
            public string JointOrderSha256;
            public string RootMotionPolicy;
            public string Provider;
            public string Model;
            public int FrameCount;
            public float FramesPerSecond;
            public float DurationSeconds;
            public bool Loop;
            public int HumanoidBindings;
            public int FacialBindings;
            public float BoundaryMeanDegrees;
            public float BoundaryMaximumDegrees;
            public string BoundaryMaximumJoint;
            public List<string> Warnings = new List<string>();
        }

        sealed class Document
        {
            public JObject Json;
            public string[] Joints;
            public JArray Frames;
            public string RootMotionPolicy;
            public float Fps;
            public float Duration;
            public float BoundaryMeanDegrees;
            public float BoundaryMaximumDegrees;
            public string BoundaryMaximumJoint;
        }

        public static Result Import(string sourcePath, string outputAssetPath, Options options = null)
        {
            options = options ?? new Options();
            var resolvedSource = ResolveSourcePath(sourcePath);
            var output = NormalizeOutputPath(outputAssetPath);
            var document = ParseAndValidate(File.ReadAllText(resolvedSource));
            EnsureOutputFolder(output);

            if (AssetDatabase.LoadMainAssetAtPath(output) != null)
            {
                if (!options.Overwrite)
                    throw new IOException($"An asset already exists at '{output}'.");
                if (!AssetDatabase.DeleteAsset(output))
                    throw new IOException($"Could not replace existing asset '{output}'.");
            }

            var source = BuildSourceHierarchy(document);
            Avatar sourceAvatar = null;
            AnimationClip clip = null;
            BodyAnimationMetadata metadata = null;
            try
            {
                sourceAvatar = BuildSourceAvatar(source, document.Json);
                if (sourceAvatar == null || !sourceAvatar.isValid || !sourceAvatar.isHuman)
                    throw new InvalidDataException("The embedded SOMA definition did not produce a valid Unity Humanoid source Avatar.");

                clip = Bake(document, source, sourceAvatar, options);
                AssetDatabase.CreateAsset(clip, output);
                clip = null;

                metadata = BuildMetadata(document, resolvedSource, options.Loop);
                metadata.name = Path.GetFileNameWithoutExtension(output) + " Bodyanim Metadata";
                AssetDatabase.AddObjectToAsset(metadata, output);
                metadata = null;
                AssetDatabase.SaveAssets();

                var importedClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(output)
                    ?? throw new InvalidOperationException($"Unity did not load the imported clip at '{output}'.");
                var bindings = AnimationUtility.GetCurveBindings(importedClip);
                int facialBindings = bindings.Count(IsFacialBinding);
                var result = new Result
                {
                    SourcePath = resolvedSource,
                    ClipPath = output,
                    MetadataSubAsset = metadataName(output),
                    SkeletonId = (string)document.Json["skeleton"]["id"],
                    SkeletonDefinitionId = (string)document.Json["skeleton"]["definition"]["id"],
                    SkeletonDefinitionSha256 = (string)document.Json["skeleton"]["definition_sha256"],
                    JointOrderSha256 = (string)document.Json["skeleton"]["joint_order_sha256"],
                    RootMotionPolicy = document.RootMotionPolicy,
                    Provider = (string)document.Json["provenance"]?["provider"],
                    Model = (string)document.Json["provenance"]?["model"],
                    FrameCount = document.Frames.Count,
                    FramesPerSecond = document.Fps,
                    DurationSeconds = document.Duration,
                    Loop = options.Loop,
                    HumanoidBindings = bindings.Length,
                    FacialBindings = facialBindings,
                    BoundaryMeanDegrees = document.BoundaryMeanDegrees,
                    BoundaryMaximumDegrees = document.BoundaryMaximumDegrees,
                    BoundaryMaximumJoint = document.BoundaryMaximumJoint,
                };
                if (options.Loop && document.BoundaryMaximumDegrees > 10f)
                    result.Warnings.Add(
                        $"Imported as a loop, but the provider endpoints differ by up to " +
                        $"{document.BoundaryMaximumDegrees:F1} degrees at {document.BoundaryMaximumJoint}. " +
                        "Use a transition/loop-conditioning pass before treating it as a production cycle.");
                return result;
            }
            catch
            {
                if (AssetDatabase.LoadMainAssetAtPath(output) != null)
                    AssetDatabase.DeleteAsset(output);
                throw;
            }
            finally
            {
                if (metadata != null) UnityEngine.Object.DestroyImmediate(metadata);
                if (clip != null) UnityEngine.Object.DestroyImmediate(clip);
                if (sourceAvatar != null) UnityEngine.Object.DestroyImmediate(sourceAvatar);
                UnityEngine.Object.DestroyImmediate(source);
            }

            string metadataName(string path) => Path.GetFileNameWithoutExtension(path) + " Bodyanim Metadata";
        }

        public static Result Inspect(string sourcePath)
        {
            var resolved = ResolveSourcePath(sourcePath);
            var document = ParseAndValidate(File.ReadAllText(resolved));
            return new Result
            {
                SourcePath = resolved,
                SkeletonId = (string)document.Json["skeleton"]["id"],
                SkeletonDefinitionId = (string)document.Json["skeleton"]["definition"]["id"],
                SkeletonDefinitionSha256 = (string)document.Json["skeleton"]["definition_sha256"],
                JointOrderSha256 = (string)document.Json["skeleton"]["joint_order_sha256"],
                RootMotionPolicy = document.RootMotionPolicy,
                Provider = (string)document.Json["provenance"]?["provider"],
                Model = (string)document.Json["provenance"]?["model"],
                FrameCount = document.Frames.Count,
                FramesPerSecond = document.Fps,
                DurationSeconds = document.Duration,
                BoundaryMeanDegrees = document.BoundaryMeanDegrees,
                BoundaryMaximumDegrees = document.BoundaryMaximumDegrees,
                BoundaryMaximumJoint = document.BoundaryMaximumJoint,
            };
        }

        static Document ParseAndValidate(string json)
        {
            JObject body;
            try { body = JObject.Parse(json); }
            catch (Exception exception) { throw new InvalidDataException("Body animation is not valid JSON.", exception); }

            if ((string)body["schema"] != Schema || (int?)body["version"] != SchemaVersion)
                throw new InvalidDataException($"Unsupported body animation contract. Expected {Schema} version {SchemaVersion}.");

            float fps = RequireFinitePositive(body["fps"], "fps");
            float duration = RequireFinitePositive(body["duration"], "duration");
            var skeleton = body["skeleton"] as JObject
                ?? throw new InvalidDataException("Body animation has no skeleton object.");
            if ((string)skeleton["type"] != "soma"
                || (string)skeleton["id"] != SomaSkeletonId
                || (int?)skeleton["joint_count"] != 77
                || (string)skeleton["joint_order_sha256"] != SomaJointOrderSha256)
                throw new InvalidDataException("Unsupported SOMA skeleton identity or joint order.");
            if ((string)skeleton["definition_sha256"] != SomaDefinitionSha256
                || (string)body["provenance"]?["skeleton_definition_sha256"] != SomaDefinitionSha256)
                throw new InvalidDataException("Unknown or inconsistent SOMA skeleton definition hash.");

            var coordinates = body["coordinate_system"] as JObject
                ?? throw new InvalidDataException("Body animation has no coordinate_system object.");
            if ((string)coordinates["handedness"] != "right"
                || (string)coordinates["up_axis"] != "+Y"
                || (string)coordinates["forward_axis"] != "+Z"
                || (string)coordinates["units"] != "meters"
                || (string)coordinates["quaternion_order"] != "xyzw"
                || (string)coordinates["rotation_space"] != "local_parent_relative"
                || (string)coordinates["root_translation_space"] != "skeleton_global")
                throw new InvalidDataException("Unsupported body animation coordinate contract.");

            var definition = skeleton["definition"] as JObject
                ?? throw new InvalidDataException("The authoritative embedded SOMA definition is required.");
            if ((string)definition["schema"] != "character-factory/skeleton-definition"
                || (int?)definition["version"] != 1
                || (string)definition["id"] != SomaDefinitionId
                || (string)definition["rest_pose"] != "soma_standard_tpose"
                || (string)definition["virtual_root"] != "Root"
                || (string)definition["root_joint"] != "Hips")
                throw new InvalidDataException("Unsupported embedded SOMA definition.");

            var rest = definition["rest"] as JObject
                ?? throw new InvalidDataException("The embedded SOMA definition has no rest basis.");
            if ((string)rest["translation_space"] != "local_parent_relative"
                || (string)rest["rotation_space"] != "local_parent_relative"
                || (string)rest["translation_units"] != "meters"
                || (string)rest["quaternion_order"] != "xyzw"
                || (string)rest["root_frame_semantics"] != "frame root_position replaces the Root-to-Hips rest translation")
                throw new InvalidDataException("Unsupported SOMA rest-basis contract.");

            var joints = RequireArray(body, "joints", 77);
            var outerHierarchy = RequireArray(skeleton, "hierarchy", 77);
            var hierarchy = RequireArray(definition, "hierarchy", 77);
            var translations = RequireArray(rest, "translations", 77);
            var rotations = RequireArray(rest, "rotations", 77);
            var jointNames = joints.Values<string>().ToArray();
            if (jointNames.Any(string.IsNullOrWhiteSpace) || jointNames.Distinct(StringComparer.Ordinal).Count() != 77)
                throw new InvalidDataException("SOMA joints must contain 77 unique non-empty names.");

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < 77; i++)
            {
                string name = (string)hierarchy[i]?["name"];
                string parent = (string)hierarchy[i]?["parent"];
                if (jointNames[i] != name
                    || (string)outerHierarchy[i]?["name"] != name
                    || (string)outerHierarchy[i]?["parent"] != parent)
                    throw new InvalidDataException($"SOMA joint/hierarchy mismatch at index {i}.");
                if (parent != null && !seen.Contains(parent))
                    throw new InvalidDataException($"SOMA joint '{name}' refers to missing or later parent '{parent}'.");
                seen.Add(name);
                RequireVector(translations[i], 3, $"skeleton.definition.rest.translations[{i}]");
                RequireQuaternion(rotations[i], $"skeleton.definition.rest.rotations[{i}]");
            }

            var mapping = definition["semantic_mapping"] as JObject;
            var roles = mapping?["roles"] as JObject;
            if ((string)mapping?["id"] != "character-factory/humanoid-soma77"
                || (int?)mapping?["version"] != 1 || roles?.Properties().Count() != 55)
                throw new InvalidDataException("Unsupported SOMA Humanoid semantic mapping.");
            ValidateHumanoidRoles(roles, seen);

            var frames = body["frames"] as JArray;
            if (frames == null || frames.Count == 0)
                throw new InvalidDataException("Body animation has no frames.");
            if (Mathf.Abs(frames.Count - duration * fps) > .01f)
                throw new InvalidDataException(
                    $"Frame count {frames.Count} does not match duration {duration.ToString(CultureInfo.InvariantCulture)} " +
                    $"at {fps.ToString(CultureInfo.InvariantCulture)} fps.");
            float previousTime = -1f;
            for (int frameIndex = 0; frameIndex < frames.Count; frameIndex++)
            {
                var frame = frames[frameIndex] as JObject
                    ?? throw new InvalidDataException($"frames[{frameIndex}] is not an object.");
                float time = RequireFinite(frame["time"], $"frames[{frameIndex}].time");
                float expected = frameIndex / fps;
                if (Mathf.Abs(time - expected) > .0001f)
                    throw new InvalidDataException($"frames[{frameIndex}].time is inconsistent with fps.");
                if (time <= previousTime)
                    throw new InvalidDataException("Frame times must be strictly increasing.");
                previousTime = time;
                RequireVector(frame["root_position"], 3, $"frames[{frameIndex}].root_position");
                var frameRotations = RequireArray(frame, "rotations", 77);
                for (int jointIndex = 0; jointIndex < 77; jointIndex++)
                    RequireQuaternion(frameRotations[jointIndex], $"frames[{frameIndex}].rotations[{jointIndex}]");
            }

            string requestedPolicy = (string)body["auxiliary"]?["root_motion"]?["requested_policy"];
            string appliedPolicy = (string)body["auxiliary"]?["root_motion"]?["applied_policy"];
            if (requestedPolicy != appliedPolicy || !new[] { "preserve", "in_place", "strip_horizontal" }.Contains(appliedPolicy))
                throw new InvalidDataException("Root-motion policy is missing, unsupported, or was not applied as requested.");
            if (appliedPolicy != "preserve")
                ValidateNoHorizontalTravel(frames);

            ValidateContacts(body, frames.Count);
            MeasureBoundary(frames, jointNames, out var boundaryMean, out var boundaryMax, out var boundaryJoint);
            return new Document
            {
                Json = body,
                Joints = jointNames,
                Frames = frames,
                RootMotionPolicy = appliedPolicy,
                Fps = fps,
                Duration = duration,
                BoundaryMeanDegrees = boundaryMean,
                BoundaryMaximumDegrees = boundaryMax,
                BoundaryMaximumJoint = boundaryJoint,
            };
        }

        static GameObject BuildSourceHierarchy(Document document)
        {
            var definition = (JObject)document.Json["skeleton"]["definition"];
            var hierarchy = (JArray)definition["hierarchy"];
            var translations = (JArray)definition["rest"]["translations"];
            var rotations = (JArray)definition["rest"]["rotations"];
            var root = new GameObject((string)definition["virtual_root"])
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            var byName = new Dictionary<string, Transform>(StringComparer.Ordinal);
            for (int i = 0; i < hierarchy.Count; i++)
            {
                string name = (string)hierarchy[i]["name"];
                string parent = (string)hierarchy[i]["parent"];
                var go = new GameObject(name);
                go.transform.SetParent(parent == null ? root.transform : byName[parent], false);
                go.transform.localPosition = ConvertPosition(ReadVector3(translations[i]));
                go.transform.localRotation = ConvertRotation(ReadQuaternion(rotations[i]));
                byName.Add(name, go.transform);
            }
            return root;
        }

        static Avatar BuildSourceAvatar(GameObject root, JObject body)
        {
            var roles = (JObject)body["skeleton"]["definition"]["semantic_mapping"]["roles"];
            var humanNames = HumanTrait.BoneName.ToDictionary(NormalizeRole, n => n);
            var human = roles.Properties().Select(role =>
            {
                if (!humanNames.TryGetValue(NormalizeRole(role.Name), out var humanName))
                    throw new InvalidDataException($"Unknown Unity Humanoid role '{role.Name}'.");
                var bone = new HumanBone { humanName = humanName, boneName = (string)role.Value };
                bone.limit.useDefaultValues = true;
                return bone;
            }).ToArray();
            var skeleton = root.GetComponentsInChildren<Transform>(true).Select(transform => new SkeletonBone
            {
                name = transform.name,
                position = transform.localPosition,
                rotation = transform.localRotation,
                scale = transform.localScale,
            }).ToArray();
            return AvatarBuilder.BuildHumanAvatar(root, new HumanDescription
            {
                human = human,
                skeleton = skeleton,
                upperArmTwist = .5f,
                lowerArmTwist = .5f,
                upperLegTwist = .5f,
                lowerLegTwist = .5f,
                armStretch = .05f,
                legStretch = .05f,
                feetSpacing = 0f,
                hasTranslationDoF = false,
            });
        }

        static AnimationClip Bake(Document document, GameObject source, Avatar avatar, Options options)
        {
            var transforms = source.GetComponentsInChildren<Transform>(true)
                .ToDictionary(transform => transform.name, StringComparer.Ordinal);
            var muscleKeys = HumanTrait.MuscleName.ToDictionary(name => name, _ => new List<Keyframe>());
            var rootPositionKeys = Enumerable.Range(0, 3).Select(_ => new List<Keyframe>()).ToArray();
            var rootRotationKeys = Enumerable.Range(0, 4).Select(_ => new List<Keyframe>()).ToArray();
            var handler = new HumanPoseHandler(avatar, source.transform);
            var pose = new HumanPose();
            Quaternion previousRotation = Quaternion.identity;
            Vector3 firstBodyPosition = Vector3.zero;
            try
            {
                for (int frameIndex = 0; frameIndex < document.Frames.Count; frameIndex++)
                {
                    var frame = document.Frames[frameIndex];
                    float time = (float)frame["time"];
                    ApplyFrame(frame, document.Joints, transforms);
                    handler.GetHumanPose(ref pose);
                    if (frameIndex == 0) firstBodyPosition = pose.bodyPosition;
                    for (int muscle = 0; muscle < HumanTrait.MuscleCount; muscle++)
                        muscleKeys[HumanTrait.MuscleName[muscle]].Add(new Keyframe(time, pose.muscles[muscle]));

                    var position = pose.bodyPosition;
                    position.x -= firstBodyPosition.x;
                    position.z -= firstBodyPosition.z;
                    for (int axis = 0; axis < 3; axis++)
                        rootPositionKeys[axis].Add(new Keyframe(time, position[axis]));

                    var rotation = pose.bodyRotation;
                    if (frameIndex > 0 && Quaternion.Dot(previousRotation, rotation) < 0f)
                        rotation = new Quaternion(-rotation.x, -rotation.y, -rotation.z, -rotation.w);
                    previousRotation = rotation;
                    for (int component = 0; component < 4; component++)
                        rootRotationKeys[component].Add(new Keyframe(time, rotation[component]));
                }
            }
            finally { handler.Dispose(); }

            var clip = new AnimationClip
            {
                name = string.IsNullOrWhiteSpace(options.ClipName)
                    ? "BodyAnimation"
                    : options.ClipName,
                frameRate = document.Fps,
            };
            foreach (var pair in muscleKeys)
            {
                if (IsFacialProperty(pair.Key)) continue;
                clip.SetCurve("", typeof(Animator), pair.Key,
                    CreateBoundaryCurve(pair.Value, document.Duration, options.Loop));
            }
            clip.SetCurve("", typeof(Animator), "RootT.x", CreateBoundaryCurve(rootPositionKeys[0], document.Duration, options.Loop));
            clip.SetCurve("", typeof(Animator), "RootT.y", CreateBoundaryCurve(rootPositionKeys[1], document.Duration, options.Loop));
            clip.SetCurve("", typeof(Animator), "RootT.z", CreateBoundaryCurve(rootPositionKeys[2], document.Duration, options.Loop));
            float loopQuaternionSign = 1f;
            if (options.Loop)
            {
                float endDot = 0f;
                for (int component = 0; component < 4; component++)
                    endDot += rootRotationKeys[component][0].value
                        * rootRotationKeys[component][rootRotationKeys[component].Count - 1].value;
                if (endDot < 0f) loopQuaternionSign = -1f;
            }
            clip.SetCurve("", typeof(Animator), "RootQ.x", CreateBoundaryCurve(rootRotationKeys[0], document.Duration, options.Loop, loopQuaternionSign));
            clip.SetCurve("", typeof(Animator), "RootQ.y", CreateBoundaryCurve(rootRotationKeys[1], document.Duration, options.Loop, loopQuaternionSign));
            clip.SetCurve("", typeof(Animator), "RootQ.z", CreateBoundaryCurve(rootRotationKeys[2], document.Duration, options.Loop, loopQuaternionSign));
            clip.SetCurve("", typeof(Animator), "RootQ.w", CreateBoundaryCurve(rootRotationKeys[3], document.Duration, options.Loop, loopQuaternionSign));
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = options.Loop;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            return clip;
        }

        static BodyAnimationMetadata BuildMetadata(Document document, string sourcePath, bool loop)
        {
            var body = document.Json;
            var metadata = ScriptableObject.CreateInstance<BodyAnimationMetadata>();
            metadata.SourceArtifact = sourcePath;
            metadata.Schema = (string)body["schema"];
            metadata.SchemaVersion = (int)body["version"];
            metadata.SkeletonId = (string)body["skeleton"]["id"];
            metadata.SkeletonVersion = (string)body["skeleton"]["version"];
            metadata.SkeletonDefinitionId = (string)body["skeleton"]["definition"]["id"];
            metadata.SkeletonDefinitionVersion = (int)body["skeleton"]["definition"]["version"];
            metadata.SkeletonDefinitionSha256 = (string)body["skeleton"]["definition_sha256"];
            metadata.JointOrderSha256 = (string)body["skeleton"]["joint_order_sha256"];
            metadata.Provider = (string)body["provenance"]?["provider"];
            metadata.Model = (string)body["provenance"]?["model"];
            metadata.Prompt = (string)body["provenance"]?["prompt"];
            metadata.FramesPerSecond = document.Fps;
            metadata.DurationSeconds = document.Duration;
            metadata.FrameCount = document.Frames.Count;
            metadata.RootMotionPolicy = document.RootMotionPolicy;
            metadata.SourceHorizontalDisplacementMeters =
                (float?)body["auxiliary"]?["root_motion"]?["source_horizontal_displacement_meters"] ?? 0f;
            metadata.RecommendedBlendInSeconds =
                (float?)body["boundary"]?["recommended_blend_in_seconds"] ?? 0f;
            metadata.RecommendedBlendOutSeconds =
                (float?)body["boundary"]?["recommended_blend_out_seconds"] ?? 0f;
            metadata.ImportedAsLoop = loop;
            metadata.BoundaryMeanDegrees = document.BoundaryMeanDegrees;
            metadata.BoundaryMaximumDegrees = document.BoundaryMaximumDegrees;
            metadata.BoundaryMaximumJoint = document.BoundaryMaximumJoint;
            var loopConditioning = body["boundary"]?["loop_conditioning"];
            metadata.LoopConditioned = loopConditioning != null;
            metadata.LoopConditioningProcessor = (string)loopConditioning?["processor"];
            metadata.BoundaryVelocityMeanDegreesPerFrame =
                (float?)loopConditioning?["boundary_velocity_mean_degrees_per_frame"] ?? 0f;
            metadata.BoundaryVelocityMaximumDegreesPerFrame =
                (float?)loopConditioning?["boundary_velocity_max_degrees_per_frame"] ?? 0f;

            var contacts = body["auxiliary"]?["foot_contacts"];
            metadata.FootContactChannels = (contacts?["channels"] as JArray)?.Values<string>().ToArray()
                ?? Array.Empty<string>();
            var contactValues = contacts?["values"] as JArray;
            if (contactValues == null)
            {
                metadata.FootContacts = Array.Empty<BodyAnimationMetadata.ContactSample>();
            }
            else
            {
                metadata.FootContacts = contactValues.Select((values, index) =>
                    new BodyAnimationMetadata.ContactSample
                    {
                        Time = (float)document.Frames[index]["time"],
                        Values = values.Values<float>().ToArray(),
                    }).ToArray();
            }
            return metadata;
        }

        static void ApplyFrame(JToken frame, string[] joints, Dictionary<string, Transform> transforms)
        {
            transforms["Hips"].localPosition = ConvertPosition(ReadVector3(frame["root_position"]));
            var rotations = (JArray)frame["rotations"];
            for (int joint = 0; joint < joints.Length; joint++)
                transforms[joints[joint]].localRotation = ConvertRotation(ReadQuaternion(rotations[joint]));
        }

        static AnimationCurve CreateBoundaryCurve(List<Keyframe> source, float duration, bool loop,
            float loopValueSign = 1f)
        {
            var keys = new List<Keyframe>(source);
            if (keys.Count == 0) throw new InvalidDataException("Cannot create an empty animation curve.");
            if (duration > keys[keys.Count - 1].time)
            {
                // bodyanim frames are samples held until the following frame. For a loop, the
                // sample at exactly duration is therefore the first frame of the next cycle, not
                // another copy of the final source sample. Authoring that boundary key makes the
                // imported curve obey the format's timing semantics and avoids a one-frame hitch.
                float boundaryValue = loop
                    ? keys[0].value * loopValueSign
                    : keys[keys.Count - 1].value;
                keys.Add(new Keyframe(duration, boundaryValue));
            }
            var curve = new AnimationCurve(keys.ToArray());
            curve.preWrapMode = loop ? WrapMode.Loop : WrapMode.ClampForever;
            curve.postWrapMode = loop ? WrapMode.Loop : WrapMode.ClampForever;
            for (int i = 0; i < curve.length; i++)
            {
                AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
                AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
            }
            return curve;
        }

        static void ValidateHumanoidRoles(JObject roles, HashSet<string> joints)
        {
            var humanNames = new HashSet<string>(HumanTrait.BoneName.Select(NormalizeRole));
            var mappedHumanRoles = new HashSet<string>();
            foreach (var role in roles.Properties())
            {
                string normalized = NormalizeRole(role.Name);
                if (!humanNames.Contains(normalized))
                    throw new InvalidDataException($"Unknown Unity Humanoid role '{role.Name}'.");
                if (!mappedHumanRoles.Add(normalized))
                    throw new InvalidDataException($"Duplicate Unity Humanoid role '{role.Name}'.");
                if (!joints.Contains((string)role.Value))
                    throw new InvalidDataException($"Humanoid role '{role.Name}' maps to unknown joint '{role.Value}'.");
            }
        }

        static void ValidateNoHorizontalTravel(JArray frames)
        {
            var first = ReadVector3(frames[0]["root_position"]);
            for (int i = 1; i < frames.Count; i++)
            {
                var current = ReadVector3(frames[i]["root_position"]);
                if (Mathf.Abs(current.x - first.x) > 1e-5f || Mathf.Abs(current.z - first.z) > 1e-5f)
                    throw new InvalidDataException("A horizontal-stripped root-motion artifact still contains horizontal travel.");
            }
        }

        static void ValidateContacts(JObject body, int frameCount)
        {
            var contacts = body["auxiliary"]?["foot_contacts"];
            if (contacts == null) return;
            var channels = contacts["channels"] as JArray;
            var values = contacts["values"] as JArray;
            if (channels == null || values == null || channels.Count == 0 || values.Count != frameCount)
                throw new InvalidDataException("Foot-contact channels must contain one value row per body frame.");
            for (int frame = 0; frame < values.Count; frame++)
            {
                var row = values[frame] as JArray;
                if (row == null || row.Count != channels.Count)
                    throw new InvalidDataException($"Foot-contact row {frame} does not match the channel count.");
                foreach (var value in row)
                {
                    float number = RequireFinite(value, $"foot_contacts.values[{frame}]");
                    if (number < 0f || number > 1f)
                        throw new InvalidDataException("Foot-contact values must be normalized to 0..1.");
                }
            }
        }

        static void MeasureBoundary(JArray frames, string[] joints,
            out float meanDegrees, out float maximumDegrees, out string maximumJoint)
        {
            var first = (JArray)frames[0]["rotations"];
            var last = (JArray)frames[frames.Count - 1]["rotations"];
            float sum = 0f;
            maximumDegrees = 0f;
            maximumJoint = joints[0];
            for (int i = 0; i < joints.Length; i++)
            {
                float angle = Quaternion.Angle(ReadQuaternion(first[i]), ReadQuaternion(last[i]));
                sum += angle;
                if (angle > maximumDegrees)
                {
                    maximumDegrees = angle;
                    maximumJoint = joints[i];
                }
            }
            meanDegrees = sum / joints.Length;
        }

        static JArray RequireArray(JToken parent, string name, int count)
        {
            var array = parent?[name] as JArray;
            if (array == null || array.Count != count)
                throw new InvalidDataException($"'{name}' must contain exactly {count} entries.");
            return array;
        }

        static void RequireVector(JToken token, int count, string path)
        {
            var array = token as JArray;
            if (array == null || array.Count != count)
                throw new InvalidDataException($"'{path}' must contain exactly {count} finite numbers.");
            foreach (var value in array) RequireFinite(value, path);
        }

        static void RequireQuaternion(JToken token, string path)
        {
            RequireVector(token, 4, path);
            var quaternion = ReadQuaternion(token);
            float magnitude = Mathf.Sqrt(quaternion.x * quaternion.x + quaternion.y * quaternion.y
                + quaternion.z * quaternion.z + quaternion.w * quaternion.w);
            if (Mathf.Abs(magnitude - 1f) > .002f)
                throw new InvalidDataException($"'{path}' is not a normalized quaternion.");
        }

        static float RequireFinitePositive(JToken token, string path)
        {
            float value = RequireFinite(token, path);
            if (value <= 0f) throw new InvalidDataException($"'{path}' must be positive.");
            return value;
        }

        static float RequireFinite(JToken token, string path)
        {
            if (token == null || token.Type != JTokenType.Float && token.Type != JTokenType.Integer)
                throw new InvalidDataException($"'{path}' must be a finite number.");
            float value = (float)token;
            if (float.IsNaN(value) || float.IsInfinity(value))
                throw new InvalidDataException($"'{path}' must be a finite number.");
            return value;
        }

        static Vector3 ReadVector3(JToken token)
        {
            var values = token.Values<float>().ToArray();
            return new Vector3(values[0], values[1], values[2]);
        }

        static Quaternion ReadQuaternion(JToken token)
        {
            var values = token.Values<float>().ToArray();
            return new Quaternion(values[0], values[1], values[2], values[3]);
        }

        static Vector3 ConvertPosition(Vector3 rightHanded) =>
            new Vector3(-rightHanded.x, rightHanded.y, rightHanded.z);

        static Quaternion ConvertRotation(Quaternion rightHanded) =>
            new Quaternion(rightHanded.x, -rightHanded.y, -rightHanded.z, rightHanded.w).normalized;

        static string NormalizeRole(string value) =>
            new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray())
                .Replace("pinky", "little");

        static bool IsFacialProperty(string property) =>
            property == "Jaw Close" || property.StartsWith("Left Eye", StringComparison.Ordinal)
                || property.StartsWith("Right Eye", StringComparison.Ordinal);

        static bool IsFacialBinding(EditorCurveBinding binding) =>
            binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal)
                || IsFacialProperty(binding.propertyName);

        static string ResolveSourcePath(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
                throw new ArgumentException("Body animation source path is empty.", nameof(sourcePath));
            string candidate = sourcePath;
            if (!Path.IsPathRooted(candidate))
            {
                string projectRoot = Directory.GetParent(Application.dataPath).FullName;
                candidate = Path.Combine(projectRoot, candidate);
            }
            candidate = Path.GetFullPath(candidate);
            if (!File.Exists(candidate))
                throw new FileNotFoundException("Body animation artifact was not found.", candidate);
            return candidate;
        }

        static string NormalizeOutputPath(string outputAssetPath)
        {
            if (string.IsNullOrWhiteSpace(outputAssetPath))
                throw new ArgumentException("Output asset path is empty.", nameof(outputAssetPath));
            string normalized = outputAssetPath.Replace('\\', '/');
            if (!normalized.StartsWith("Assets/", StringComparison.Ordinal) || !normalized.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Output must be a project-relative .anim path under Assets/.", nameof(outputAssetPath));
            return normalized;
        }

        static void EnsureOutputFolder(string outputAssetPath)
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string folder = Path.GetDirectoryName(Path.Combine(projectRoot, outputAssetPath));
            Directory.CreateDirectory(folder);
            AssetDatabase.Refresh();
        }
    }
}

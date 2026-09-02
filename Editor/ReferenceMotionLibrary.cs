using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CharacterFactory.Core;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace CharacterFactory.Editor
{
    /// <summary>Builds and validates the optional, explicitly non-production reference setup.</summary>
    public static class ReferenceMotionLibrary
    {
        public const string SourceLabel = "Kimodo-SOMA-RP-v1.1 generated reference motion";
        const string Root = "Packages/com.character-factory.unity/Editor/ReferenceAnimations";

        public sealed class Motions
        {
            public AnimationClip Idle;
            public AnimationClip Talking;
            public AnimationClip Walk;
            public AnimationClip Jog;
            public AnimationClip Run;
            public AnimationClip Interact;

            public IEnumerable<AnimationClip> All =>
                new[] { Idle, Talking, Walk, Jog, Run, Interact };
        }

        public static Motions Load()
        {
            AnimationClip Clip(string name)
            {
                var path = $"{Root}/CF_Reference{name}.anim";
                return AssetDatabase.LoadAssetAtPath<AnimationClip>(path)
                    ?? throw new FileNotFoundException($"Reference motion asset is missing: {path}");
            }
            return new Motions
            {
                Idle = Clip("Idle"), Talking = Clip("Talking"), Walk = Clip("Walk"),
                Jog = Clip("Jog"), Run = Clip("Run"), Interact = Clip("Interact"),
            };
        }

        public static AnimatorController BuildController(string path, Motions motions)
        {
            // The controller is generated output and its prefab is regenerated in the same pass.
            // Keep the optional reference controller distinct from the canonical prefab.
            if (AssetDatabase.LoadAssetAtPath<AnimatorController>(path) != null)
                AssetDatabase.DeleteAsset(path);
            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            controller.AddParameter(ReferenceMotionDriver.MoveSpeedParameter, AnimatorControllerParameterType.Float);
            controller.AddParameter(ReferenceMotionDriver.TalkingParameter, AnimatorControllerParameterType.Bool);
            controller.AddParameter(ReferenceMotionDriver.InteractParameter, AnimatorControllerParameterType.Trigger);

            var machine = controller.layers[0].stateMachine;
            machine.name = "Reference full-body motion";
            var locomotion = machine.AddState("Locomotion");
            var tree = new BlendTree
            {
                name = "Reference Locomotion",
                blendType = BlendTreeType.Simple1D,
                blendParameter = ReferenceMotionDriver.MoveSpeedParameter,
                useAutomaticThresholds = false,
            };
            AssetDatabase.AddObjectToAsset(tree, controller);
            tree.AddChild(motions.Idle, 0f);
            tree.AddChild(motions.Walk, 1f);
            tree.AddChild(motions.Jog, 2f);
            tree.AddChild(motions.Run, 3f);
            locomotion.motion = tree;
            machine.defaultState = locomotion;

            var talking = machine.AddState("Talking idle");
            talking.motion = motions.Talking;
            var toTalking = locomotion.AddTransition(talking);
            toTalking.hasExitTime = false;
            toTalking.duration = .2f;
            toTalking.AddCondition(AnimatorConditionMode.If, 0f, ReferenceMotionDriver.TalkingParameter);
            var fromTalking = talking.AddTransition(locomotion);
            fromTalking.hasExitTime = false;
            fromTalking.duration = .2f;
            fromTalking.AddCondition(AnimatorConditionMode.IfNot, 0f, ReferenceMotionDriver.TalkingParameter);

            var interact = machine.AddState("Interact");
            interact.motion = motions.Interact;
            var toInteract = machine.AddAnyStateTransition(interact);
            toInteract.hasExitTime = false;
            toInteract.duration = .12f;
            toInteract.AddCondition(AnimatorConditionMode.If, 0f, ReferenceMotionDriver.InteractParameter);
            var fromInteract = interact.AddTransition(locomotion);
            fromInteract.hasExitTime = true;
            fromInteract.exitTime = .95f;
            fromInteract.duration = .15f;

            EditorUtility.SetDirty(controller);
            return controller;
        }

        /// <summary>
        /// Rejects the exact failure mode found in the vertical slice: structurally Humanoid
        /// clips that omit legs and therefore collapse into the Avatar's muscle-zero crouch.
        /// </summary>
        public static List<string> Validate(GameObject modelPrefab, Avatar avatar, ExportManifest manifest, Motions motions)
        {
            var evidence = new List<string>();
            var required = new[]
            {
                "Left Upper Leg Front-Back", "Right Upper Leg Front-Back",
                "Left Lower Leg Stretch", "Right Lower Leg Stretch",
                "Left Foot Up-Down", "Right Foot Up-Down",
                "Spine Front-Back", "Chest Front-Back",
                "Left Arm Front-Back", "Right Arm Front-Back",
            };
            foreach (var clip in motions.All)
            {
                var bindings = AnimationUtility.GetCurveBindings(clip);
                var properties = bindings.Select(b => b.propertyName).ToHashSet(StringComparer.Ordinal);
                var missing = required.Where(r => !properties.Contains(r)).ToArray();
                if (!clip.isHumanMotion || missing.Length > 0)
                    throw new InvalidDataException(
                        $"Reference clip '{clip.name}' is not a complete Humanoid body motion; missing: {string.Join(", ", missing)}.");
                if (bindings.Any(b => b.propertyName.StartsWith("blendShape.", StringComparison.Ordinal)
                    || b.propertyName == "Jaw Close" || b.propertyName.StartsWith("Left Eye", StringComparison.Ordinal)
                    || b.propertyName.StartsWith("Right Eye", StringComparison.Ordinal)))
                    throw new InvalidDataException($"Reference clip '{clip.name}' owns facial animation properties.");

                bool expectedLoop = clip != motions.Interact;
                var settings = AnimationUtility.GetAnimationClipSettings(clip);
                if (settings.loopTime != expectedLoop)
                    throw new InvalidDataException($"Reference clip '{clip.name}' has the wrong loop setting.");
                var metadata = AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(clip))
                    .OfType<BodyAnimationMetadata>().SingleOrDefault()
                    ?? throw new InvalidDataException($"Reference clip '{clip.name}' has no bodyanim provenance metadata.");
                if (expectedLoop)
                {
                    if (!metadata.ImportedAsLoop || !metadata.LoopConditioned)
                        throw new InvalidDataException($"Reference clip '{clip.name}' is not a conditioned bodyanim loop.");
                    if (metadata.BoundaryMaximumDegrees > 16f
                        || metadata.BoundaryVelocityMaximumDegreesPerFrame > 5f)
                        throw new InvalidDataException(
                            $"Reference clip '{clip.name}' has an excessive cyclic seam: "
                            + $"{metadata.BoundaryMaximumDegrees:F1} degrees, "
                            + $"{metadata.BoundaryVelocityMaximumDegreesPerFrame:F1} degrees/frame velocity mismatch.");
                    float endpointDelta = bindings.Select(binding =>
                    {
                        var curve = AnimationUtility.GetEditorCurve(clip, binding);
                        return Mathf.Abs(curve.Evaluate(0f) - curve.Evaluate(clip.length));
                    }).Max();
                    if (endpointDelta > .0001f)
                        throw new InvalidDataException(
                            $"Reference clip '{clip.name}' does not author an exact next-cycle boundary key.");
                }
            }

            float minIdleKnee = 180f;
            float maxIdleKnee = 0f;
            float minMotionKnee = 180f;
            float minGeometry = float.PositiveInfinity;
            foreach (var clip in motions.All)
            foreach (var fraction in new[] { 0f, .125f, .25f, .375f, .5f, .625f, .75f, .875f })
            {
                var instance = UnityEngine.Object.Instantiate(modelPrefab);
                try
                {
                    instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                    var animator = instance.GetComponent<Animator>() ?? instance.AddComponent<Animator>();
                    animator.avatar = avatar;
                    clip.SampleAnimation(instance, clip.length * fraction);
                    float left = Knee(instance, "l_upleg", "l_lowleg", "l_foot");
                    float right = Knee(instance, "r_upleg", "r_lowleg", "r_foot");
                    minMotionKnee = Mathf.Min(minMotionKnee, left, right);
                    if (clip == motions.Idle)
                    {
                        minIdleKnee = Mathf.Min(minIdleKnee, left, right);
                        maxIdleKnee = Mathf.Max(maxIdleKnee, left, right);
                        minGeometry = Mathf.Min(minGeometry, LowestGeometryY(instance));
                    }
                }
                finally { UnityEngine.Object.DestroyImmediate(instance); }
            }
            if (minIdleKnee < 120f)
                throw new InvalidDataException($"Reference idle collapses the knees to {minIdleKnee:F1} degrees.");
            if (minMotionKnee < 35f)
                throw new InvalidDataException($"Reference locomotion contains an implausible {minMotionKnee:F1}-degree knee pose.");
            float groundError = Mathf.Abs(minGeometry - manifest.Grounding.PlaneHeightM);
            float tolerance = Mathf.Max(.015f, manifest.Grounding.IdleGroundToleranceM);
            if (groundError > tolerance)
                throw new InvalidDataException(
                    $"Reference idle geometry misses the manifest plane by {groundError * 1000f:F1} mm (tolerance {tolerance * 1000f:F1} mm).");

            evidence.Add($"reference idle knees {minIdleKnee:F1}–{maxIdleKnee:F1} degrees");
            evidence.Add($"all reference-motion knees >= {minMotionKnee:F1} degrees");
            evidence.Add($"reference idle ground error {groundError * 1000f:F1} mm");
            evidence.Add("reference motions contain full-body curves and no facial curves");
            evidence.Add("five cyclic motions have conditioned seams and exact boundary keys; Interact is a one-shot");
            return evidence;
        }

        static float Knee(GameObject root, string upper, string lower, string foot)
        {
            var map = root.GetComponentsInChildren<Transform>(true)
                .GroupBy(t => t.name).ToDictionary(g => g.Key, g => g.First());
            return Vector3.Angle(map[upper].position - map[lower].position,
                map[foot].position - map[lower].position);
        }

        static float LowestGeometryY(GameObject root)
        {
            float result = float.PositiveInfinity;
            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (renderer.sharedMesh == null) continue;
                var mesh = new Mesh();
                try
                {
                    renderer.BakeMesh(mesh);
                    foreach (var vertex in mesh.vertices)
                        result = Mathf.Min(result, root.transform.InverseTransformPoint(
                            renderer.transform.TransformPoint(vertex)).y);
                }
                finally { UnityEngine.Object.DestroyImmediate(mesh); }
            }
            if (float.IsInfinity(result))
                throw new InvalidDataException("Could not measure skinned geometry for reference-motion validation.");
            return result;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace CharacterFactory.Core
{
    /// <summary>
    /// Builds a Humanoid <see cref="Avatar"/> for an instantiated character-factory hierarchy.
    ///
    /// The rest pose ships as an A-pose, so before sampling the skeleton this straightens both
    /// arm chains into a T-pose (Unity's humanoid solver expects a near-T reference; without the
    /// straightening, retargeted arms droop). The straightening happens on the instance passed
    /// in — pass a throwaway instance, not your scene object.
    /// </summary>
    public static class AvatarFactory
    {
        public class Result
        {
            public Avatar Avatar;
            public bool FromManifestMap;
            public List<string> Warnings = new List<string>();
            public List<string> MissingBones = new List<string>();
        }

        /// <summary>
        /// Build a Humanoid avatar from a character instance. The instance's transform must be
        /// at identity; its pose is modified (T-pose straightening) in the process.
        /// </summary>
        public static Result Build(GameObject instance, ExportManifest manifest, bool mapJaw = false)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));

            var result = new Result();
            var mapping = HumanoidMapping.Resolve(manifest, mapJaw);
            result.FromManifestMap = mapping.FromManifest;
            result.Warnings.AddRange(mapping.Warnings);

            var byName = new Dictionary<string, Transform>();
            foreach (var t in instance.GetComponentsInChildren<Transform>(true))
                if (!byName.ContainsKey(t.name)) byName.Add(t.name, t);

            StraightenArmsToTPose(byName, result.Warnings);

            var humanBones = new List<HumanBone>();
            foreach (var kv in mapping.Map)
            {
                if (!byName.ContainsKey(kv.Value))
                {
                    result.MissingBones.Add(kv.Value);
                    continue;
                }
                var hb = new HumanBone { humanName = kv.Key, boneName = kv.Value };
                hb.limit.useDefaultValues = true;
                humanBones.Add(hb);
            }
            if (result.MissingBones.Count > 0)
                result.Warnings.Add($"Rig is missing {result.MissingBones.Count} mapped bone(s): {string.Join(", ", result.MissingBones)}. They were skipped.");

            var skeleton = instance.GetComponentsInChildren<Transform>(true)
                .Select(t => new SkeletonBone
                {
                    name = t.name,
                    position = t.localPosition,
                    rotation = t.localRotation,
                    scale = t.localScale,
                })
                .ToArray();

            var description = new HumanDescription
            {
                human = humanBones.ToArray(),
                skeleton = skeleton,
                upperArmTwist = 0.5f,
                lowerArmTwist = 0.5f,
                upperLegTwist = 0.5f,
                lowerLegTwist = 0.5f,
                armStretch = 0.05f,
                legStretch = 0.05f,
                feetSpacing = 0f,
                hasTranslationDoF = false,
            };

            var avatar = AvatarBuilder.BuildHumanAvatar(instance, description);
            if (avatar == null || !avatar.isValid || !avatar.isHuman)
                throw new InvalidOperationException(
                    "AvatarBuilder did not produce a valid human avatar. " +
                    (result.MissingBones.Count > 0
                        ? $"Missing bones: {string.Join(", ", result.MissingBones)}."
                        : "Check the Unity console for HumanDescription errors."));

            result.Avatar = avatar;
            return result;
        }

        /// <summary>
        /// Rotate each upper-arm and forearm so the arm chain points along world ±X (character
        /// faces +Z, so the anatomical left arm extends toward -X in Unity's left-handed space).
        /// </summary>
        static void StraightenArmsToTPose(Dictionary<string, Transform> byName, List<string> warnings)
        {
            foreach (var side in new[] { "l", "r" })
            {
                float sign = side == "l" ? -1f : 1f;
                if (!byName.TryGetValue(side + "_uparm", out var upper) ||
                    !byName.TryGetValue(side + "_lowarm", out var lower) ||
                    !byName.TryGetValue(side + "_wrist", out var wrist))
                {
                    warnings.Add($"Could not T-pose the '{side}' arm (bones not found); retarget quality may suffer.");
                    continue;
                }

                var target = new Vector3(sign, 0f, 0f);
                var upperDir = (lower.position - upper.position).normalized;
                upper.rotation = Quaternion.FromToRotation(upperDir, target) * upper.rotation;
                var lowerDir = (wrist.position - lower.position).normalized;
                lower.rotation = Quaternion.FromToRotation(lowerDir, target) * lower.rotation;
            }
        }
    }
}

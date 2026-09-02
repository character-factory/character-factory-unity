using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace CharacterFactory.Core
{
    /// <summary>
    /// Measures handedness, facing, grounding, and skeleton shape for an imported Character
    /// Factory hierarchy. This has no editor dependency and works on any instantiated hierarchy.
    /// </summary>
    public static class CharacterVerification
    {
        [Serializable]
        public class Report
        {
            public bool Passed;
            public List<Check> Checks = new List<Check>();
            public List<string> Notes = new List<string>();

            public void Add(string name, bool passed, string evidence)
            {
                Checks.Add(new Check { Name = name, Passed = passed, Evidence = evidence });
                Passed = Checks.All(c => c.Passed);
            }
        }

        [Serializable]
        public class Check
        {
            public string Name;
            public bool Passed;
            public string Evidence;
        }

        /// <summary>
        /// Run all checks against an instance whose root sits at the world origin with identity
        /// rotation (spawned prefabs qualify). Checks are tolerant of small animation offsets.
        /// </summary>
        public static Report Run(GameObject instance, ExportManifest manifest = null)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            var report = new Report();

            var byName = new Dictionary<string, Transform>();
            foreach (var t in instance.GetComponentsInChildren<Transform>(true))
                if (!byName.ContainsKey(t.name)) byName.Add(t.name, t);

            Transform Get(string n) => byName.TryGetValue(n, out var t) ? t : null;
            var lWrist = Get("l_wrist"); var rWrist = Get("r_wrist");
            var lFoot = Get("l_foot"); var rFoot = Get("r_foot");
            var head = Get("c_head"); var lEye = Get("l_eye"); var rEye = Get("r_eye");

            // 1. Skeleton presence.
            var required = new[] { "root", "l_wrist", "r_wrist", "l_foot", "r_foot", "c_head", "l_eye", "r_eye" };
            var missing = required.Where(n => Get(n) == null).ToList();
            report.Add("skeleton bones present", missing.Count == 0,
                missing.Count == 0 ? "all landmark bones found" : "missing: " + string.Join(", ", missing));
            if (missing.Count > 0) return report; // nothing below is meaningful

            var origin = instance.transform;

            // 2. Facing: eyes should sit forward (+Z in instance space) of the head.
            var eyeMidLocal = origin.InverseTransformPoint((lEye.position + rEye.position) / 2f);
            var headLocal = origin.InverseTransformPoint(head.position);
            float forwardOffset = eyeMidLocal.z - headLocal.z;
            report.Add("faces +Z", forwardOffset > 0.01f,
                $"eye midpoint sits {forwardOffset:F3} m forward of the head bone");

            // 3. Handedness: facing +Z in left-handed Unity space puts the anatomical left at -X.
            var lw = origin.InverseTransformPoint(lWrist.position);
            var rw = origin.InverseTransformPoint(rWrist.position);
            report.Add("left is left", lw.x < rw.x - 0.05f,
                $"l_wrist x={lw.x:F3}, r_wrist x={rw.x:F3} (left must be the smaller x)");

            // 4. Grounding: retain the useful foot-bone diagnostic, then use baked geometry and
            // the manifest plane as the authoritative visual check when 0.6 is available.
            var lf = origin.InverseTransformPoint(lFoot.position);
            var rf = origin.InverseTransformPoint(rFoot.position);
            float lowestFoot = Mathf.Min(lf.y, rf.y);
            report.Add("feet near ground", lowestFoot > -0.05f && lowestFoot < 0.25f,
                $"lowest foot bone at y={lowestFoot:F3} (joint diagnostic; visual grounding is measured from baked geometry)");

            if (manifest?.Grounding != null)
            {
                if (manifest.Grounding.LeftSole != null && manifest.Grounding.RightSole != null)
                {
                    float leftSoleY = lf.y - manifest.Grounding.LeftSole.OffsetToGroundM;
                    float rightSoleY = rf.y - manifest.Grounding.RightSole.OffsetToGroundM;
                    float soleTolerance = Mathf.Max(.01f, manifest.Grounding.IdleGroundToleranceM);
                    float leftError = Mathf.Abs(leftSoleY - manifest.Grounding.PlaneHeightM);
                    float rightError = Mathf.Abs(rightSoleY - manifest.Grounding.PlaneHeightM);
                    report.Add("both sole markers match manifest ground plane",
                        leftError <= soleTolerance && rightError <= soleTolerance,
                        $"estimated soles y={leftSoleY:F4}/{rightSoleY:F4}, plane={manifest.Grounding.PlaneHeightM:F4}, " +
                        $"errors={leftError * 1000f:F1}/{rightError * 1000f:F1} mm, tolerance={soleTolerance * 1000f:F1} mm");
                }

                float minGeometryY = float.PositiveInfinity;
                foreach (var renderer in instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (renderer.sharedMesh == null) continue;
                    var baked = new Mesh();
                    try
                    {
                        renderer.BakeMesh(baked);
                        foreach (var vertex in baked.vertices)
                            minGeometryY = Mathf.Min(minGeometryY,
                                origin.InverseTransformPoint(renderer.transform.TransformPoint(vertex)).y);
                    }
                    finally { UnityEngine.Object.DestroyImmediate(baked); }
                }
                float tolerance = Mathf.Max(0.01f, manifest.Grounding.IdleGroundToleranceM);
                float error = Mathf.Abs(minGeometryY - manifest.Grounding.PlaneHeightM);
                report.Add("geometry matches manifest ground plane", error <= tolerance,
                    $"lowest skinned vertex y={minGeometryY:F4}, plane={manifest.Grounding.PlaneHeightM:F4}, " +
                    $"error={error * 1000f:F1} mm, tolerance={tolerance * 1000f:F1} mm");
            }

            // 5. Plausible stature.
            float headHeight = origin.InverseTransformPoint(head.position).y;
            report.Add("plausible stature", headHeight > 1.2f && headHeight < 2.2f,
                $"head bone at y={headHeight:F2} m");

            // 6. Manifest agreement, when one is available. joint_count describes the skin's
            // joint array, which survives import as SkinnedMeshRenderer.bones.
            if (manifest != null)
            {
                report.Add("manifest schema supported", manifest.IsRecognizedFormat && manifest.IsSupportedSchema,
                    $"format='{manifest.Format}', schema='{manifest.SchemaVersion}', supported='{ExportManifest.SupportedSchemaVersion}'");
                report.Add("mandatory topology present", manifest.IsMouthInterior,
                    $"topology='{manifest.Topology}'");
                var skin = instance.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                    .OrderByDescending(s => s.bones?.Length ?? 0).FirstOrDefault();
                int joints = skin?.bones?.Length ?? -1;
                bool jointCountOk = manifest.JointCount <= 0 || joints == manifest.JointCount;
                report.Add("joint count matches manifest", jointCountOk,
                    $"largest skin has {joints} joints, manifest says {manifest.JointCount}");
                if (!manifest.DeclaresMetersYUpZForward)
                    report.Notes.Add($"Manifest declares units={manifest.Units}, up={manifest.UpAxis}, forward={manifest.ForwardAxis} — not the meters/+Y/+Z the checks assume.");
            }
            else
            {
                report.Notes.Add("No manifest supplied: geometric checks only.");
            }

            // 7. Expression morphs, when the manifest declares them (mouth-interior topology).
            if (manifest?.Morphs != null && manifest.Morphs.Count > 0)
            {
                var body = instance.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                    .OrderByDescending(s => s.sharedMesh != null ? s.sharedMesh.blendShapeCount : 0).FirstOrDefault();
                var bodyMesh = body != null ? body.sharedMesh : null;
                int shapeCount = bodyMesh != null ? bodyMesh.blendShapeCount : 0;
                report.Add("morph count matches manifest", shapeCount == manifest.Morphs.Count,
                    $"mesh has {shapeCount} blendshapes, manifest declares {manifest.Morphs.Count}");

                if (bodyMesh != null && shapeCount > 0)
                {
                    int mismatches = 0;
                    string firstMismatch = null;
                    for (int i = 0; i < Mathf.Min(shapeCount, manifest.Morphs.Names.Count); i++)
                    {
                        // glTFast may prefix names with the mesh name; compare by suffix.
                        if (!bodyMesh.GetBlendShapeName(i).EndsWith(manifest.Morphs.Names[i]))
                        {
                            mismatches++;
                            firstMismatch ??= $"index {i}: '{bodyMesh.GetBlendShapeName(i)}' vs '{manifest.Morphs.Names[i]}'";
                        }
                    }
                    report.Add("morph names match manifest", mismatches == 0,
                        mismatches == 0 ? "all names align" : $"{mismatches} mismatched ({firstMismatch})");

                    // A sample morph must actually displace vertices — catches a silent all-zero import.
                    int probe = manifest.Jaw != null && manifest.Jaw.ExpressionUnit >= 0 && manifest.Jaw.ExpressionUnit < shapeCount
                        ? manifest.Jaw.ExpressionUnit : 0;
                    float frameWeight = bodyMesh.GetBlendShapeFrameWeight(probe, bodyMesh.GetBlendShapeFrameCount(probe) - 1);
                    float saved = body.GetBlendShapeWeight(probe);
                    var baked0 = new Mesh(); var baked1 = new Mesh();
                    body.SetBlendShapeWeight(probe, 0f); body.BakeMesh(baked0);
                    body.SetBlendShapeWeight(probe, frameWeight); body.BakeMesh(baked1);
                    body.SetBlendShapeWeight(probe, saved);
                    var v0 = baked0.vertices; var v1 = baked1.vertices;
                    float maxDisp = 0f;
                    for (int i = 0; i < v0.Length; i++) maxDisp = Mathf.Max(maxDisp, (v1[i] - v0[i]).magnitude);
                    UnityEngine.Object.DestroyImmediate(baked0); UnityEngine.Object.DestroyImmediate(baked1);
                    report.Add("sample morph displaces vertices", maxDisp > 0.001f,
                        $"facs_{probe:00} at full weight moves vertices up to {maxDisp * 1000f:F1} mm");
                    report.Add("morph frame weight noted", true,
                        $"frame weight is {frameWeight:F2} — full activation at {frameWeight:F2}, NOT Unity's conventional 100");
                }

                report.Add("limitations table present", manifest.Limitations != null && manifest.Limitations.Entries.Count > 0,
                    manifest.Limitations != null
                        ? $"{manifest.Limitations.Entries.Count} measured entries (tolerance {manifest.Limitations.ToleranceMm} mm)"
                        : "manifest has no animation_limitations block");
            }

            // 8. Humanoid avatar, when an Animator is configured.
            var animator = instance.GetComponentInChildren<Animator>();
            if (animator != null && animator.avatar != null)
            {
                report.Add("humanoid avatar valid", animator.avatar.isValid && animator.avatar.isHuman,
                    $"avatar '{animator.avatar.name}' valid={animator.avatar.isValid} human={animator.avatar.isHuman}");
                var mappedLeft = animator.GetBoneTransform(HumanBodyBones.LeftHand);
                if (mappedLeft != null)
                    report.Add("humanoid LeftHand is the rig's left wrist", mappedLeft == lWrist,
                        $"LeftHand resolves to '{mappedLeft.name}'");
            }

            return report;
        }
    }
}

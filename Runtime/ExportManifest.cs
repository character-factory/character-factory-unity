using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CharacterFactory.Core
{
    /// <summary>
    /// Export-manifest 0.6 carried in a character-factory GLB's asset-level `extras` and served
    /// verbatim at GET /v0/characters/{id}/manifest.json. This package pins 0.6 deliberately:
    /// consumers must not guess when a schema changes shape or meaning.
    /// </summary>
    [Serializable]
    public class ExportManifest
    {
        public const string ExpectedFormat = "character-factory/export-manifest";
        public const string SupportedSchemaVersion = "0.6";
        public const string TopologyMouthInterior = "mouth-interior";

        public string Format;
        public string SchemaVersion;
        public string SchemaUri;
        public string SchemaVersionContract;
        public string Generator;
        public string Units;
        public string UpAxis;
        public string ForwardAxis;
        public int JointCount;
        public float RestKneeFlexionDegrees;
        public string SkeletonRoot;
        public float StatureM;

        public string Topology;

        /// <summary>Humanoid role -> rig bone name (normalized to Unity HumanTrait role names).</summary>
        public Dictionary<string, string> HumanoidMap = new Dictionary<string, string>();
        public string HumanoidMapConvention;

        public List<string> Notes = new List<string>();

        // ---- mandatory character/export blocks in schema 0.6 ----
        public ExpressionMorphs Morphs;
        public JawBlock Jaw;
        public AnimationLimitations Limitations;
        public IdleClipBlock IdleClip;
        public GroundingBlock Grounding;

        public bool HasHumanoidMap => HumanoidMap != null && HumanoidMap.Count > 0;
        public bool IsRecognizedFormat => Format == ExpectedFormat;
        public bool IsSupportedSchema => SchemaVersion == SupportedSchemaVersion;
        public bool IsMouthInterior => Topology == TopologyMouthInterior;

        public bool DeclaresMetersYUpZForward =>
            string.Equals(Units, "meters", StringComparison.OrdinalIgnoreCase)
            && UpAxis == "+Y" && ForwardAxis == "+Z";

        [Serializable]
        public class ExpressionMorphs
        {
            public List<string> Names = new List<string>();
            public int Count;
            /// <summary>e.g. "provisional-measured" — semantics are guidance, not contract.</summary>
            public string SemanticSource;
            /// <summary>semantic name (e.g. "smile_left") -> morph index.</summary>
            public Dictionary<string, int> Semantics = new Dictionary<string, int>();
        }

        [Serializable]
        public class JawBlock
        {
            public string Joint = "c_jaw";
            public string CertifiedControl;
            public float[] RotationAxisLocal;
            public string RotationSign;
            public float FullOpenDegrees;
            public float FullOpenApertureCm;
            public int ExpressionUnit = -1;
            public float ExpressionFitAngleDegrees;
            public string Note;
        }

        [Serializable]
        public class AnimationLimitations
        {
            public float ToleranceMm;
            public string MeasuredOn;
            public string Parameterization;
            public string Reading;
            public List<Entry> Entries = new List<Entry>();

            [Serializable]
            public class Entry
            {
                public string Kind;
                public string Case;
                public Parameters Params;
                public float? MaxProtrusionMm;
                public string Note;
            }

            [Serializable]
            public class Parameters
            {
                public float Facs24;
                public int? Unit;
                public float? Weight;
            }

            /// <summary>
            /// Entries involving a morph unit. Version 0.6 makes `params` authoritative; `case`
            /// is a human-readable label and is never parsed.
            /// </summary>
            public IEnumerable<(int unit, float weight, float? jaw, Entry entry)> MorphCases()
            {
                foreach (var e in Entries)
                {
                    if (e.Params?.Unit == null || e.Params.Weight == null) continue;
                    yield return (e.Params.Unit.Value, e.Params.Weight.Value, e.Params.Facs24, e);
                }
            }
        }

        [Serializable]
        public class IdleClipBlock
        {
            public string Name;
            public float Seconds;
            public bool Loops;
            public bool StartsAtRest;
            public string Content;
            public string RigType;
            public string IntendedPlayback;
            public string HumanoidRetargeting;
            public string ReferencePose;
            public bool HasCertifiedContactFrames;
        }

        [Serializable]
        public class GroundingBlock
        {
            public string CoordinateSpace;
            public string UpAxis;
            public float PlaneHeightM;
            public string RootJoint;
            public float RootOffsetToGroundM;
            public float IdleGroundToleranceM;
            public bool ContactFramesCertified;
            public bool RuntimeFootIkRecommended;
            public SoleMarker LeftSole;
            public SoleMarker RightSole;

            public float RootSceneHeightM => PlaneHeightM + RootOffsetToGroundM;

            [Serializable]
            public class SoleMarker
            {
                public string Joint;
                public float JointSceneYM;
                public float OffsetToGroundM;
            }
        }

        /// <summary>Reject an export this package cannot interpret without guessing.</summary>
        public void RequireSupportedBaseline()
        {
            if (!IsRecognizedFormat)
                throw new InvalidDataException($"Unsupported manifest format '{Format ?? "(missing)"}'.");
            if (!IsSupportedSchema)
                throw new InvalidDataException(
                    $"Unsupported export-manifest schema '{SchemaVersion ?? "(missing)"}'; this package supports {SupportedSchemaVersion}.");
            if (!DeclaresMetersYUpZForward)
                throw new InvalidDataException($"Unsupported coordinates: units={Units}, up={UpAxis}, forward={ForwardAxis}.");
            if (!IsMouthInterior)
                throw new InvalidDataException($"Unsupported topology '{Topology ?? "(missing)"}'; mouth-interior is mandatory.");
            if (!HasHumanoidMap || HumanoidMap.Count != 54)
                throw new InvalidDataException(
                    $"Manifest 0.6 must declare exactly 54 Humanoid mappings; found {HumanoidMap?.Count ?? 0}.");
            if (Morphs == null || Morphs.Count != 72 || Morphs.Names.Count != 72)
                throw new InvalidDataException(
                    $"Manifest 0.6 must declare exactly 72 facial morphs; found {Morphs?.Count ?? 0}.");
            for (int i = 0; i < 72; i++)
                if (Morphs.Names[i] != $"facs_{i:00}")
                    throw new InvalidDataException(
                        $"Manifest morph {i} is '{Morphs.Names[i]}', expected 'facs_{i:00}'.");
            if (Jaw == null || Jaw.RotationAxisLocal == null || Jaw.RotationAxisLocal.Length != 3)
                throw new InvalidDataException("Manifest 0.6 has no usable jaw contract.");
            if (Grounding == null)
                throw new InvalidDataException("Manifest 0.6 has no grounding contract.");
        }

        // ------------------------------------------------------------------ parsing

        public static ExportManifest FromJson(JObject extras)
        {
            var m = new ExportManifest
            {
                Format = S(extras["format"]),
                SchemaVersion = S(extras["schema_version"]),
                SchemaUri = S(extras["$schema"]),
                SchemaVersionContract = S(extras["schema_version_contract"]),
                Generator = S(extras["generator"]),
                Units = S(extras["units"]),
                UpAxis = S(extras["up_axis"]),
                ForwardAxis = S(extras["forward_axis"]),
                JointCount = extras.Value<int?>("joint_count") ?? 0,
                RestKneeFlexionDegrees = extras.Value<float?>("rest_knee_flexion_degrees") ?? 0f,
                SkeletonRoot = S(extras["skeleton_root"]),
                StatureM = extras.Value<float?>("stature_m") ?? 0f,
                Topology = S(extras["topology"]),
            };

            foreach (var t in extras["notes"] as JArray ?? new JArray())
                if (t.Type == JTokenType.String) m.Notes.Add((string)t);

            // humanoid_map has shipped in two shapes: a flat {role: bone} dict (possibly empty),
            // and a structured { convention, map: {role: bone}, unmapped: {...}, ... } object.
            var hm = extras["humanoid_map"] as JObject;
            if (hm != null)
            {
                var mapObj = hm["map"] as JObject;
                if (mapObj != null)
                {
                    m.HumanoidMapConvention = S(hm["convention"]);
                    foreach (var kv in mapObj)
                        if (kv.Value.Type == JTokenType.String)
                            m.HumanoidMap[NormalizeRoleName((string)kv.Key)] = (string)kv.Value;
                }
                else
                {
                    foreach (var kv in hm)
                        if (kv.Value.Type == JTokenType.String)
                            m.HumanoidMap[NormalizeRoleName((string)kv.Key)] = (string)kv.Value;
                }
            }

            var em = extras["expression_morphs"] as JObject;
            if (em != null)
            {
                m.Morphs = new ExpressionMorphs { Count = em.Value<int?>("count") ?? 0 };
                foreach (var t in em["names"] as JArray ?? new JArray())
                    if (t.Type == JTokenType.String) m.Morphs.Names.Add((string)t);
                var sem = em["semantics"] as JObject;
                if (sem != null)
                {
                    m.Morphs.SemanticSource = S(sem["semantic_source"]);
                    var entries = sem["entries"] as JObject;
                    if (entries != null)
                        foreach (var kv in entries)
                            if (int.TryParse(kv.Key, out var idx) && kv.Value is JObject e && e["name"]?.Type == JTokenType.String)
                                m.Morphs.Semantics[(string)e["name"]] = idx;
                }
            }

            var jaw = extras["jaw"] as JObject;
            if (jaw != null && jaw["rotation_axis_local"] != null)
            {
                m.Jaw = new JawBlock
                {
                    Joint = S(jaw["joint"]) ?? "c_jaw",
                    CertifiedControl = S(jaw["certified_control"]),
                    RotationSign = S(jaw["rotation_sign"]),
                    FullOpenDegrees = jaw.Value<float?>("full_open_degrees") ?? 0f,
                    FullOpenApertureCm = jaw.Value<float?>("full_open_aperture_cm") ?? 0f,
                    ExpressionUnit = jaw.Value<int?>("expression_unit") ?? -1,
                    ExpressionFitAngleDegrees = jaw.Value<float?>("expression_fit_angle_degrees") ?? 0f,
                    Note = S(jaw["note"]),
                };
                var axis = jaw["rotation_axis_local"] as JArray;
                if (axis != null && axis.Count == 3)
                    m.Jaw.RotationAxisLocal = new[] { (float)axis[0], (float)axis[1], (float)axis[2] };
            }

            var lim = extras["animation_limitations"] as JObject;
            if (lim != null)
            {
                m.Limitations = new AnimationLimitations
                {
                    ToleranceMm = lim.Value<float?>("tolerance_mm") ?? 0f,
                    MeasuredOn = S(lim["measured_on"]),
                    Parameterization = S(lim["parameterization"]),
                    Reading = S(lim["reading"]),
                };
                foreach (var t in lim["entries"] as JArray ?? new JArray())
                {
                    if (t is not JObject e) continue;
                    var entry = new AnimationLimitations.Entry
                    {
                        Kind = S(e["kind"]),
                        Case = S(e["case"]),
                        MaxProtrusionMm = e.Value<float?>("max_protrusion_mm"),
                        Note = S(e["note"]),
                    };
                    if (e["params"] is JObject p)
                    {
                        entry.Params = new AnimationLimitations.Parameters
                        {
                            Facs24 = p.Value<float?>("facs_24") ?? 0f,
                            Unit = p.Value<int?>("unit"),
                            Weight = p.Value<float?>("weight"),
                        };
                    }
                    m.Limitations.Entries.Add(entry);
                }
            }

            var idle = extras["idle_clip"] as JObject;
            if (idle != null)
            {
                m.IdleClip = new IdleClipBlock
                {
                    Name = S(idle["name"]),
                    Seconds = idle.Value<float?>("seconds") ?? 0f,
                    Loops = idle.Value<bool?>("loops") ?? false,
                    StartsAtRest = idle.Value<bool?>("starts_at_rest") ?? false,
                    Content = S(idle["content"]),
                    RigType = S(idle["rig_type"]),
                    IntendedPlayback = S(idle["intended_playback"]),
                    HumanoidRetargeting = S(idle["humanoid_retargeting"]),
                    ReferencePose = S(idle["reference_pose"]),
                    HasCertifiedContactFrames = (idle["contact_frames"] as JArray)?.Count > 0,
                };
            }

            var grounding = extras["grounding"] as JObject;
            if (grounding != null)
            {
                m.Grounding = new GroundingBlock
                {
                    CoordinateSpace = S(grounding["coordinate_space"]),
                    UpAxis = S(grounding["up_axis"]),
                    PlaneHeightM = grounding.Value<float?>("plane_height_m") ?? 0f,
                    RootJoint = S(grounding["root_joint"]),
                    RootOffsetToGroundM = grounding.Value<float?>("root_offset_to_ground_m") ?? 0f,
                    IdleGroundToleranceM = grounding.Value<float?>("idle_ground_tolerance_m") ?? 0f,
                    ContactFramesCertified = grounding.Value<bool?>("contact_frames_certified") ?? false,
                    RuntimeFootIkRecommended = grounding.Value<bool?>("runtime_foot_ik_recommended") ?? false,
                };
                var soles = grounding["sole_markers"] as JObject;
                m.Grounding.LeftSole = ParseSole(soles?["left"] as JObject);
                m.Grounding.RightSole = ParseSole(soles?["right"] as JObject);
            }

            return m;
        }

        static GroundingBlock.SoleMarker ParseSole(JObject value)
        {
            if (value == null) return null;
            return new GroundingBlock.SoleMarker
            {
                Joint = S(value["joint"]),
                JointSceneYM = value.Value<float?>("joint_scene_y_m") ?? 0f,
                OffsetToGroundM = value.Value<float?>("offset_to_ground_m") ?? 0f,
            };
        }

        static string S(JToken t) => t?.Type == JTokenType.String ? (string)t : null;

        /// <summary>
        /// The manifest declares convention "unity-humanoid" but writes finger roles CamelCase
        /// ("LeftThumbProximal") where Unity's HumanTrait uses spaces ("Left Thumb Proximal").
        /// Body roles are identical in both spellings; normalize fingers here.
        /// </summary>
        public static string NormalizeRoleName(string role)
        {
            if (string.IsNullOrEmpty(role) || role.Contains(" ")) return role;
            foreach (var finger in new[] { "Thumb", "Index", "Middle", "Ring", "Little" })
            {
                foreach (var seg in new[] { "Proximal", "Intermediate", "Distal" })
                {
                    if (role == "Left" + finger + seg) return $"Left {finger} {seg}";
                    if (role == "Right" + finger + seg) return $"Right {finger} {seg}";
                }
            }
            return role;
        }
    }

    /// <summary>Where a manifest (or its stand-in) came from, in decreasing order of authority.</summary>
    public enum ManifestSource
    {
        EmbeddedExtras,   // read from the GLB's asset.extras — authoritative
        ServerRoute       // GET /v0/characters/{id}/manifest.json — same object, cheap read
    }

    /// <summary>
    /// Reads the asset-level `extras` manifest straight out of a .glb container without any
    /// glTF library: parses the GLB header and the JSON chunk only. Engine-free and editor-free.
    /// </summary>
    public static class GlbManifestReader
    {
        const uint GlbMagic = 0x46546C67; // "glTF"
        const uint JsonChunkType = 0x4E4F534A; // "JSON"

        /// <summary>
        /// Try to read the embedded export manifest from GLB bytes.
        /// Returns false (manifest null) when the file has no extras or they are not
        /// a character-factory export manifest; throws on a malformed GLB container.
        /// </summary>
        public static bool TryRead(byte[] glb, out ExportManifest manifest, out string rawExtrasJson)
        {
            manifest = null;
            rawExtrasJson = null;

            if (glb == null || glb.Length < 20)
                throw new InvalidDataException("Not a GLB file: shorter than the 12-byte header plus first chunk header.");
            if (BitConverter.ToUInt32(glb, 0) != GlbMagic)
                throw new InvalidDataException("Not a GLB file: missing 'glTF' magic.");

            uint chunkLength = BitConverter.ToUInt32(glb, 12);
            uint chunkType = BitConverter.ToUInt32(glb, 16);
            if (chunkType != JsonChunkType)
                throw new InvalidDataException("Malformed GLB: first chunk is not the JSON chunk.");
            if (20 + chunkLength > glb.Length)
                throw new InvalidDataException("Malformed GLB: JSON chunk length exceeds file size.");

            var json = System.Text.Encoding.UTF8.GetString(glb, 20, (int)chunkLength);
            var root = JObject.Parse(json);
            var extras = root["asset"]?["extras"] as JObject;
            if (extras == null)
                return false;

            rawExtrasJson = extras.ToString(Formatting.Indented);
            manifest = ExportManifest.FromJson(extras);
            return manifest != null && manifest.IsRecognizedFormat;
        }

        public static bool TryReadFile(string glbPath, out ExportManifest manifest, out string rawExtrasJson)
        {
            return TryRead(File.ReadAllBytes(glbPath), out manifest, out rawExtrasJson);
        }
    }
}

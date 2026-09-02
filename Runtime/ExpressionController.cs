using System;
using System.Collections.Generic;
using UnityEngine;

namespace CharacterFactory.Core
{
    /// <summary>
    /// Runtime access to a mouth-interior character's FACS expression morphs and jaw, resolved
    /// through the export manifest at import time (never hardcoded — the semantic table is
    /// marked provisional by the server and may change per export).
    ///
    /// Weights are normalized 0..1 (full activation), regardless of Unity's blendshape weight
    /// convention: glTFast registers morph frames at weight 1.0, so inspector-style 0..100
    /// values would displace the face a hundredfold. This component reads the actual frame
    /// weight and scales accordingly.
    ///
    /// Jaw ("jaw_open") follows manifest 0.6's composition and handedness contract: in Unity,
    /// opening is negative rotation about the imported local axis, and the joint is driven with
    /// the fit morph
    /// (facs_24 = w, joint = -w * expression_fit_angle). The joint is applied in LateUpdate so
    /// it wins over (deliberately jaw-free) animator clips.
    /// </summary>
    public class ExpressionController : MonoBehaviour
    {
        [Serializable]
        public class SemanticEntry
        {
            public string Name;
            public int Index;
        }

        [Tooltip("The skinned mesh carrying the facs_* blendshapes.")]
        public SkinnedMeshRenderer Body;

        [Tooltip("Semantic name -> morph index, copied from the manifest (provisional).")]
        public List<SemanticEntry> Semantics = new List<SemanticEntry>();

        [Tooltip("The jaw joint (c_jaw).")]
        public Transform JawJoint;
        public Vector3 JawAxisLocal = new Vector3(0.019202f, -0.006873f, -0.999792f);
        public float JawFitAngleDegrees = 14.84f;
        public float JawFullOpenDegrees = 27.06f;
        [Tooltip("Morph index paired with the jaw joint (facs_24), -1 if none.")]
        public int JawFitMorphIndex = -1;

        [Range(0f, 1f)]
        [Tooltip("Jaw open level; drives the joint + fit morph pair.")]
        public float JawOpen;

        Quaternion _jawRest;
        bool _jawRestCaptured;
        readonly Dictionary<string, int> _byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public const string JawOpenName = "jaw_open";

        void Awake()
        {
            if (Body == null) Body = GetComponentInChildren<SkinnedMeshRenderer>();
            CaptureJawRest();
            RebuildLookup();
        }

        void OnValidate() => RebuildLookup();

        void RebuildLookup()
        {
            _byName.Clear();
            if (Semantics == null) return;
            foreach (var s in Semantics)
                if (!string.IsNullOrEmpty(s.Name)) _byName[s.Name] = s.Index;
        }

        void EnsureLookup()
        {
            // Editor scripts can instantiate and sample a prefab without invoking Awake or
            // OnValidate. The semantic table is serialized, but the runtime dictionary is not.
            if (_byName.Count == 0 && Semantics != null && Semantics.Count > 0)
                RebuildLookup();
        }

        void CaptureJawRest()
        {
            if (JawJoint != null && !_jawRestCaptured)
            {
                _jawRest = JawJoint.localRotation;
                _jawRestCaptured = true;
            }
        }

        /// <summary>
        /// Set an expression by semantic name ("smile_left"), by raw channel name ("facs_32"),
        /// or by paired shorthand ("smile" drives smile_left + smile_right). "jaw_open" and the
        /// manifest-declared jaw expression unit both route to the jaw joint + fit-morph pair.
        /// Use SetMorph when deliberately requesting morph-only control. Weight is 0..1 (clamped).
        /// </summary>
        public void SetExpression(string name, float weight)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("Expression name is empty.");
            weight = Mathf.Clamp01(weight);

            if (name.Equals(JawOpenName, StringComparison.OrdinalIgnoreCase))
            {
                JawOpen = weight;
                ApplyJaw();
                return;
            }
            EnsureLookup();
            if (_byName.TryGetValue(name, out var index))
            {
                SetExpressionUnit(index, weight);
                return;
            }
            // paired shorthand: "smile" -> smile_left + smile_right
            bool any = false;
            foreach (var side in new[] { "_left", "_right" })
            {
                if (_byName.TryGetValue(name + side, out var i)) { SetExpressionUnit(i, weight); any = true; }
            }
            if (any) return;

            if (name.StartsWith("facs_", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(name.Substring(5), out var raw))
            {
                SetExpressionUnit(raw, weight);
                return;
            }
            throw new ArgumentException(
                $"Unknown expression '{name}'. Known: jaw_open, {string.Join(", ", _byName.Keys)}, or facs_00..facs_{(Body != null && Body.sharedMesh != null ? Body.sharedMesh.blendShapeCount - 1 : 71):00}.");
        }

        void SetExpressionUnit(int index, float weight)
        {
            if (index == JawFitMorphIndex && JawFitMorphIndex >= 0)
            {
                JawOpen = weight;
                ApplyJaw();
                return;
            }
            SetMorph(index, weight);
        }

        /// <summary>
        /// Set only a morph by index, weight 0..1 of full activation. This intentionally bypasses
        /// manifest expression composition; normal name-based playback should use SetExpression.
        /// </summary>
        public void SetMorph(int index, float weight)
        {
            if (Body == null || Body.sharedMesh == null)
                throw new InvalidOperationException("ExpressionController has no body SkinnedMeshRenderer.");
            if (index < 0 || index >= Body.sharedMesh.blendShapeCount)
                throw new ArgumentOutOfRangeException(nameof(index),
                    $"Morph index {index} out of range (mesh has {Body.sharedMesh.blendShapeCount}).");
            float frameWeight = Body.sharedMesh.GetBlendShapeFrameWeight(index, Body.sharedMesh.GetBlendShapeFrameCount(index) - 1);
            Body.SetBlendShapeWeight(index, Mathf.Clamp01(weight) * frameWeight);
        }

        public float GetMorph(int index)
        {
            float frameWeight = Body.sharedMesh.GetBlendShapeFrameWeight(index, Body.sharedMesh.GetBlendShapeFrameCount(index) - 1);
            return frameWeight <= 0f ? 0f : Body.GetBlendShapeWeight(index) / frameWeight;
        }

        /// <summary>Zero every morph and close the jaw.</summary>
        public void ResetExpressions()
        {
            if (Body != null && Body.sharedMesh != null)
                for (int i = 0; i < Body.sharedMesh.blendShapeCount; i++)
                    Body.SetBlendShapeWeight(i, 0f);
            JawOpen = 0f;
            ApplyJaw();
        }

        void LateUpdate() => ApplyJaw();

        void ApplyJaw()
        {
            if (JawJoint == null) return;
            CaptureJawRest();
            // Manifest 0.6 documents that handedness conversion reverses the observed sign.
            JawJoint.localRotation = _jawRest * Quaternion.AngleAxis(-JawOpen * JawFitAngleDegrees, JawAxisLocal);
            if (JawFitMorphIndex >= 0 && Body != null)
                SetMorph(JawFitMorphIndex, JawOpen);
        }
    }
}

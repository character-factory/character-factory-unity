using System.Collections.Generic;
using UnityEngine;

namespace CharacterFactory.Core
{
    /// <summary>
    /// Optional facial demo-clip builders. Body-pose generation intentionally does not live here:
    /// partial Humanoid clips inherit Unity's muscle-zero pose for every omitted property, which
    /// is not the exported character rest pose and can collapse the legs.
    /// </summary>
    public static class NeutralPose
    {
        /// <summary>
        /// Demo blink cycle for a mouth-interior character: two blinks over 4 s on the manifest's
        /// blink units. Curves target `blendShape.facs_NN` on the body renderer's path with
        /// weights 0..1 (glTFast registers morph frames at weight 1.0 — see ExpressionController).
        /// Honors the manifest's animation_limitations: blink units carry no documented clipping.
        /// </summary>
        public static AnimationClip BuildBlinkClip(string bodyPath, int blinkLeft, int blinkRight, float duration = 4f)
        {
            var clip = new AnimationClip { name = "CF_Blink", frameRate = 60f };
            foreach (var idx in new[] { blinkLeft, blinkRight })
            {
                if (idx < 0) continue;
                var keys = new List<Keyframe>();
                keys.Add(new Keyframe(0f, 0f));
                foreach (var start in new[] { 1.1f, 3.2f })
                {
                    keys.Add(new Keyframe(start, 0f));
                    keys.Add(new Keyframe(start + 0.07f, 1f));
                    keys.Add(new Keyframe(start + 0.10f, 1f));
                    keys.Add(new Keyframe(start + 0.22f, 0f));
                }
                keys.Add(new Keyframe(duration, 0f));
                clip.SetCurve(bodyPath, typeof(SkinnedMeshRenderer), $"blendShape.facs_{idx:00}", new AnimationCurve(keys.ToArray()));
            }
            return clip;
        }

        /// <summary>
        /// Idle micro-expression: slow brow and faint smile drift. Uses only units with no entry
        /// in the manifest's animation_limitations table, at low weights.
        /// </summary>
        public static AnimationClip BuildMicroExpressionClip(
            string bodyPath, int browLeft, int browRight, int smileLeft, int smileRight, float duration = 6f)
        {
            var clip = new AnimationClip { name = "CF_MicroExpression", frameRate = 30f };
            void Drift(int idx, float peak, float phase)
            {
                if (idx < 0) return;
                var keys = new List<Keyframe>();
                const int samples = 12;
                for (int i = 0; i <= samples; i++)
                {
                    float t = duration * i / samples;
                    float v = peak * 0.5f * (1f - Mathf.Cos(2f * Mathf.PI * (t / duration + phase)));
                    keys.Add(new Keyframe(t, v));
                }
                clip.SetCurve(bodyPath, typeof(SkinnedMeshRenderer), $"blendShape.facs_{idx:00}", new AnimationCurve(keys.ToArray()));
            }
            Drift(browLeft, 0.3f, 0f);
            Drift(browRight, 0.3f, 0.02f);
            Drift(smileLeft, 0.35f, 0.45f);
            Drift(smileRight, 0.35f, 0.47f);
            return clip;
        }

    }
}

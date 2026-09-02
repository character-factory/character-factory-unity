using UnityEngine;

namespace CharacterFactory.Core
{
    /// <summary>
    /// Optional preview plumbing for the package's reference Animator Controller. It is not a
    /// navigation or gameplay controller: callers remain responsible for moving the character.
    /// </summary>
    [RequireComponent(typeof(Animator))]
    public sealed class ReferenceMotionDriver : MonoBehaviour
    {
        public const string MoveSpeedParameter = "MoveSpeed";
        public const string TalkingParameter = "Talking";
        public const string InteractParameter = "Interact";

        [Range(0f, 3f)]
        [Tooltip("Reference locomotion preview: 0 idle, 1 walk, 2 jog, 3 run.")]
        public float MoveSpeed;

        [Tooltip("Use the reference talking-idle motion.")]
        public bool Talking;

        [Tooltip("Play the one-shot reference interaction when play mode starts.")]
        public bool InteractOnStart;

        void Start()
        {
            Apply();
            if (InteractOnStart)
                GetComponent<Animator>().SetTrigger(InteractParameter);
        }

        public void Apply()
        {
            var animator = GetComponent<Animator>();
            if (animator == null || animator.runtimeAnimatorController == null) return;
            animator.SetFloat(MoveSpeedParameter, MoveSpeed);
            animator.SetBool(TalkingParameter, Talking);
        }

        public void PlayInteract()
        {
            var animator = GetComponent<Animator>();
            if (animator != null && animator.runtimeAnimatorController != null)
                animator.SetTrigger(InteractParameter);
        }
    }
}

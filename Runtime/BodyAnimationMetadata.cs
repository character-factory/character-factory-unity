using System;
using UnityEngine;

namespace CharacterFactory.Core
{
    /// <summary>
    /// Provenance and auxiliary channels retained beside an imported bodyanim Humanoid clip.
    /// The AnimationClip remains character-independent; this metadata preserves facts that Unity's
    /// muscle curves cannot represent, including the source skeleton contract and foot contacts.
    /// </summary>
    public sealed class BodyAnimationMetadata : ScriptableObject
    {
        [Serializable]
        public sealed class ContactSample
        {
            public float Time;
            public float[] Values;
        }

        public string SourceArtifact;
        public string Schema;
        public int SchemaVersion;
        public string SkeletonId;
        public string SkeletonVersion;
        public string SkeletonDefinitionId;
        public int SkeletonDefinitionVersion;
        public string SkeletonDefinitionSha256;
        public string JointOrderSha256;
        public string Provider;
        public string Model;
        [TextArea] public string Prompt;
        public float FramesPerSecond;
        public float DurationSeconds;
        public int FrameCount;
        public string RootMotionPolicy;
        public float SourceHorizontalDisplacementMeters;
        public float RecommendedBlendInSeconds;
        public float RecommendedBlendOutSeconds;
        public bool ImportedAsLoop;
        public float BoundaryMeanDegrees;
        public float BoundaryMaximumDegrees;
        public string BoundaryMaximumJoint;
        public bool LoopConditioned;
        public string LoopConditioningProcessor;
        public float BoundaryVelocityMeanDegreesPerFrame;
        public float BoundaryVelocityMaximumDegreesPerFrame;
        public string[] FootContactChannels;
        public ContactSample[] FootContacts;
    }
}

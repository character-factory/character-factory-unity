using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace CharacterFactory.Editor
{
    /// <summary>Right-click import door for bodyanim JSON artifacts already under Assets/.</summary>
    public static class BodyAnimationContextMenu
    {
        const string ImportPath = "Assets/Character Factory/Import Body Animation";
        const string ImportLoopPath = "Assets/Character Factory/Import Body Animation as Loop";

        [MenuItem(ImportPath, validate = true)]
        [MenuItem(ImportLoopPath, validate = true)]
        static bool Validate() => SelectedBodyAnimations().Any();

        [MenuItem(ImportPath)]
        static void Import() => ImportSelected(false);

        [MenuItem(ImportLoopPath)]
        static void ImportAsLoop() => ImportSelected(true);

        static void ImportSelected(bool loop)
        {
            foreach (string source in SelectedBodyAnimations())
            {
                string stem = Path.GetFileName(source);
                stem = stem.Substring(0, stem.Length - ".bodyanim.json".Length);
                string output = Path.GetDirectoryName(source)?.Replace('\\', '/') + "/" + stem + ".anim";
                var result = BodyAnimationImporter.Import(source, output,
                    new BodyAnimationImporter.Options { ClipName = stem, Loop = loop });
                string summary = $"[character-factory] Imported {result.ClipPath}: " +
                    $"{result.FrameCount} frames, {result.DurationSeconds:F2}s, " +
                    $"{result.RootMotionPolicy}, SOMA {result.SkeletonDefinitionSha256.Substring(0, 12)}…";
                if (result.Warnings.Count > 0)
                    Debug.LogWarning(summary + "\n" + string.Join("\n", result.Warnings));
                else
                    Debug.Log(summary);
                EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<AnimationClip>(result.ClipPath));
            }
        }

        static string[] SelectedBodyAnimations() =>
            Selection.assetGUIDs.Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => path.EndsWith(".bodyanim.json", StringComparison.OrdinalIgnoreCase))
                .ToArray();
    }
}

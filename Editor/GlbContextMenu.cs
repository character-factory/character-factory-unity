using System.Linq;
using UnityEditor;
using UnityEngine;

namespace CharacterFactory.Editor
{
    /// <summary>Right-click door for GLBs already in the project (drag-in workflow).</summary>
    public static class GlbContextMenu
    {
        const string MenuPath = "Assets/Character Factory/Build Avatar and Prefab";
        const string ReferenceMenuPath = "Assets/Character Factory/Build with Reference Controller";

        [MenuItem(MenuPath, validate = true)]
        static bool Validate() => SelectedGlbPaths().Any();

        [MenuItem(MenuPath)]
        static void Build() => BuildSelected(false);

        [MenuItem(ReferenceMenuPath, validate = true)]
        static bool ValidateReference() => SelectedGlbPaths().Any();

        [MenuItem(ReferenceMenuPath)]
        static void BuildReference() => BuildSelected(true);

        static void BuildSelected(bool includeReferenceController)
        {
            foreach (var path in SelectedGlbPaths())
            {
                var result = CharacterImportPipeline.BuildFromGlb(
                    path, includeReferenceController: includeReferenceController);
                var summary = $"[character-factory] Built {result.PrefabPath} (manifest: {result.ManifestSource})";
                if (result.ReferenceControllerIncluded)
                    summary += $" plus {result.ReferencePrefabPath}";
                if (result.Warnings.Count > 0)
                    Debug.LogWarning(summary + "\n" + string.Join("\n", result.Warnings));
                else
                    Debug.Log(summary);
                EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<GameObject>(result.PrefabPath));
            }
        }

        static string[] SelectedGlbPaths() =>
            Selection.assetGUIDs
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => p.EndsWith(".glb", System.StringComparison.OrdinalIgnoreCase))
                .ToArray();
    }
}

using System;
using System.Threading.Tasks;
using CharacterFactory.Core;
using UnityEditor;
using UnityEngine;

namespace CharacterFactory.Editor
{
    /// <summary>
    /// Prompt-based character generation and import for interactive Editor use.
    /// </summary>
    public class CharacterFactoryWindow : EditorWindow
    {
        string _prompt = "";
        bool _referenceController;
        bool _walking;
        bool _busy;
        string _status = "";
        string _lastError = "";

        [MenuItem("Window/Character Factory")]
        public static void Open()
        {
            var window = GetWindow<CharacterFactoryWindow>("Character Factory");
            window.minSize = new Vector2(380, 220);
        }

        void OnGUI()
        {
            EditorGUILayout.Space(6);

            using (new EditorGUI.DisabledScope(_busy))
            {
                EditorGUI.BeginChangeCheck();
                var url = EditorGUILayout.TextField("Server", CharacterFactorySettings.StoredServerUrl);
                if (EditorGUI.EndChangeCheck())
                    CharacterFactorySettings.StoredServerUrl = url;

                EditorGUILayout.LabelField("Prompt");
                _prompt = EditorGUILayout.TextArea(_prompt, GUILayout.MinHeight(50));
                _referenceController = EditorGUILayout.ToggleLeft(
                    "Add generated full-body reference controller", _referenceController);
                using (new EditorGUI.DisabledScope(!_referenceController))
                    _walking = EditorGUILayout.ToggleLeft("Start at reference walk speed", _walking);

                EditorGUILayout.Space(4);
                using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(_prompt)))
                {
                    if (GUILayout.Button("Create and import into scene", GUILayout.Height(28)))
                        _ = CreateAsync();
                }
            }

            EditorGUILayout.Space(6);
            if (_busy)
                EditorGUILayout.HelpBox(_status, MessageType.Info);
            else if (!string.IsNullOrEmpty(_lastError))
                EditorGUILayout.HelpBox(_lastError, MessageType.Error);
            else if (!string.IsNullOrEmpty(_status))
                EditorGUILayout.HelpBox(_status, MessageType.None);
        }

        async Task CreateAsync()
        {
            _busy = true;
            _lastError = "";
            var startedAt = DateTime.UtcNow;
            void Progress(string message)
            {
                _status = $"{message}  ({(DateTime.UtcNow - startedAt).TotalSeconds:F0}s elapsed)";
                Repaint();
            }

            try
            {
                var client = new CharacterFactoryClient(CharacterFactorySettings.ResolveServer());
                Progress("Submitting prompt…");
                var request = new CreateCharacterRequest { Prompt = _prompt };
                var job = await client.CreateCharacterAsync(request);

                Progress($"Job {job.Id} accepted");
                job = await client.WaitForJobAsync(job.Id, TimeSpan.FromSeconds(900),
                    j => Progress($"Server: {j.Status} / {j.Stage} ({j.Progress:P0})"));

                Progress("Downloading and importing…");
                var import = await CharacterImportPipeline.ImportAsync(
                    client, job.Result.CharacterId, _referenceController);

                Progress("Spawning into the open scene…");
                var instance = CharacterImportPipeline.Spawn(
                    import, Vector3.zero, _referenceController, _walking ? 1f : 0f);
                Selection.activeGameObject = instance;

                _status = $"Done: '{instance.name}' is in the scene. Prefab: {import.PrefabPath}"
                    + (import.Warnings.Count > 0 ? $"\nWarnings: {string.Join(" | ", import.Warnings)}" : "");
            }
            catch (Exception e)
            {
                _lastError = e.Message;
                _status = "";
            }
            finally
            {
                _busy = false;
                Repaint();
            }
        }
    }
}

using System.IO;
using CharacterFactory.Core;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace CharacterFactory.Editor
{
    /// <summary>
    /// Project-scoped configuration, persisted to ProjectSettings/CharacterFactorySettings.json.
    /// The server address lives here (or in the CHARACTER_FACTORY_URL environment variable, or a
    /// per-call --server argument) — it is deployment configuration, never a constant in code.
    /// </summary>
    public static class CharacterFactorySettings
    {
        const string FilePath = "ProjectSettings/CharacterFactorySettings.json";

        class Data
        {
            [JsonProperty("serverUrl")] public string ServerUrl = "";
        }

        static Data Load()
        {
            if (!File.Exists(FilePath)) return new Data();
            try { return JsonConvert.DeserializeObject<Data>(File.ReadAllText(FilePath)) ?? new Data(); }
            catch { return new Data(); }
        }

        static void Save(Data data) =>
            File.WriteAllText(FilePath, JsonConvert.SerializeObject(data, Formatting.Indented));

        /// <summary>The stored server address ("" when unset).</summary>
        public static string StoredServerUrl
        {
            get => Load().ServerUrl;
            set { var d = Load(); d.ServerUrl = value ?? ""; Save(d); }
        }

        /// <summary>Resolve the effective server address (explicit > env var > stored > default).</summary>
        public static string ResolveServer(string explicitValue = null) =>
            ServerAddress.Resolve(explicitValue, StoredServerUrl);

        [SettingsProvider]
        public static SettingsProvider CreateSettingsProvider()
        {
            return new SettingsProvider("Project/Character Factory", SettingsScope.Project)
            {
                keywords = new[] { "character", "factory", "server", "glb" },
                guiHandler = _ =>
                {
                    EditorGUILayout.Space(8);
                    EditorGUI.BeginChangeCheck();
                    var url = EditorGUILayout.TextField("Server address", StoredServerUrl);
                    if (EditorGUI.EndChangeCheck())
                        StoredServerUrl = url;
                    EditorGUILayout.HelpBox(
                        $"Effective address: {ResolveServer()}\n" +
                        $"Resolution order: --server argument > {ServerAddress.EnvVar} env var > this field > {ServerAddress.DefaultAddress}.",
                        MessageType.Info);
                },
            };
        }
    }
}

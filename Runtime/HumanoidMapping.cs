using System;
using System.Collections.Generic;

namespace CharacterFactory.Core
{
    /// <summary>
    /// Resolves Unity Humanoid roles exclusively from export-manifest 0.6. Rig naming and
    /// topology are server format guarantees; the package does not carry a second empirical
    /// mapping that could silently drift from the exporter.
    /// </summary>
    public static class HumanoidMapping
    {
        public const string JawRole = "Jaw";

        public class Resolved
        {
            public IReadOnlyDictionary<string, string> Map;
            public bool FromManifest;
            public bool JawMapped;
            public List<string> Warnings = new List<string>();
        }

        /// <summary>
        /// Resolve the manifest mapping. Jaw remains opt-in because direct c_jaw control is the
        /// certified default and Humanoid jaw muscles may fight facial playback.
        /// </summary>
        public static Resolved Resolve(ExportManifest manifest, bool mapJaw = false)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            manifest.RequireSupportedBaseline();

            var map = new Dictionary<string, string>(manifest.HumanoidMap);
            if (mapJaw)
                map[JawRole] = manifest.Jaw?.Joint ?? "c_jaw";
            else
                map.Remove(JawRole);

            return new Resolved
            {
                Map = map,
                FromManifest = true,
                JawMapped = mapJaw,
            };
        }
    }
}

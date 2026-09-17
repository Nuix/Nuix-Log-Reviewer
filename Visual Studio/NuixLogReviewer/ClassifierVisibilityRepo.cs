using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace NuixLogReviewer
{
    /// <summary>
    /// Reads and writes the per-classifier "shown/hidden" default preferences from
    /// <c>ClassifierVisibility.config</c> next to the executable.
    ///
    /// These are DEFAULTS only: they seed each classifier's eye toggle when logs load. The user can
    /// override any classifier for the current session in the Classifiers tab, and edit the defaults
    /// via the Settings &gt; Classifier Defaults dialog (which calls <see cref="Save"/>).
    ///
    /// File format: one "<c>flag = shown|hidden</c>" per line; '#' comments and blank lines ignored;
    /// unknown flags / unrecognized values default to SHOWN. Parsing is tolerant and never throws - a
    /// missing or malformed file simply yields "everything shown".
    /// </summary>
    public static class ClassifierVisibilityRepo
    {
        private static readonly string ConfigPath;

        static ClassifierVisibilityRepo()
        {
            ConfigPath = ConfigPaths.ClassifierVisibilityConfig;
        }

        /// <summary>
        /// Loads the visibility defaults as a map of flag name (lower-cased) to "hidden by default".
        /// Only flags explicitly set to "hidden" appear as true; everything else is shown (absent or false).
        /// Never throws - returns an empty map (all shown) on any IO/parse failure.
        /// </summary>
        public static Dictionary<string, bool> Load()
        {
            var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (ConfigPath == null || !File.Exists(ConfigPath)) return result;

                foreach (var raw in File.ReadAllLines(ConfigPath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;

                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    string flag = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string value = line.Substring(eq + 1).Trim().ToLowerInvariant();
                    if (flag.Length == 0) continue;

                    // Only "hidden" flips a flag off; anything else (incl. "shown") means shown.
                    result[flag] = (value == "hidden" || value == "hide" || value == "false");
                }
            }
            catch
            {
                // Tolerant: any failure => treat as no preferences (all shown).
            }
            return result;
        }

        /// <summary>True if the given flag is configured to be hidden by default.</summary>
        public static bool IsHiddenByDefault(string flag)
        {
            if (string.IsNullOrWhiteSpace(flag)) return false;
            var map = Load();
            return map.TryGetValue(flag.Trim().ToLowerInvariant(), out bool hidden) && hidden;
        }

        /// <summary>
        /// Persists the given flag -&gt; "hidden by default" preferences, writing a "hidden" line for
        /// each flag whose value is true (shown flags are simply omitted, since shown is the default).
        /// Preserves a short header comment. Best-effort; surfaces IO errors to the caller so the UI
        /// can report a failed save.
        /// </summary>
        public static void Save(IDictionary<string, bool> hiddenByFlag)
        {
            if (ConfigPath == null)
                throw new InvalidOperationException("Cannot determine config path for ClassifierVisibility.config");

            var lines = new List<string>
            {
                "# Nuix Log Reviewer - classifier visibility defaults",
                "# <flag> = shown | hidden   (unlisted flags default to shown)",
                "# Edited via Settings > Classifier Defaults.",
                "",
            };

            foreach (var kv in hiddenByFlag.Where(kv => kv.Value)
                                           .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                lines.Add($"{kv.Key} = hidden");
            }

            File.WriteAllLines(ConfigPath, lines);
        }
    }
}

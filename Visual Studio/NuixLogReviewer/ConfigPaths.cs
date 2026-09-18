using System;
using System.IO;
using System.Reflection;

namespace NuixLogReviewer
{
    /// <summary>
    /// Single source of truth for where the user-editable configuration lives: a dedicated
    /// <c>Configuration</c> folder next to the executable, rather than the exe directory itself (which
    /// is cluttered with the exe, DLLs, and license files). All config resolution points route through
    /// here so they can never drift apart, and the "Open Configuration Folder" menu opens exactly this
    /// folder.
    ///
    /// Layout (all under <see cref="Root"/>):
    /// <list type="bullet">
    /// <item><c>ClassifierScripts\*.js</c> — sandboxed scripted classifiers</item>
    /// <item><c>ClassifierVisibility.config</c> — per-flag default show/hide</item>
    /// <item><c>LogPatterns.config</c> — pattern-masking rules</item>
    /// <item><c>SavedSearches\*.txt</c> — saved searches</item>
    /// </list>
    /// The build copies the shipped defaults into this folder in the output directory.
    /// </summary>
    public static class ConfigPaths
    {
        /// <summary>Folder name (under the app directory) that holds all editable configuration.</summary>
        public const string ConfigFolderName = "Configuration";

        /// <summary>The application directory (where the exe/DLLs live), or the process base directory.</summary>
        public static string AppDirectory =>
            Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location)
            ?? Path.GetDirectoryName(Assembly.GetExecutingAssembly()?.Location)
            ?? AppContext.BaseDirectory;

        /// <summary>
        /// The configuration root: &lt;app dir&gt;/Configuration. Created on access if missing so writers
        /// (saved searches, visibility defaults) always have a target, and the menu never opens a
        /// non-existent folder. Best-effort: if creation fails, the path is still returned.
        /// </summary>
        public static string Root
        {
            get
            {
                string root = Path.Combine(AppDirectory, ConfigFolderName);
                try
                {
                    if (!Directory.Exists(root)) { Directory.CreateDirectory(root); }
                }
                catch
                {
                    // Non-fatal: callers tolerate a missing/unwritable config dir (defaults are used).
                }
                return root;
            }
        }

        /// <summary>Full path to ClassifierVisibility.config under the config root.</summary>
        public static string ClassifierVisibilityConfig => Path.Combine(Root, "ClassifierVisibility.config");

        /// <summary>Full path to LogPatterns.config under the config root.</summary>
        public static string LogPatternsConfig => Path.Combine(Root, "LogPatterns.config");

        /// <summary>Directory holding saved searches (*.txt).</summary>
        public static string SavedSearchesDir => Path.Combine(Root, "SavedSearches");

        /// <summary>Directory holding scripted classifiers (*.js).</summary>
        public static string ClassifierScriptsDir => Path.Combine(Root, "ClassifierScripts");

        /// <summary>Directory holding scripted insight detectors (*.js).</summary>
        public static string InsightScriptsDir => Path.Combine(Root, "InsightScripts");
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace NuixLogReviewer.LogRepository.Classifiers.Scripting
{
    /// <summary>
    /// Discovers and prepares scripted classifiers from the <c>ClassifierScripts</c> directory inside
    /// the <see cref="ConfigPaths.Root"/> configuration folder, once at startup (restart to pick up
    /// changes, mirroring LogPatterns.config).
    ///
    /// Each prepared <see cref="ScriptClassifierDefinition"/> is parsed/validated once and shared
    /// read-only; the load pipeline then builds a per-worker <see cref="ScriptedClassifier"/> from each.
    /// Failures are logged (via <see cref="Diagnostics.ScriptDiagnostics"/>) and skipped so a single bad
    /// script never blocks the others or the load.
    /// </summary>
    public static class ScriptClassifierLoader
    {
        private static readonly object _lock = new object();
        private static IReadOnlyList<ScriptClassifierDefinition> _cached;

        /// <summary>
        /// Returns the prepared definitions, loading them on first use. Cached for the process lifetime
        /// (startup-only load). Safe to call from multiple threads.
        /// </summary>
        public static IReadOnlyList<ScriptClassifierDefinition> GetDefinitions()
        {
            var cached = _cached;
            if (cached != null) { return cached; }

            lock (_lock)
            {
                if (_cached != null) { return _cached; }
                _cached = LoadAll(ResolveScriptDirectory());
                return _cached;
            }
        }

        /// <summary>The directory scanned for scripts: &lt;config root&gt;/ClassifierScripts.</summary>
        public static string ResolveScriptDirectory()
        {
            return ConfigPaths.ClassifierScriptsDir;
        }

        /// <summary>
        /// Loads and prepares every <c>*.js</c> in the given directory (non-recursive), in a stable
        /// alphabetical order. Missing directory => no scripts (feature simply inactive).
        /// </summary>
        public static IReadOnlyList<ScriptClassifierDefinition> LoadAll(string directory)
        {
            var result = new List<ScriptClassifierDefinition>();
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return result;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(directory, "*.js", SearchOption.TopDirectoryOnly);
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Diagnostics.ScriptDiagnostics.ReportOnce(directory, "Failed to enumerate scripts: " + ex.Message);
                return result;
            }

            foreach (var file in files)
            {
                try
                {
                    string source = File.ReadAllText(file);
                    result.Add(ScriptClassifierDefinition.Load(file, source));
                }
                catch (ScriptClassifierException ex)
                {
                    Diagnostics.ScriptDiagnostics.ReportOnce(ex.SourcePath ?? file, ex.Message);
                }
                catch (Exception ex)
                {
                    Diagnostics.ScriptDiagnostics.ReportOnce(file, "Could not load script: " + ex.Message);
                }
            }

            return result;
        }

        /// <summary>
        /// Builds a fresh set of per-worker <see cref="ScriptedClassifier"/> instances from the prepared
        /// definitions (each with its own engine). Called once per load worker. A definition that fails
        /// to build an engine is skipped (logged) rather than failing the whole set.
        /// </summary>
        public static IEnumerable<IEntryClassifier> CreateClassifiersForWorker()
        {
            foreach (var def in GetDefinitions())
            {
                ScriptedClassifier classifier = null;
                try
                {
                    classifier = new ScriptedClassifier(def);
                }
                catch (Exception ex)
                {
                    Diagnostics.ScriptDiagnostics.ReportOnce(def.SourcePath,
                        "Could not initialize engine: " + ex.Message);
                }
                if (classifier != null) { yield return classifier; }
            }
        }
    }
}

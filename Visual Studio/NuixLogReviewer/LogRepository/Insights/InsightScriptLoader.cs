using System;
using System.Collections.Generic;
using System.IO;
using Jint;
using NuixLogReviewer.LogRepository.Classifiers.Scripting.Diagnostics; // ScriptDiagnostics

namespace NuixLogReviewer.LogRepository.Insights
{
    /// <summary>
    /// Discovers user-supplied insight detectors from <c>InsightScripts\*.js</c> under the configuration
    /// folder and builds a <see cref="ScriptedInsightDetector"/> for each. Loaded on demand (each Analyze
    /// re-reads them, so editing a script + clicking Analyze picks up changes without a restart - unlike
    /// classifiers, which are load-time). A single bad script is logged (via
    /// <see cref="ScriptDiagnostics"/>) and skipped so it never blocks the others or the built-ins.
    ///
    /// A script must evaluate to an object with an <c>analyze(ctx)</c> function; it may also expose a
    /// <c>name</c> string used as the finding group label (else the file name is used).
    /// </summary>
    public static class InsightScriptLoader
    {
        /// <summary>Builds detectors for every *.js in the InsightScripts dir (alphabetical). Never throws.</summary>
        public static IReadOnlyList<IInsightDetector> LoadDetectors()
        {
            return LoadDetectors(ConfigPaths.InsightScriptsDir);
        }

        /// <summary>Builds detectors from a specific directory (used by tests/harnesses).</summary>
        public static IReadOnlyList<IInsightDetector> LoadDetectors(string directory)
        {
            var result = new List<IInsightDetector>();
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return result; // feature simply inactive when no scripts folder exists
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(directory, "*.js", SearchOption.TopDirectoryOnly);
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                ScriptDiagnostics.ReportOnce(directory, "Failed to enumerate insight scripts: " + ex.Message);
                return result;
            }

            foreach (var file in files)
            {
                try
                {
                    string source = File.ReadAllText(file);
                    string name = ProbeName(file, source); // validates analyze() exists too
                    if (name == null) { continue; }        // invalid script already reported
                    result.Add(new ScriptedInsightDetector(file, source, name));
                }
                catch (Exception ex)
                {
                    ScriptDiagnostics.ReportOnce(file, "Could not load insight script: " + ex.Message);
                }
            }

            return result;
        }

        /// <summary>
        /// Parses the script once in a throwaway sandboxed engine to (a) confirm it evaluates to an object
        /// with an <c>analyze</c> function and (b) read an optional <c>name</c>. Returns the name (or file
        /// name) on success, or null if the script is invalid (reported and skipped).
        /// </summary>
        private static string ProbeName(string file, string source)
        {
            try
            {
                var engine = ScriptedInsightDetector.CreateSandboxedEngine();
                var module = engine.Evaluate(source).AsObject();

                var analyze = module.Get("analyze");
                if (analyze == null || !(analyze is Jint.Native.Function.Function))
                {
                    ScriptDiagnostics.ReportOnce(file, "insight script has no analyze(ctx) function; skipped.");
                    return null;
                }

                var nameVal = module.Get("name");
                string name = (nameVal != null && nameVal.IsString()) ? nameVal.AsString() : null;
                return string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(file) : name;
            }
            catch (Exception ex)
            {
                ScriptDiagnostics.ReportOnce(file, "insight script failed to parse: " + ex.Message);
                return null;
            }
        }
    }
}

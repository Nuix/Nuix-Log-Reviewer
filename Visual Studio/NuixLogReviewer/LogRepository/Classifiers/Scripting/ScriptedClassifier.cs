using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Jint;
using Jint.Native;
using Jint.Runtime;

namespace NuixLogReviewer.LogRepository.Classifiers.Scripting
{
    /// <summary>
    /// Runs one <see cref="ScriptClassifierDefinition"/> against log entries via a private, sandboxed
    /// Jint engine. One instance per parallel load worker (Jint engines are single-threaded); the load
    /// pipeline already builds a fresh set of classifiers per worker.
    ///
    /// Sandbox posture: the engine has no access to the BCL, filesystem, network, or reflection, and is
    /// bounded by statement / recursion / time limits. The only thing a script sees is a plain read-only
    /// object of primitives describing the current entry. Everything is fail-soft: a script that throws,
    /// times out, or returns junk yields no flags and never aborts a load.
    /// </summary>
    public sealed class ScriptedClassifier : IEntryClassifier
    {
        private readonly ScriptClassifierDefinition _definition;
        private readonly Engine _engine;
        private readonly JsValue _classify;
        private bool _disabled;

        // Per-call execution limits. These bound a single classify() call, which is all we need since
        // classify is invoked once per entry.
        private const int MaxStatementsPerCall = 10000;
        private const int MaxRecursionDepth = 64;
        private static readonly TimeSpan CallTimeout = TimeSpan.FromMilliseconds(250);

        public ScriptedClassifier(ScriptClassifierDefinition definition)
        {
            _definition = definition ?? throw new ArgumentNullException(nameof(definition));
            _engine = CreateSandboxedEngine();

            // Evaluate the module once for this engine to obtain its classify function.
            var module = _engine.Evaluate(_definition.Source);
            _classify = module.AsObject().Get("classify");
        }

        /// <summary>
        /// Builds an engine with the sandbox constraints applied. Used both for the per-worker runtime
        /// engine and for the load-time metadata probe. BCL/CLR access is left disabled (Jint's default),
        /// so scripts cannot reach <c>System.*</c>, files, or the network.
        /// </summary>
        internal static Engine CreateSandboxedEngine()
        {
            return new Engine(options =>
            {
                options.Strict();
                options.MaxStatements(MaxStatementsPerCall);
                options.LimitRecursion(MaxRecursionDepth);
                options.TimeoutInterval(CallTimeout);
                // No options.AllowClr(...) call: CLR/BCL interop stays off.
            });
        }

        public IEnumerable<string> Classify(NuixLogEntry entry)
        {
            if (_disabled || entry == null) { return null; }

            // Cheap host-side gate: only enter the engine for candidate lines.
            if (!PassesPrefilter(entry)) { return null; }

            try
            {
                JsValue entryView = BuildEntryView(entry);
                JsValue result = _engine.Invoke(_classify, entryView);
                return NormalizeFlags(result);
            }
            catch (JavaScriptException jsEx)
            {
                Diagnostics.ScriptDiagnostics.ReportOnce(_definition.SourcePath,
                    "classify() threw: " + jsEx.Message);
                return null;
            }
            catch (TimeoutException)
            {
                Diagnostics.ScriptDiagnostics.ReportOnce(_definition.SourcePath,
                    "classify() exceeded the time limit and was disabled for this session.");
                _disabled = true;
                return null;
            }
            catch (Exception ex)
            {
                // Statement/recursion limits surface as engine exceptions; disable to avoid repeated cost.
                Diagnostics.ScriptDiagnostics.ReportOnce(_definition.SourcePath,
                    "classify() failed and was disabled for this session: " + ex.Message);
                _disabled = true;
                return null;
            }
        }

        /// <summary>Case-insensitive Contains gate over Content/Source. Empty prefilter => always run.</summary>
        private bool PassesPrefilter(NuixLogEntry entry)
        {
            var literals = _definition.Prefilter;
            if (literals.Count == 0) { return true; }

            string content = entry.Content?.ToLowerInvariant();
            string source = entry.Source?.ToLowerInvariant();
            for (int i = 0; i < literals.Count; i++)
            {
                string lit = literals[i];
                if ((content != null && content.Contains(lit)) ||
                    (source != null && source.Contains(lit)))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Builds the read-only object handed to the script: only primitives (strings/numbers), no CLR
        /// objects or methods, so there's nothing to pivot into the host from. TimeStamp is an ISO-8601
        /// string; elapsed is milliseconds.
        /// </summary>
        private JsValue BuildEntryView(NuixLogEntry entry)
        {
            var view = new JsObject(_engine);
            view.FastSetDataProperty("content", entry.Content ?? "");
            view.FastSetDataProperty("source", entry.Source ?? "");
            view.FastSetDataProperty("level", entry.Level ?? "");
            view.FastSetDataProperty("channel", entry.Channel ?? "");
            view.FastSetDataProperty("filePath", entry.FilePath ?? "");
            view.FastSetDataProperty("fileName", entry.FileName ?? "");
            view.FastSetDataProperty("timeStamp", entry.TimeStamp.ToString("o"));
            view.FastSetDataProperty("elapsedMs", entry.Elapsed.TotalMilliseconds);
            view.FastSetDataProperty("lineNumber", entry.LineNumber);
            view.PreventExtensions();
            return view;
        }

        /// <summary>
        /// Turns a classify() return value into normalized flag strings. Accepts a string or an array of
        /// strings; anything else yields nothing. Each flag is normalized like the built-ins: trimmed,
        /// lower-cased, non-alphanumeric runs collapsed to underscore. Empty results are dropped.
        /// </summary>
        internal static IEnumerable<string> NormalizeFlags(JsValue result)
        {
            if (result == null || result.IsNull() || result.IsUndefined())
            {
                return null;
            }

            var flags = new List<string>();
            if (result.IsString())
            {
                AddNormalized(flags, result.AsString());
            }
            else if (result.IsArray())
            {
                var arr = result.AsArray();
                for (uint i = 0; i < arr.Length; i++)
                {
                    var item = arr[i];
                    if (item != null && item.IsString()) { AddNormalized(flags, item.AsString()); }
                }
            }

            return flags.Count > 0 ? flags : null;
        }

        private static void AddNormalized(List<string> flags, string raw)
        {
            string norm = NormalizeFlag(raw);
            if (!string.IsNullOrEmpty(norm) && !flags.Contains(norm))
            {
                flags.Add(norm);
            }
        }

        /// <summary>
        /// Normalizes a flag to the same shape the built-ins emit: lower snake_case, alphanumerics kept,
        /// every other run collapsed to a single underscore, leading/trailing underscores trimmed.
        /// </summary>
        internal static string NormalizeFlag(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) { return null; }

            var sb = new StringBuilder(raw.Length);
            bool lastUnderscore = false;
            foreach (char c in raw.Trim().ToLowerInvariant())
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
                {
                    sb.Append(c);
                    lastUnderscore = false;
                }
                else if (!lastUnderscore)
                {
                    sb.Append('_');
                    lastUnderscore = true;
                }
            }

            return sb.ToString().Trim('_');
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Acornima.Ast;
using Jint;

namespace NuixLogReviewer.LogRepository.Classifiers.Scripting
{
    /// <summary>
    /// A single scripted classifier loaded from a <c>.js</c> file, parsed and prepared ONCE at startup
    /// and then shared (read-only) across all per-worker engines. The script is expected to evaluate to
    /// an object literal exposing:
    /// <list type="bullet">
    /// <item><c>classify(entry)</c> — a pure function returning a flag string, an array of flag strings,
    /// or null/undefined for "no flags". This is the required member.</item>
    /// <item><c>prefilter</c> (optional) — a string or array of strings. If present, the host only calls
    /// <c>classify</c> for entries whose Content or Source contains at least one of these literals
    /// (case-insensitive). This keeps the load fast: Jint is entered only for candidate lines.</item>
    /// <item><c>name</c> (optional) — a human label for diagnostics; defaults to the file name.</item>
    /// </list>
    /// The prepared script is thread-safe to share; each worker builds its own <see cref="Jint.Engine"/>
    /// from it (Jint engines are single-threaded). See <see cref="ScriptedClassifier"/>.
    /// </summary>
    public sealed class ScriptClassifierDefinition
    {
        /// <summary>Source file path (for diagnostics).</summary>
        public string SourcePath { get; }

        /// <summary>Display name (script-provided <c>name</c>, else the file name).</summary>
        public string Name { get; }

        /// <summary>The raw script source. Each engine evaluates this once to obtain its classify function.</summary>
        public string Source { get; }

        /// <summary>
        /// Lower-cased literals for the cheap host-side prefilter, or empty for "always run classify".
        /// Checked against Content/Source in C# before entering the engine.
        /// </summary>
        public IReadOnlyList<string> Prefilter { get; }

        /// <summary>
        /// Optional flag-&gt;description text declared by the script, for the Classifiers tab. Built from
        /// the script's <c>descriptions</c> object map and/or a single <c>description</c> string. Keys
        /// are normalized flag names; empty when the script declares none.
        /// </summary>
        public IReadOnlyDictionary<string, string> Descriptions { get; }

        private ScriptClassifierDefinition(string sourcePath, string name, string source,
            IReadOnlyList<string> prefilter, IReadOnlyDictionary<string, string> descriptions)
        {
            SourcePath = sourcePath;
            Name = name;
            Source = source;
            Prefilter = prefilter;
            Descriptions = descriptions;
        }

        /// <summary>
        /// Parses and validates a script file into a definition. Uses a throwaway sandboxed engine to
        /// read the static metadata (name/prefilter) and to confirm a <c>classify</c> function exists.
        /// Throws <see cref="ScriptClassifierException"/> on any problem so the loader can log-and-skip.
        /// </summary>
        public static ScriptClassifierDefinition Load(string sourcePath, string source)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                throw new ScriptClassifierException(sourcePath, "Script file is empty.");
            }

            // Sanity-parse + read metadata in a fully sandboxed engine. If the script has side effects
            // at module scope they run here in isolation (no BCL/IO), never during the load hot path.
            Engine probe = ScriptedClassifier.CreateSandboxedEngine();
            Jint.Native.JsValue module;
            try
            {
                module = probe.Evaluate(source);
            }
            catch (Exception ex)
            {
                throw new ScriptClassifierException(sourcePath, "Failed to evaluate script: " + ex.Message, ex);
            }

            if (module == null || !module.IsObject())
            {
                throw new ScriptClassifierException(sourcePath,
                    "Script must evaluate to an object with a classify(entry) function, e.g. ({ classify: function(e){...} }).");
            }

            var obj = module.AsObject();
            var classify = obj.Get("classify");
            if (classify == null || !classify.IsCallable())
            {
                throw new ScriptClassifierException(sourcePath, "Script object has no callable 'classify' member.");
            }

            string name = System.IO.Path.GetFileNameWithoutExtension(sourcePath);
            var nameVal = obj.Get("name");
            if (nameVal != null && nameVal.IsString() && !string.IsNullOrWhiteSpace(nameVal.AsString()))
            {
                name = nameVal.AsString().Trim();
            }

            var prefilter = ReadPrefilter(obj.Get("prefilter"));
            var descriptions = ReadDescriptions(obj);

            return new ScriptClassifierDefinition(sourcePath, name, source, prefilter, descriptions);
        }

        /// <summary>
        /// Reads flag descriptions from the script's optional <c>descriptions</c> member: an object map
        /// of flag name =&gt; description text, surfaced in the Classifiers tab. Keys are normalized to the
        /// same flag shape the runtime emits; empty/non-string values are ignored. Returns an empty map
        /// when the script declares none.
        /// </summary>
        private static IReadOnlyDictionary<string, string> ReadDescriptions(Jint.Native.Object.ObjectInstance obj)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var descriptionsVal = obj.Get("descriptions");
            if (descriptionsVal != null && descriptionsVal.IsObject() && !descriptionsVal.IsArray())
            {
                var descObj = descriptionsVal.AsObject();
                foreach (var prop in descObj.GetOwnProperties())
                {
                    string flag = ScriptedClassifier.NormalizeFlag(prop.Key.ToString());
                    var val = prop.Value?.Value;
                    if (!string.IsNullOrEmpty(flag) && val != null && val.IsString())
                    {
                        string text = val.AsString().Trim();
                        if (text.Length > 0) { map[flag] = text; }
                    }
                }
            }

            return map;
        }

        /// <summary>Reads the optional prefilter (string or array of strings) into lower-cased literals.</summary>
        private static IReadOnlyList<string> ReadPrefilter(Jint.Native.JsValue value)
        {
            var result = new List<string>();
            if (value == null || value.IsUndefined() || value.IsNull())
            {
                return result;
            }

            if (value.IsString())
            {
                AddLiteral(result, value.AsString());
            }
            else if (value.IsArray())
            {
                var arr = value.AsArray();
                for (uint i = 0; i < arr.Length; i++)
                {
                    var item = arr[i];
                    if (item != null && item.IsString()) { AddLiteral(result, item.AsString()); }
                }
            }
            return result;
        }

        private static void AddLiteral(List<string> list, string literal)
        {
            if (!string.IsNullOrEmpty(literal))
            {
                list.Add(literal.ToLowerInvariant());
            }
        }
    }

    /// <summary>Raised when a script classifier cannot be loaded or prepared. The loader logs and skips.</summary>
    public sealed class ScriptClassifierException : Exception
    {
        public string SourcePath { get; }

        public ScriptClassifierException(string sourcePath, string message, Exception inner = null)
            : base(message, inner)
        {
            SourcePath = sourcePath;
        }
    }
}

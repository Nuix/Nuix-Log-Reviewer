using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace NuixLogReviewer.LogRepository.Classifiers.Scripting.Diagnostics
{
    /// <summary>
    /// Collects non-fatal script problems (parse failures, runtime errors, disabled scripts) during a
    /// load. Deduplicated so a bad script that hits millions of entries reports once, not per entry.
    /// Thread-safe because classification runs across parallel workers. The UI can read
    /// <see cref="Drain"/> after a load to surface a summary; nothing here ever aborts a load.
    /// </summary>
    public static class ScriptDiagnostics
    {
        private static readonly ConcurrentDictionary<string, byte> Seen = new ConcurrentDictionary<string, byte>();
        private static readonly ConcurrentQueue<string> Messages = new ConcurrentQueue<string>();

        /// <summary>Records a message once per (sourcePath + message) pair.</summary>
        public static void ReportOnce(string sourcePath, string message)
        {
            string key = (sourcePath ?? "?") + "\u0001" + message;
            if (Seen.TryAdd(key, 0))
            {
                Messages.Enqueue(string.Format("[{0}] {1}", System.IO.Path.GetFileName(sourcePath ?? "?"), message));
            }
        }

        /// <summary>Returns and clears the accumulated messages (e.g. to show after a load).</summary>
        public static IReadOnlyList<string> Drain()
        {
            var list = new List<string>();
            while (Messages.TryDequeue(out var m)) { list.Add(m); }
            return list;
        }

        /// <summary>Resets dedupe state, e.g. at the start of a fresh load.</summary>
        public static void Reset()
        {
            Seen.Clear();
            while (Messages.TryDequeue(out _)) { }
        }
    }
}

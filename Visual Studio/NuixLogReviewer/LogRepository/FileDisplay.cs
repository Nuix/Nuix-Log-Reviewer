using System;
using System.Collections.Generic;
using System.Linq;

namespace NuixLogReviewer.LogRepository
{
    /// <summary>
    /// Helpers for presenting and querying source files in the Files tab:
    /// <list type="bullet">
    /// <item>Computing a short, disambiguated display name for each path (the shortest trailing run of
    /// path segments that is unique across the loaded set), since many worker logs are all named
    /// <c>nuix.log</c> and the bare file name is ambiguous.</item>
    /// <item>Building escaped Lucene queries against the indexed <c>file</c> field for drill-down
    /// (<c>file:"&lt;path&gt;"</c>) and hiding (<c>NOT (file:"a" OR file:"b")</c>).</item>
    /// </list>
    /// </summary>
    public static class FileDisplay
    {
        /// <summary>
        /// Returns a map of full path =&gt; shortest-unique tail display name across <paramref name="paths"/>.
        /// The tail is built from whole path segments (split on / and \), growing from the file name
        /// backwards until unique; if even the full path repeats (it shouldn't), the full path is used.
        /// Comparison is case-insensitive to match the lower-cased index keys.
        /// </summary>
        public static Dictionary<string, string> ShortUniqueNames(IEnumerable<string> paths)
        {
            var list = paths?.Where(p => !string.IsNullOrEmpty(p)).Distinct().ToList() ?? new List<string>();
            var result = new Dictionary<string, string>();

            // Pre-split each path into segments (drop empties from leading/trailing separators).
            var segments = list.ToDictionary(
                p => p,
                p => p.Split('/', '\\').Where(s => s.Length > 0).ToArray());

            foreach (var path in list)
            {
                var segs = segments[path];
                if (segs.Length == 0) { result[path] = path; continue; }

                string chosen = null;
                for (int take = 1; take <= segs.Length; take++)
                {
                    string tail = string.Join("/", segs.Skip(segs.Length - take));
                    // Unique if no OTHER path yields the same tail at this depth.
                    bool unique = true;
                    foreach (var other in list)
                    {
                        if (ReferenceEquals(other, path) || other == path) { continue; }
                        var oSegs = segments[other];
                        if (oSegs.Length < take) { continue; }
                        string oTail = string.Join("/", oSegs.Skip(oSegs.Length - take));
                        if (string.Equals(oTail, tail, StringComparison.OrdinalIgnoreCase)) { unique = false; break; }
                    }
                    if (unique) { chosen = tail; break; }
                }

                result[path] = chosen ?? path;
            }

            return result;
        }

        /// <summary>
        /// Escapes a value for use inside a Lucene quoted phrase: backslash and double-quote are the
        /// characters that would otherwise break out of / escape within the quotes.
        /// </summary>
        public static string EscapePhrase(string value)
        {
            if (string.IsNullOrEmpty(value)) { return ""; }
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        /// <summary>A drill-down query matching exactly one file: <c>file:"&lt;escaped lowercased path&gt;"</c>.</summary>
        public static string DrillDownQuery(string fullPath)
        {
            return "file:\"" + EscapePhrase((fullPath ?? "").ToLowerInvariant()) + "\"";
        }

        /// <summary>
        /// A hide clause for the given files: <c>NOT (file:"a" OR file:"b" ...)</c>, or "" when none.
        /// Paths are lower-cased to match the index and phrase-escaped.
        /// </summary>
        public static string HideClause(IEnumerable<string> hiddenPaths)
        {
            var terms = (hiddenPaths ?? Enumerable.Empty<string>())
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(p => "file:\"" + EscapePhrase(p.ToLowerInvariant()) + "\"")
                .ToList();
            if (terms.Count == 0) { return ""; }
            return "NOT (" + string.Join(" OR ", terms) + ")";
        }
    }
}

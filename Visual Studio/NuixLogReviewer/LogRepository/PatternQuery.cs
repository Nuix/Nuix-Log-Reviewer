using System.Collections.Generic;
using System.Linq;

namespace NuixLogReviewer.LogRepository
{
    /// <summary>
    /// Builds escaped Lucene queries against the indexed <c>tmpl</c> field (the masked message
    /// template) for the Patterns view: drill-down (<c>tmpl:"&lt;template&gt;"</c>) and per-pattern
    /// hiding (<c>NOT (tmpl:"a" OR tmpl:"b")</c>). Templates are lower-cased to match the index key and
    /// phrase-escaped (they contain spaces and mask tokens like &lt;GUID&gt;/&lt;PATH&gt;). Mirrors
    /// <see cref="FileDisplay"/>'s query helpers.
    /// </summary>
    public static class PatternQuery
    {
        /// <summary>A drill-down query matching entries whose masked template equals the given one.</summary>
        public static string DrillDownQuery(string template)
        {
            return "tmpl:\"" + FileDisplay.EscapePhrase((template ?? "").ToLowerInvariant()) + "\"";
        }

        /// <summary>
        /// A hide clause for the given templates: <c>NOT (tmpl:"a" OR tmpl:"b" ...)</c>, or "" when none.
        /// </summary>
        public static string HideClause(IEnumerable<string> hiddenTemplates)
        {
            var terms = (hiddenTemplates ?? Enumerable.Empty<string>())
                .Where(t => !string.IsNullOrEmpty(t))
                .Select(t => "tmpl:\"" + FileDisplay.EscapePhrase(t.ToLowerInvariant()) + "\"")
                .ToList();
            if (terms.Count == 0) { return ""; }
            return "NOT (" + string.Join(" OR ", terms) + ")";
        }
    }
}

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
        /// A drill-down query for a (possibly folded) pattern that may cover several regex-templates:
        /// <c>(tmpl:"a" OR tmpl:"b" ...)</c>. Falls back to a single-template query when there's one
        /// member. Used so a Drain-collapsed pattern drills into ALL the lines it represents.
        /// </summary>
        public static string DrillDownQuery(IEnumerable<string> templates)
        {
            var terms = (templates ?? Enumerable.Empty<string>())
                .Where(t => !string.IsNullOrEmpty(t))
                .Select(t => "tmpl:\"" + FileDisplay.EscapePhrase(t.ToLowerInvariant()) + "\"")
                .ToList();
            if (terms.Count == 0) { return ""; }
            if (terms.Count == 1) { return terms[0]; }
            return "(" + string.Join(" OR ", terms) + ")";
        }

        // NOTE: pattern hiding no longer builds a NOT (tmpl:"a" OR tmpl:"b" ...) query string. Folding
        // hundreds of template phrases into one boolean made the classic QueryParser pathologically slow
        // (seconds per pass, and it froze the UI on a chart click). Hidden templates are now applied as a
        // single FieldCacheTermsFilter MUST_NOT over the tmpl field - see LogSearchIndex.ComposeFilteredQuery.
    }
}

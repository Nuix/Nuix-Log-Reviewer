using System;
using System.Collections.Generic;
using System.Linq;

namespace NuixLogReviewer.LogRepository
{
    /// <summary>
    /// Second-stage log-template mining (Drain-style, depth-bounded parse tree) that runs AFTER the
    /// coarse regex <see cref="LogPatternMasker"/> pass. Its job is to collapse regex-templates that are
    /// near-duplicates (differ only in a few residual variable tokens the regexes didn't catch) into a
    /// single normalized template, replacing the differing token positions with a wildcard.
    ///
    /// Why a separate stage, and why it's deterministic here:
    ///   • The regex masker runs inline at index time (concurrent, per-entry) and produces the stable,
    ///     queryable <c>tmpl</c> string. Drain is stateful/order-sensitive, so it must NOT run there.
    ///   • Instead we build a Drain over the small set of DISTINCT regex-templates once, after indexing.
    ///     Feeding distinct templates (each seen exactly once, in a fixed sorted order) makes the
    ///     resulting cluster set independent of log ingest order — the property the concurrent load
    ///     pipeline needs. The output is a <c>regexTemplate → drainTemplate</c> map the Patterns view
    ///     groups by (and hide/drill-down expands through), so the Lucene index is never rewritten.
    ///
    /// Wildcard token is <c>&lt;*&gt;</c>, distinct from the regex masker's named tokens (&lt;ID&gt;, &lt;PATH&gt;, ...)
    /// so the two stages remain visually distinguishable in a template.
    /// </summary>
    public sealed class DrainTemplateMiner
    {
        public const string Wildcard = "<*>";

        private readonly double _similarityThreshold;
        private readonly int _maxClustersPerLeaf;
        private readonly Node _root = new Node();

        /// <param name="similarityThreshold">
        /// Fraction of matching token positions required to join a cluster (0..1). 0.5–0.7 is typical;
        /// higher = more conservative (fewer merges). Default 0.6.
        /// </param>
        /// <param name="maxClustersPerLeaf">Safety cap on clusters under one leaf (prevents blowup).</param>
        public DrainTemplateMiner(double similarityThreshold = 0.6, int maxClustersPerLeaf = 1000)
        {
            if (similarityThreshold < 0 || similarityThreshold > 1)
                throw new ArgumentOutOfRangeException(nameof(similarityThreshold));
            _similarityThreshold = similarityThreshold;
            _maxClustersPerLeaf = Math.Max(1, maxClustersPerLeaf);
        }

        /// <summary>
        /// Builds a Drain model from a set of regex-templates and returns the map
        /// <c>regexTemplate → drainTemplate</c>. Input is de-duplicated and processed in a fixed
        /// (ordinal) order so the result is deterministic regardless of the caller's enumeration order.
        /// Empty/null templates map to themselves.
        /// </summary>
        public static IReadOnlyDictionary<string, string> BuildMap(
            IEnumerable<string> regexTemplates,
            double similarityThreshold = 0.6)
        {
            var miner = new DrainTemplateMiner(similarityThreshold);

            // Distinct + fixed order => order-independent clustering over the distinct set.
            var distinct = (regexTemplates ?? Enumerable.Empty<string>())
                .Where(t => !string.IsNullOrEmpty(t))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(t => t, StringComparer.Ordinal)
                .ToList();

            var clusterOf = new Dictionary<string, LogCluster>(StringComparer.Ordinal);
            foreach (var t in distinct)
            {
                clusterOf[t] = miner.Add(t);
            }

            // After all merges settle, each regex-template maps to its cluster's FINAL template.
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in clusterOf)
            {
                map[kv.Key] = kv.Value.Template();
            }
            return map;
        }

        /// <summary>Adds one regex-template to the tree, returning the cluster it joined/created.</summary>
        private LogCluster Add(string template)
        {
            var tokens = template.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
            {
                // Degenerate: represent as a single empty-token cluster keyed under length 0.
                var leaf0 = _root.GetOrAdd("0").GetOrAdd("<empty>");
                var c0 = leaf0.Clusters.FirstOrDefault();
                if (c0 == null) { c0 = new LogCluster(new[] { "" }); leaf0.Clusters.Add(c0); }
                return c0;
            }

            // Depth 1: token count. Depth 2: first token (or wildcard for single-token lines).
            var lenNode = _root.GetOrAdd(tokens.Length.ToString());
            string firstKey = tokens.Length > 1 ? tokens[0] : Wildcard;
            var leaf = lenNode.GetOrAdd(firstKey);

            LogCluster best = null;
            double bestSim = 0;
            foreach (var c in leaf.Clusters)
            {
                // Length is guaranteed equal by the length route, so index-aligned compare is safe.
                double sim = Similarity(c.Tokens, tokens);
                if (sim >= _similarityThreshold && sim > bestSim)
                {
                    bestSim = sim;
                    best = c;
                }
            }

            if (best != null)
            {
                for (int i = 0; i < tokens.Length; i++)
                {
                    if (best.Tokens[i] != tokens[i]) best.Tokens[i] = Wildcard;
                }
                return best;
            }

            var created = new LogCluster(tokens);
            if (leaf.Clusters.Count < _maxClustersPerLeaf)
            {
                leaf.Clusters.Add(created);
            }
            return created;
        }

        private static double Similarity(IReadOnlyList<string> template, string[] tokens)
        {
            int matches = 0;
            for (int i = 0; i < tokens.Length; i++)
            {
                if (template[i] == Wildcard || template[i] == tokens[i]) matches++;
            }
            return (double)matches / tokens.Length;
        }

        private sealed class LogCluster
        {
            public readonly List<string> Tokens;
            public LogCluster(string[] tokens) { Tokens = new List<string>(tokens); }
            public string Template() => string.Join(" ", Tokens);
        }

        private sealed class Node
        {
            public readonly Dictionary<string, Node> Children = new Dictionary<string, Node>(StringComparer.Ordinal);
            public readonly List<LogCluster> Clusters = new List<LogCluster>();
            public Node GetOrAdd(string key)
            {
                if (!Children.TryGetValue(key, out var n)) { n = new Node(); Children[key] = n; }
                return n;
            }
        }
    }
}
